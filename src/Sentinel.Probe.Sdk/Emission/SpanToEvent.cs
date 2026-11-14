using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Emet.Sentinel.Model.V1;
using Google.Protobuf;
using Sentinel.Probe.Sdk.Int128;
using Sentinel.Probe.Sdk.Otlp;
using Sentinel.Probe.Sdk.Proto;

// Span-to-event conversion — the .NET analog of sdk/go/emission/spantoevent.go
// (mirrors span-to-event.ts).
//
// S1 fix: .NET's Activity.Tags pre-stringifies every value to a string. We read
// Activity.TagObjects (IEnumerable<KeyValuePair<string,object?>>), which preserves the original
// runtime types the producer set — the type-directed attribute mapping depends on this. For link
// attributes, ActivityLink.Tags is already object-typed (there is no stringified variant), so it
// is used directly.

/// <summary>Reserved attribute keys carrying causal-edge metadata, never emitted as attributes.</summary>
public static class ReservedAttributes
{
    public const string AttributeEventId = "sentinel.event.id";
    public const string AttributeParentEventID = "sentinel.parent.event.id";

    /// <summary>Malformed-link sources reported through SpanConversion.OnMalformedLink.</summary>
    public const string MalformedLinkSourceParent = "parent";
    public const string MalformedLinkSourceLink = "link";
}

/// <summary>
/// Everything SpanToEvent needs beyond the activity itself. Named SpanConversion rather than
/// SpanContext (the TypeScript name) because ActivitySpanId appears in this conversion and
/// shadowing it would be confusing. No parameter here is named ctx: in .NET that means
/// CancellationToken, which the enforcement gate genuinely takes.
/// </summary>
public sealed class SpanConversion
{
    /// <summary>The ended activity to convert.</summary>
    public required Activity Activity { get; init; }

    /// <summary>The host-assigned sequence coordinate. Sequence assignment is out of SDK scope.</summary>
    public SequenceCoordinate? Sequence { get; init; }

    /// <summary>The schema version of the emitted event.</summary>
    public string? SchemaVersion { get; init; }

    /// <summary>
    /// The filter epoch this Probe has acknowledged, typically ProbeClient.AcknowledgedEpoch().
    /// null means none is held; it does not mean 0.
    /// </summary>
    public ulong? AcknowledgedEpoch { get; init; }

    /// <summary>
    /// The capabilities the source asserts. Claims only: the receiver owns effective capabilities,
    /// tier, integrity and observation time by construction (event.proto:32-33).
    /// </summary>
    public List<SourceCapability> ClaimedCapabilities { get; init; } = new();

    /// <summary>The claimed sensitivity of the event.</summary>
    public Sensitivity ClaimedSensitivity { get; init; } = Sensitivity.Unspecified;

    /// <summary>The reserved sentinel.event.id the producer assigned at span start.</summary>
    public string? EventId { get; init; }

    /// <summary>
    /// Retained so existing callers still compile. A missing predecessor event id fails the
    /// export. The span id is not passed here and is not stored as causality.
    /// </summary>
    public Action<string, string>? OnMalformedLink { get; init; }

    /// <summary>
    /// When true, an activity whose REMOTE parent carries no <c>sentinel.parent.event.id</c> (the
    /// caller is not a Probe: a browser, curl, an uninstrumented service) converts as a causal
    /// root with no predecessor. A local parent without the id is still refused. Default false.
    /// </summary>
    public bool AllowUnattributedRemoteParent { get; init; }
}

public static class SpanToEventConverter
{
    /// <summary>
    /// Converts an ended OTel activity into a ProducerEvent. The event's kind is the activity's
    /// display name, its occurrence time is the activity's start time, its attributes are the
    /// activity's tags minus the two reserved keys, and its causal predecessors come from the
    /// parent and link tags described in the package doc.
    /// </summary>
    public static ProducerEvent SpanToEvent(SpanConversion conversion)
    {
        var activity = conversion.Activity;
        var @event = new ProducerEvent
        {
            Id = conversion.EventId ?? "",
            Sequence = conversion.Sequence,
            SchemaVersion = conversion.SchemaVersion ?? "",
            Kind = activity.DisplayName,
            OccurrenceTime = BuildOccurrenceTime(activity),
            Attributes = { MapAttributes(activity.TagObjects) },
            ClaimedCapabilities = { conversion.ClaimedCapabilities },
            ClaimedSensitivity = conversion.ClaimedSensitivity,
            CausalPredecessorIds = { CollectCausalPredecessors(activity, conversion.AllowUnattributedRemoteParent) },
        };
        // proto3 `optional`: a null acknowledged epoch stays absent rather than becoming 0.
        @event.SetAcknowledgedFilterEpoch(conversion.AcknowledgedEpoch);
        return @event;
    }

    /// <summary>
    /// Converts an activity's start time into an OccurrenceTime in the "unix" clock domain.
    /// uncertainty_nanoseconds is always 0, mirroring the reference exactly.
    /// </summary>
    public static OccurrenceTime BuildOccurrenceTime(Activity activity)
    {
        return new OccurrenceTime
        {
            ClockDomainId = "unix",
            Nanoseconds = Int128Codec.FromBigInteger(
                Int128Codec.TimeToNanoseconds(new DateTimeOffset(activity.StartTimeUtc))),
            UncertaintyNanoseconds = 0,
        };
    }

    private static List<AttributeEntry> MapAttributes(IEnumerable<KeyValuePair<string, object?>>? tags)
    {
        var entries = new List<AttributeEntry>();
        if (tags == null) return entries;
        foreach (var kv in tags)
        {
            string key = kv.Key;
            if (key == ReservedAttributes.AttributeEventId || key == ReservedAttributes.AttributeParentEventID)
            {
                continue; // reserved: causal-edge metadata, not a domain attribute
            }
            AttributeValue? value = MapValue(kv.Value);
            if (value == null) continue;
            entries.Add(new AttributeEntry { Key = key, Value = value });
        }
        return entries;
    }

    /// <summary>
    /// Maps one attribute value onto the AttributeValue oneof, dispatched on its RUNTIME TYPE
    /// (D14: type-directed, so a double 3.0 stays a double). Returns null for null/unknown, like
    /// the reference skipping null/undefined.
    /// </summary>
    private static AttributeValue? MapValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return new AttributeValue { StringValue = s };
            case bool b:
                return new AttributeValue { BoolValue = b };
            case long l:
                return new AttributeValue { IntegerValue = l };
            case int i:
                return new AttributeValue { IntegerValue = i };
            case double d:
                return new AttributeValue { DoubleValue = d };
            case float f:
                return new AttributeValue { DoubleValue = f };
            case byte[] bytes:
                return new AttributeValue { BytesValue = ByteString.CopyFrom(bytes) };
            case Array array:
                return ArrayValue(array);
            case IDictionary<string, object?> dict:
                return MapValueDict(dict);
            default:
                // An unmappable type is skipped rather than guessed at, because inventing a mapping
                // would put data in the wrong oneof arm.
                return null;
        }
    }

    private static AttributeValue ArrayValue(Array array)
    {
        var values = new List<AttributeValue>();
        foreach (var item in array)
        {
            var mapped = MapValue(item);
            if (mapped == null) continue; // skip empties, as the reference skips null members
            values.Add(mapped);
        }
        return new AttributeValue
        {
            ArrayValue = new AttributeArray { Values = { values } },
        };
    }

    private static AttributeValue MapValueDict(IDictionary<string, object?> dict)
    {
        var entries = new List<AttributeEntry>();
        foreach (var kv in dict)
        {
            var mapped = MapValue(kv.Value);
            if (mapped == null) continue;
            entries.Add(new AttributeEntry { Key = kv.Key, Value = mapped });
        }
        return new AttributeValue
        {
            MapValue = new AttributeMap { Entries = { entries } },
        };
    }

    /// <summary>
    /// Reads the parent edge and every link edge, in that order. A parent span or a link
    /// without <c>sentinel.event.id</c> fails the export. The span id is not a predecessor.
    /// </summary>
    private static List<string> CollectCausalPredecessors(Activity activity, bool allowUnattributedRemoteParent)
    {
        var predecessors = new List<string>();

        // Parent: the child span carries the parent's event ID under the reserved key.
        if (StringAttribute(activity.TagObjects, ReservedAttributes.AttributeParentEventID, out string parentEventId))
        {
            predecessors.Add(parentEventId);
        }
        else if (activity.ParentSpanId != default && !IsUnattributedRemoteParentAllowed(activity, allowUnattributedRemoteParent))
        {
            throw new OtlpExportException("parent span is missing sentinel.parent.event.id");
        }

        // Links: each link carries its target's event ID in that link's OWN tags.
        foreach (var link in activity.Links)
        {
            if (StringAttribute(link.Tags, ReservedAttributes.AttributeEventId, out string linkEventId))
            {
                predecessors.Add(linkEventId);
                continue;
            }
            throw new OtlpExportException("link is missing sentinel.event.id");
        }

        return predecessors;
    }

    /// <summary>
    /// True when <paramref name="allow"/> is set and the activity's parent is remote, so a missing
    /// <c>sentinel.parent.event.id</c> means "the caller is not a Probe" rather than a contract bug.
    /// </summary>
    internal static bool IsUnattributedRemoteParentAllowed(Activity activity, bool allow) =>
        allow && activity.HasRemoteParent;

    /// <summary>
    /// Returns the value of key when it is present AND holds a string. A non-string value under a
    /// reserved key is treated as absent, matching the reference's typeof guard, so a mistyped
    /// attribute takes the malformed-link path rather than silently producing a garbage event ID.
    /// </summary>
    private static bool StringAttribute(IEnumerable<KeyValuePair<string, object?>>? tags, string key, out string value)
    {
        if (tags != null)
        {
            foreach (var kv in tags)
            {
                if (!string.Equals(kv.Key, key, StringComparison.Ordinal)) continue;
                if (kv.Value is string s)
                {
                    value = s;
                    return true;
                }
                break;
            }
        }
        value = string.Empty;
        return false;
    }
}
