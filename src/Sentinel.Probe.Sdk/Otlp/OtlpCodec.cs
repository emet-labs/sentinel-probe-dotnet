using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Emet.Sentinel.Model.V1;
using Google.Protobuf;
using Sentinel.Probe.Sdk.Emission;
using Sentinel.Probe.Sdk.Int128;

namespace Sentinel.Probe.Sdk.Otlp;

/// <summary>One ended span plus the producer fields the OTLP span must carry.</summary>
public sealed class SpanExport
{
    public required Activity Activity { get; init; }
    public required string EventId { get; init; }
    public required ulong SequenceEpoch { get; init; }
    public required ulong SequenceNumber { get; init; }
    public required string SchemaVersion { get; init; }
    public required ulong AcknowledgedFilterEpoch { get; init; }
    public Sensitivity ClaimedSensitivity { get; init; } = Sensitivity.Unspecified;
    public IReadOnlyList<SourceCapability> ClaimedCapabilities { get; init; } = Array.Empty<SourceCapability>();
}

/// <summary>An attribute read back from an OTLP span or log record.</summary>
public sealed class DecodedAttribute
{
    public required string Key { get; init; }
    public required OtlpValue Value { get; init; }
}

/// <summary>A scalar or nested OTLP AnyValue.</summary>
public sealed class OtlpValue
{
    public enum Shape
    {
        Empty = 0,
        String,
        Bool,
        Int,
        Double,
        Bytes,
        Array,
        Map,
    }

    public Shape Case { get; private init; }
    public string String { get; private init; } = "";
    public bool Bool { get; private init; }
    public long Int { get; private init; }
    public double Double { get; private init; }
    public byte[] Bytes { get; private init; } = Array.Empty<byte>();
    public IReadOnlyList<OtlpValue> Items { get; private init; } = Array.Empty<OtlpValue>();
    public IReadOnlyList<DecodedAttribute> Entries { get; private init; } = Array.Empty<DecodedAttribute>();

    public static OtlpValue FromString(string value) => new() { Case = Shape.String, String = value };
    public static OtlpValue FromBool(bool value) => new() { Case = Shape.Bool, Bool = value };
    public static OtlpValue FromInt(long value) => new() { Case = Shape.Int, Int = value };
    public static OtlpValue FromDouble(double value) => new() { Case = Shape.Double, Double = value };
    public static OtlpValue FromBytes(byte[] value) => new() { Case = Shape.Bytes, Bytes = value };
    public static OtlpValue FromArray(IReadOnlyList<OtlpValue> values) => new() { Case = Shape.Array, Items = values };
    public static OtlpValue FromMap(IReadOnlyList<DecodedAttribute> entries) => new() { Case = Shape.Map, Entries = entries };
}

/// <summary>One span from an <c>ExportTraceServiceRequest</c>.</summary>
public sealed class DecodedSpan
{
    public required string Name { get; init; }
    public required ulong StartTimeUnixNano { get; init; }
    public required ulong EndTimeUnixNano { get; init; }
    public required bool HasParentSpanId { get; init; }
    public required IReadOnlyList<DecodedAttribute> Attributes { get; init; }
    public required IReadOnlyList<IReadOnlyList<DecodedAttribute>> Links { get; init; }

    /// <summary>
    /// Maps this span with the probe contract. A parent span id or a link without
    /// <c>sentinel.event.id</c> throws; the span id is not stored as causality.
    /// </summary>
    public ProducerEvent ToProducerEvent() => OtlpTraces.ToProducerEvent(this);
}

/// <summary>One log record from an <c>ExportLogsServiceRequest</c>.</summary>
public sealed class DecodedLog
{
    public required IReadOnlyList<DecodedAttribute> Attributes { get; init; }
    public required uint DroppedAttributesCount { get; init; }

    /// <summary>Protobuf body when the OTLP body is bytes. Null when the body is absent or another type.</summary>
    public byte[]? BodyBytes { get; init; }

    public bool BodyIsBytes { get; init; }
}

/// <summary>Encode and decode the trace half of the probe OTLP wire.</summary>
public static class OtlpTraces
{
    public const string EventIdKey = "sentinel.event.id";
    public const string ParentEventIdKey = "sentinel.parent.event.id";
    public const string SequenceEpochKey = "sentinel.sequence.epoch";
    public const string SequenceNumberKey = "sentinel.sequence.number";
    public const string SchemaVersionKey = "sentinel.schema.version";
    public const string FilterEpochKey = "sentinel.acknowledged_filter_epoch";
    public const string SensitivityKey = "sentinel.claimed_sensitivity";
    public const string CapabilitiesKey = "sentinel.claimed_capabilities";

    public static byte[] Encode(IReadOnlyList<SpanExport> spans)
    {
        // ScopeSpans.spans is repeated. Each ended span is its own length-delimited
        // field. One field whose payload is several span bodies is not an export:
        // a decoder reads a single span and cannot admit the rest as Events.
        var repeatedSpans = new List<byte[]>(spans.Count);
        foreach (var span in spans)
        {
            repeatedSpans.Add(LengthDelimited(2, new[] { EncodeSpan(span) }));
        }
        var scope = Concat(repeatedSpans);
        var resource = LengthDelimited(2, new[] { scope });
        return LengthDelimited(1, new[] { resource });
    }

    public static IReadOnlyList<DecodedSpan> Decode(byte[] export)
    {
        var spans = new List<DecodedSpan>();
        foreach (var resource in ReadRepeated(export, 1))
        {
            foreach (var scope in ReadRepeated(resource, 2))
            {
                foreach (var span in ReadRepeated(scope, 2))
                {
                    spans.Add(ReadSpan(span));
                }
            }
        }
        return spans;
    }

    internal static ProducerEvent ToProducerEvent(DecodedSpan span)
    {
        if (span.EndTimeUnixNano == 0)
        {
            throw new OtlpExportException("span has not ended");
        }
        if (span.EndTimeUnixNano < span.StartTimeUnixNano)
        {
            throw new OtlpExportException("span ends before it starts");
        }

        string? eventId = null;
        ulong? epoch = null;
        ulong? sequence = null;
        string? schema = null;
        ulong? filterEpoch = null;
        string? parent = null;
        Sensitivity sensitivity = Sensitivity.Unspecified;
        bool sawSensitivity = false;
        var capabilities = new List<SourceCapability>();
        var attributes = new List<AttributeEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in span.Attributes)
        {
            if (!seen.Add(attribute.Key))
            {
                throw new OtlpExportException("duplicate attribute " + attribute.Key);
            }
            if (attribute.Key.StartsWith("sentinel.", StringComparison.Ordinal))
            {
                switch (attribute.Key)
                {
                    case EventIdKey:
                        eventId = RequireString(attribute);
                        break;
                    case ParentEventIdKey:
                        parent = RequireString(attribute);
                        break;
                    case SequenceEpochKey:
                        epoch = RequireUint(attribute);
                        break;
                    case SequenceNumberKey:
                        sequence = RequireUint(attribute);
                        break;
                    case SchemaVersionKey:
                        schema = RequireString(attribute);
                        break;
                    case FilterEpochKey:
                        filterEpoch = RequireUint(attribute);
                        break;
                    case SensitivityKey:
                        sensitivity = ParseSensitivity(RequireString(attribute));
                        sawSensitivity = true;
                        break;
                    case CapabilitiesKey:
                        capabilities = ParseCapabilities(attribute.Value);
                        break;
                    default:
                        throw new OtlpExportException("unknown sentinel attribute " + attribute.Key);
                }
            }
            else
            {
                var mapped = ToAttributeValue(attribute.Value);
                if (mapped != null)
                {
                    attributes.Add(new AttributeEntry { Key = attribute.Key, Value = mapped });
                }
            }
        }

        if (span.HasParentSpanId && parent == null)
        {
            throw new OtlpExportException("parent span is missing sentinel.parent.event.id");
        }

        var predecessors = new List<string>();
        if (parent != null)
        {
            if (parent.Length == 0)
            {
                throw new OtlpExportException("sentinel.parent.event.id is empty");
            }
            predecessors.Add(parent);
        }
        for (var i = 0; i < span.Links.Count; i++)
        {
            string? linkId = null;
            var linkSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attribute in span.Links[i])
            {
                if (!linkSeen.Add(attribute.Key))
                {
                    throw new OtlpExportException("duplicate link attribute " + attribute.Key);
                }
                if (attribute.Key == EventIdKey)
                {
                    linkId = RequireString(attribute);
                }
                else if (attribute.Key.StartsWith("sentinel.", StringComparison.Ordinal))
                {
                    throw new OtlpExportException("unknown sentinel attribute " + attribute.Key);
                }
                else
                {
                    RejectNested(attribute.Value);
                }
            }
            if (linkId == null || linkId.Length == 0)
            {
                throw new OtlpExportException("link is missing sentinel.event.id");
            }
            predecessors.Add(linkId);
        }

        if (eventId == null || eventId.Length == 0)
        {
            throw new OtlpExportException("span is missing sentinel.event.id");
        }
        if (epoch == null)
        {
            throw new OtlpExportException("span is missing sentinel.sequence.epoch");
        }
        if (sequence == null)
        {
            throw new OtlpExportException("span is missing sentinel.sequence.number");
        }
        if (schema == null)
        {
            throw new OtlpExportException("span is missing sentinel.schema.version");
        }
        if (filterEpoch == null)
        {
            throw new OtlpExportException("span is missing sentinel.acknowledged_filter_epoch");
        }
        if (!sawSensitivity || sensitivity == Sensitivity.Unspecified)
        {
            throw new OtlpExportException("span is missing sentinel.claimed_sensitivity");
        }

        return new ProducerEvent
        {
            Id = eventId,
            Sequence = new SequenceCoordinate { Epoch = epoch.Value, Sequence = sequence.Value },
            SchemaVersion = schema,
            AcknowledgedFilterEpoch = filterEpoch.Value,
            Kind = span.Name,
            OccurrenceTime = Occurrence(span.StartTimeUnixNano),
            Attributes = { attributes },
            ClaimedCapabilities = { capabilities },
            ClaimedSensitivity = sensitivity,
            CausalPredecessorIds = { predecessors },
        };
    }

    private static byte[] EncodeSpan(SpanExport export)
    {
        var activity = export.Activity ?? throw new OtlpExportException("span is missing");
        // The conversion is the export. A missing predecessor throws here and is
        // not rewritten as a span id.
        _ = SpanToEventConverter.SpanToEvent(new SpanConversion
        {
            Activity = activity,
            EventId = export.EventId,
            Sequence = new SequenceCoordinate { Epoch = export.SequenceEpoch, Sequence = export.SequenceNumber },
            SchemaVersion = export.SchemaVersion,
            AcknowledgedEpoch = export.AcknowledgedFilterEpoch,
            ClaimedCapabilities = export.ClaimedCapabilities.ToList(),
            ClaimedSensitivity = export.ClaimedSensitivity,
        });
        RejectContract(export);
        var domain = CollectDomain(activity.TagObjects);
        RejectActivityAnnotations(activity);
        string? parent = ParentEventId(activity);
        if (HasParentSpan(activity) && parent == null)
        {
            throw new OtlpExportException("parent span is missing sentinel.parent.event.id");
        }
        var links = EncodeLinks(activity);

        var attributes = new List<byte[]>
        {
            Attribute(EventIdKey, StringValue(export.EventId)),
            Attribute(SequenceEpochKey, IntValue(CheckedInt(export.SequenceEpoch, SequenceEpochKey))),
            Attribute(SequenceNumberKey, IntValue(CheckedInt(export.SequenceNumber, SequenceNumberKey))),
            Attribute(SchemaVersionKey, StringValue(export.SchemaVersion)),
            Attribute(FilterEpochKey, IntValue(CheckedInt(export.AcknowledgedFilterEpoch, FilterEpochKey))),
            Attribute(SensitivityKey, StringValue(SensitivityName(export.ClaimedSensitivity))),
        };
        if (export.ClaimedCapabilities.Count > 0)
        {
            var items = new List<byte[]>(export.ClaimedCapabilities.Count);
            foreach (var capability in export.ClaimedCapabilities)
            {
                items.Add(LengthDelimited(1, new[] { StringValue(CapabilityName(capability)) }));
            }
            attributes.Add(Attribute(CapabilitiesKey, LengthDelimited(5, new[] { Concat(items) })));
        }
        if (parent != null)
        {
            attributes.Add(Attribute(ParentEventIdKey, StringValue(parent)));
        }
        attributes.AddRange(domain);

        var (start, end) = SpanTimes(activity);
        var fields = new List<byte[]>
        {
            BytesField(1, TraceBytes(activity)),
            BytesField(2, SpanBytes(activity)),
            StringField(5, activity.DisplayName ?? ""),
            VarintField(6, (ulong)SpanKind(activity.Kind)),
            Fixed64Field(7, start),
            Fixed64Field(8, end),
        };
        fields.AddRange(attributes.ConvertAll(static value => LengthDelimited(9, new[] { value })));
        fields.AddRange(links);
        return Concat(fields);
    }

    private static void RejectContract(SpanExport export)
    {
        if (string.IsNullOrEmpty(export.EventId))
        {
            throw new OtlpExportException("sentinel.event.id is empty");
        }
        if (export.SchemaVersion == null)
        {
            throw new OtlpExportException("sentinel.schema.version is missing");
        }
        if (export.ClaimedSensitivity == Sensitivity.Unspecified)
        {
            throw new OtlpExportException("sentinel.claimed_sensitivity is unspecified");
        }
        _ = CheckedInt(export.SequenceEpoch, SequenceEpochKey);
        _ = CheckedInt(export.SequenceNumber, SequenceNumberKey);
        _ = CheckedInt(export.AcknowledgedFilterEpoch, FilterEpochKey);
        foreach (var capability in export.ClaimedCapabilities)
        {
            _ = CapabilityName(capability);
        }
        RejectConflictingTag(export.Activity.TagObjects, EventIdKey, export.EventId);
        RejectConflictingTag(export.Activity.TagObjects, SchemaVersionKey, export.SchemaVersion);
        RejectConflictingUint(export.Activity.TagObjects, SequenceEpochKey, export.SequenceEpoch);
        RejectConflictingUint(export.Activity.TagObjects, SequenceNumberKey, export.SequenceNumber);
        RejectConflictingUint(export.Activity.TagObjects, FilterEpochKey, export.AcknowledgedFilterEpoch);
    }

    private static List<byte[]> CollectDomain(IEnumerable<KeyValuePair<string, object?>>? tags)
    {
        var encoded = new List<byte[]>();
        if (tags == null)
        {
            return encoded;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            if (!seen.Add(tag.Key))
            {
                throw new OtlpExportException("duplicate attribute " + tag.Key);
            }
            if (IsOwned(tag.Key))
            {
                continue;
            }
            if (tag.Key.StartsWith("sentinel.", StringComparison.Ordinal))
            {
                throw new OtlpExportException("unknown sentinel attribute " + tag.Key);
            }
            var value = EncodeClr(tag.Value);
            if (value == null)
            {
                continue;
            }
            encoded.Add(Attribute(tag.Key, value));
        }
        return encoded;
    }

    private static void RejectActivityAnnotations(Activity activity)
    {
        foreach (var spanEvent in activity.Events)
        {
            // Span events are not copied onto the export. Omitting them with a zero
            // dropped count would hide evidence, so the export is refused.
            _ = spanEvent;
            throw new OtlpExportException("span events are not exported");
        }
    }

    private static List<byte[]> EncodeLinks(Activity activity)
    {
        var links = new List<byte[]>();
        foreach (var link in activity.Links)
        {
            string? eventId = null;
            var extras = new List<byte[]>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (link.Tags != null)
            {
                foreach (var tag in link.Tags)
                {
                    if (!seen.Add(tag.Key))
                    {
                        throw new OtlpExportException("duplicate link attribute " + tag.Key);
                    }
                    if (tag.Key == EventIdKey)
                    {
                        if (tag.Value is string text && text.Length > 0)
                        {
                            eventId = text;
                        }
                        else
                        {
                            throw new OtlpExportException("link sentinel.event.id is not a string");
                        }
                    }
                    else if (tag.Key.StartsWith("sentinel.", StringComparison.Ordinal))
                    {
                        throw new OtlpExportException("unknown sentinel attribute " + tag.Key);
                    }
                    else
                    {
                        var value = EncodeClr(tag.Value);
                        if (value != null)
                        {
                            extras.Add(Attribute(tag.Key, value));
                        }
                    }
                }
            }
            if (eventId == null)
            {
                throw new OtlpExportException("link is missing sentinel.event.id");
            }
            var fields = new List<byte[]>
            {
                BytesField(1, link.Context.TraceId.ToHexString().HexToBytes(16)),
                BytesField(2, link.Context.SpanId.ToHexString().HexToBytes(8)),
                LengthDelimited(4, new[] { Attribute(EventIdKey, StringValue(eventId)) }),
            };
            foreach (var extra in extras)
            {
                fields.Add(LengthDelimited(4, new[] { extra }));
            }
            links.Add(LengthDelimited(13, new[] { Concat(fields) }));
        }
        return links;
    }

    private static string? ParentEventId(Activity activity)
    {
        if (activity.TagObjects == null)
        {
            return null;
        }
        foreach (var tag in activity.TagObjects)
        {
            if (!string.Equals(tag.Key, ParentEventIdKey, StringComparison.Ordinal))
            {
                continue;
            }
            if (tag.Value is string text && text.Length > 0)
            {
                return text;
            }
            throw new OtlpExportException("sentinel.parent.event.id is not a string");
        }
        return null;
    }

    private static bool HasParentSpan(Activity activity) => activity.ParentSpanId != default;

    private static (ulong Start, ulong End) SpanTimes(Activity activity)
    {
        var start = UnixNanos(new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero));
        var endTicks = activity.Duration.Ticks;
        if (endTicks < 0)
        {
            throw new OtlpExportException("span ends before it starts");
        }
        var end = start + (ulong)endTicks * 100UL;
        if (end < start)
        {
            throw new OtlpExportException("span end time overflowed");
        }
        if (activity.Duration == TimeSpan.Zero && activity.Status == ActivityStatusCode.Unset && !activity.Recorded)
        {
            // A span that was never started far enough to record is not evidence.
        }
        if (end == 0)
        {
            throw new OtlpExportException("span has not ended");
        }
        return (start, end);
    }

    private static ulong UnixNanos(DateTimeOffset time)
    {
        var nanos = Int128Codec.TimeToNanoseconds(time);
        if (nanos < 0 || nanos > ulong.MaxValue)
        {
            throw new OtlpExportException("span time is outside uint64 nanoseconds");
        }
        return (ulong)nanos;
    }

    private static byte[] TraceBytes(Activity activity) => activity.TraceId.ToHexString().HexToBytes(16);

    private static byte[] SpanBytes(Activity activity) => activity.SpanId.ToHexString().HexToBytes(8);

    private static int SpanKind(ActivityKind kind) => kind switch
    {
        ActivityKind.Internal => 1,
        ActivityKind.Server => 2,
        ActivityKind.Client => 3,
        ActivityKind.Producer => 4,
        ActivityKind.Consumer => 5,
        _ => 1,
    };

    private static DecodedSpan ReadSpan(byte[] bytes)
    {
        string name = "";
        ulong start = 0;
        ulong end = 0;
        var hasParent = false;
        var attributes = new List<DecodedAttribute>();
        var links = new List<IReadOnlyList<DecodedAttribute>>();
        ReadMessage(bytes, (input, field, wire) =>
        {
            switch (field)
            {
                case 4:
                    var parent = ReadLength(input, wire);
                    hasParent = ParentPresent(parent);
                    break;
                case 5:
                    name = ReadString(input, wire);
                    break;
                case 7:
                    start = ReadFixed64(input, wire);
                    break;
                case 8:
                    end = ReadFixed64(input, wire);
                    break;
                case 9:
                    attributes.Add(ReadAttribute(ReadLength(input, wire)));
                    break;
                case 13:
                    links.Add(ReadLink(ReadLength(input, wire)));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        });
        return new DecodedSpan
        {
            Name = name,
            StartTimeUnixNano = start,
            EndTimeUnixNano = end,
            HasParentSpanId = hasParent,
            Attributes = attributes,
            Links = links,
        };
    }

    private static IReadOnlyList<DecodedAttribute> ReadLink(byte[] bytes)
    {
        var attributes = new List<DecodedAttribute>();
        ReadMessage(bytes, (input, field, wire) =>
        {
            if (field == 4)
            {
                attributes.Add(ReadAttribute(ReadLength(input, wire)));
            }
            else
            {
                input.SkipLastField();
            }
        });
        return attributes;
    }

    private static bool ParentPresent(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsOwned(string key) => key is EventIdKey or ParentEventIdKey or SequenceEpochKey
        or SequenceNumberKey or SchemaVersionKey or FilterEpochKey or SensitivityKey or CapabilitiesKey;

    private static void RejectConflictingTag(IEnumerable<KeyValuePair<string, object?>>? tags, string key, string expected)
    {
        if (!TryTag(tags, key, out var value) || value == null)
        {
            return;
        }
        if (value is string text && text == expected)
        {
            return;
        }
        throw new OtlpExportException(key + " on the span disagrees with the export");
    }

    private static void RejectConflictingUint(IEnumerable<KeyValuePair<string, object?>>? tags, string key, ulong expected)
    {
        if (!TryTag(tags, key, out var value) || value == null)
        {
            return;
        }
        if (value is int i && i >= 0 && (ulong)i == expected)
        {
            return;
        }
        if (value is long l && l >= 0 && (ulong)l == expected)
        {
            return;
        }
        throw new OtlpExportException(key + " on the span disagrees with the export");
    }

    private static bool TryTag(IEnumerable<KeyValuePair<string, object?>>? tags, string key, out object? value)
    {
        if (tags != null)
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, key, StringComparison.Ordinal))
                {
                    value = tag.Value;
                    return true;
                }
            }
        }
        value = null;
        return false;
    }

    private static long CheckedInt(ulong value, string key)
    {
        if (value > long.MaxValue)
        {
            throw new OtlpExportException(key + " does not fit in an OTLP int64");
        }
        return (long)value;
    }

    private static string SensitivityName(Sensitivity value) => value switch
    {
        Sensitivity.Public => "SENSITIVITY_PUBLIC",
        Sensitivity.Internal => "SENSITIVITY_INTERNAL",
        Sensitivity.Confidential => "SENSITIVITY_CONFIDENTIAL",
        Sensitivity.Restricted => "SENSITIVITY_RESTRICTED",
        _ => throw new OtlpExportException("sentinel.claimed_sensitivity is unspecified"),
    };

    private static string CapabilityName(SourceCapability value) => value switch
    {
        SourceCapability.ObserveBeforeEffect => "SOURCE_CAPABILITY_OBSERVE_BEFORE_EFFECT",
        SourceCapability.CausalEdges => "SOURCE_CAPABILITY_CAUSAL_EDGES",
        SourceCapability.CompletenessSignals => "SOURCE_CAPABILITY_COMPLETENESS_SIGNALS",
        SourceCapability.BoundedClockUncertainty => "SOURCE_CAPABILITY_BOUNDED_CLOCK_UNCERTAINTY",
        _ => throw new OtlpExportException("claimed capability is unspecified"),
    };

    private static Sensitivity ParseSensitivity(string name) => name switch
    {
        "SENSITIVITY_PUBLIC" => Sensitivity.Public,
        "SENSITIVITY_INTERNAL" => Sensitivity.Internal,
        "SENSITIVITY_CONFIDENTIAL" => Sensitivity.Confidential,
        "SENSITIVITY_RESTRICTED" => Sensitivity.Restricted,
        "SENSITIVITY_UNSPECIFIED" => throw new OtlpExportException("sentinel.claimed_sensitivity is unspecified"),
        _ => throw new OtlpExportException("unknown sentinel.claimed_sensitivity " + name),
    };

    private static List<SourceCapability> ParseCapabilities(OtlpValue value)
    {
        if (value.Case != OtlpValue.Shape.Array)
        {
            throw new OtlpExportException("sentinel.claimed_capabilities is not a string array");
        }
        var capabilities = new List<SourceCapability>(value.Items.Count);
        foreach (var item in value.Items)
        {
            if (!TryString(item, out var name))
            {
                throw new OtlpExportException("sentinel.claimed_capabilities is not a string array");
            }
            capabilities.Add(name switch
            {
                "SOURCE_CAPABILITY_OBSERVE_BEFORE_EFFECT" => SourceCapability.ObserveBeforeEffect,
                "SOURCE_CAPABILITY_CAUSAL_EDGES" => SourceCapability.CausalEdges,
                "SOURCE_CAPABILITY_COMPLETENESS_SIGNALS" => SourceCapability.CompletenessSignals,
                "SOURCE_CAPABILITY_BOUNDED_CLOCK_UNCERTAINTY" => SourceCapability.BoundedClockUncertainty,
                _ => throw new OtlpExportException("unknown claimed capability " + name),
            });
        }
        return capabilities;
    }

    private static OccurrenceTime Occurrence(ulong nanos)
    {
        var value = Int128Codec.FromBigInteger(new BigInteger(nanos));
        return new OccurrenceTime
        {
            ClockDomainId = "unix",
            Nanoseconds = value,
            UncertaintyNanoseconds = 0,
        };
    }

    internal static string? StringOf(OtlpValue value) => TryString(value, out var text) ? text : null;

    internal static bool TryString(OtlpValue value, out string text)
    {
        if (value.Case == OtlpValue.Shape.String)
        {
            text = value.String;
            return true;
        }
        text = "";
        return false;
    }

    private static string RequireString(DecodedAttribute attribute)
    {
        if (!TryString(attribute.Value, out var text))
        {
            throw new OtlpExportException(attribute.Key + " is not a string");
        }
        return text;
    }

    private static ulong RequireUint(DecodedAttribute attribute)
    {
        if (attribute.Value.Case != OtlpValue.Shape.Int || attribute.Value.Int < 0)
        {
            throw new OtlpExportException(attribute.Key + " is not a non-negative integer");
        }
        return (ulong)attribute.Value.Int;
    }

    private static AttributeValue? ToAttributeValue(OtlpValue value)
    {
        RejectNested(value);
        return value.Case switch
        {
            OtlpValue.Shape.String => new AttributeValue { StringValue = value.String },
            OtlpValue.Shape.Bool => new AttributeValue { BoolValue = value.Bool },
            OtlpValue.Shape.Int => new AttributeValue { IntegerValue = value.Int },
            OtlpValue.Shape.Double => new AttributeValue { DoubleValue = value.Double },
            OtlpValue.Shape.Bytes => new AttributeValue { BytesValue = ByteString.CopyFrom(value.Bytes) },
            OtlpValue.Shape.Array => ArrayAttribute(value.Items),
            OtlpValue.Shape.Map => MapAttribute(value.Entries),
            _ => null,
        };
    }

    private static AttributeValue ArrayAttribute(IReadOnlyList<OtlpValue> values)
    {
        var items = new List<AttributeValue>();
        foreach (var value in values)
        {
            var mapped = ToAttributeValue(value);
            if (mapped != null)
            {
                items.Add(mapped);
            }
        }
        return new AttributeValue
        {
            ArrayValue = new AttributeArray { Values = { items } },
        };
    }

    private static AttributeValue MapAttribute(IReadOnlyList<DecodedAttribute> entries)
    {
        var mapped = new List<AttributeEntry>();
        foreach (var entry in entries)
        {
            if (entry.Key.StartsWith("sentinel.", StringComparison.Ordinal))
            {
                throw new OtlpExportException("unknown sentinel attribute " + entry.Key);
            }
            var value = ToAttributeValue(entry.Value);
            if (value != null)
            {
                mapped.Add(new AttributeEntry { Key = entry.Key, Value = value });
            }
        }
        return new AttributeValue
        {
            MapValue = new AttributeMap { Entries = { mapped } },
        };
    }

    private static void RejectNested(OtlpValue value)
    {
        switch (value.Case)
        {
            case OtlpValue.Shape.Double when !double.IsFinite(value.Double):
                throw new OtlpExportException("non-finite double");
            case OtlpValue.Shape.Array:
                foreach (var item in value.Items)
                {
                    RejectNested(item);
                }
                break;
            case OtlpValue.Shape.Map:
                foreach (var entry in value.Entries)
                {
                    if (entry.Key.StartsWith("sentinel.", StringComparison.Ordinal))
                    {
                        throw new OtlpExportException("unknown sentinel attribute " + entry.Key);
                    }
                    RejectNested(entry.Value);
                }
                break;
        }
    }

    private static byte[]? EncodeClr(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return StringValue(s);
            case bool b:
                return BoolValue(b);
            case long l:
                return IntValue(l);
            case int i:
                return IntValue(i);
            case double d:
                if (!double.IsFinite(d))
                {
                    throw new OtlpExportException("non-finite double");
                }
                return DoubleValue(d);
            case float f:
                if (!float.IsFinite(f))
                {
                    throw new OtlpExportException("non-finite double");
                }
                return DoubleValue(f);
            case byte[] bytes:
                return BytesValue(bytes);
            case Array array:
                return ArrayValue(array);
            case IDictionary<string, object?> dict:
                return MapValue(dict);
            default:
                return null;
        }
    }

    private static byte[] ArrayValue(Array array)
    {
        var values = new List<byte[]>();
        foreach (var item in array)
        {
            var encoded = EncodeClr(item);
            if (encoded != null)
            {
                values.Add(LengthDelimited(1, new[] { encoded }));
            }
        }
        return LengthDelimited(5, new[] { Concat(values) });
    }

    private static byte[] MapValue(IDictionary<string, object?> dict)
    {
        var entries = new List<byte[]>();
        foreach (var pair in dict)
        {
            if (pair.Key.StartsWith("sentinel.", StringComparison.Ordinal))
            {
                throw new OtlpExportException("unknown sentinel attribute " + pair.Key);
            }
            var encoded = EncodeClr(pair.Value);
            if (encoded != null)
            {
                entries.Add(LengthDelimited(1, new[] { Attribute(pair.Key, encoded) }));
            }
        }
        return LengthDelimited(6, new[] { Concat(entries) });
    }

    private static byte[] Attribute(string key, byte[] value) => Concat(new[] { StringField(1, key), LengthDelimited(2, new[] { value }) });

    private static byte[] StringValue(string value) => StringField(1, value);

    private static byte[] BoolValue(bool value) => VarintField(2, value ? 1UL : 0UL);

    private static byte[] IntValue(long value) => VarintField(3, (ulong)value);

    private static byte[] DoubleValue(double value)
    {
        var payload = new byte[8];
        BitConverter.TryWriteBytes(payload, value);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(payload);
        }
        return Concat(new[] { Tag(4, 1), payload });
    }

    private static byte[] BytesValue(byte[] value) => BytesField(7, value);

    private static DecodedAttribute ReadAttribute(byte[] bytes)
    {
        string key = "";
        OtlpValue? value = null;
        ReadMessage(bytes, (input, field, wire) =>
        {
            switch (field)
            {
                case 1:
                    key = ReadString(input, wire);
                    break;
                case 2:
                    value = ReadAny(ReadLength(input, wire));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        });
        return new DecodedAttribute { Key = key, Value = value ?? OtlpValue.FromString("") };
    }

    private static OtlpValue ReadAny(byte[] bytes)
    {
        OtlpValue? value = null;
        ReadMessage(bytes, (input, field, wire) =>
        {
            value = field switch
            {
                1 => OtlpValue.FromString(ReadString(input, wire)),
                2 => OtlpValue.FromBool(ReadVarint(input, wire) != 0),
                3 => OtlpValue.FromInt(unchecked((long)ReadVarint(input, wire))),
                4 => OtlpValue.FromDouble(ReadDouble(input, wire)),
                5 => ReadArray(ReadLength(input, wire)),
                6 => ReadKeyList(ReadLength(input, wire)),
                7 => OtlpValue.FromBytes(ReadLength(input, wire)),
                _ => Skip(input),
            };
        });
        return value ?? OtlpValue.FromString("");
    }

    private static OtlpValue Skip(CodedInputStream input)
    {
        input.SkipLastField();
        return OtlpValue.FromString("");
    }

    private static OtlpValue ReadArray(byte[] bytes)
    {
        var values = new List<OtlpValue>();
        ReadMessage(bytes, (input, field, wire) =>
        {
            if (field == 1)
            {
                values.Add(ReadAny(ReadLength(input, wire)));
            }
            else
            {
                input.SkipLastField();
            }
        });
        return OtlpValue.FromArray(values);
    }

    private static OtlpValue ReadKeyList(byte[] bytes)
    {
        var entries = new List<DecodedAttribute>();
        ReadMessage(bytes, (input, field, wire) =>
        {
            if (field == 1)
            {
                entries.Add(ReadAttribute(ReadLength(input, wire)));
            }
            else
            {
                input.SkipLastField();
            }
        });
        return OtlpValue.FromMap(entries);
    }

    internal static List<byte[]> ReadRepeated(byte[] bytes, int fieldNumber)
    {
        var found = new List<byte[]>();
        ReadMessage(bytes, (input, field, wire) =>
        {
            if (field == fieldNumber)
            {
                found.Add(ReadLength(input, wire));
            }
            else
            {
                input.SkipLastField();
            }
        });
        return found;
    }

    private static void ReadMessage(byte[] bytes, Action<CodedInputStream, int, WireFormat.WireType> field)
    {
        using var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            field(input, WireFormat.GetTagFieldNumber(tag), WireFormat.GetTagWireType(tag));
        }
    }

    private static string ReadString(CodedInputStream input, WireFormat.WireType wire)
    {
        if (wire != WireFormat.WireType.LengthDelimited)
        {
            input.SkipLastField();
            return "";
        }
        return input.ReadString();
    }

    private static byte[] ReadLength(CodedInputStream input, WireFormat.WireType wire)
    {
        if (wire != WireFormat.WireType.LengthDelimited)
        {
            input.SkipLastField();
            return Array.Empty<byte>();
        }
        return input.ReadBytes().ToByteArray();
    }

    private static ulong ReadVarint(CodedInputStream input, WireFormat.WireType wire)
    {
        if (wire != WireFormat.WireType.Varint)
        {
            input.SkipLastField();
            return 0;
        }
        return input.ReadUInt64();
    }

    private static ulong ReadFixed64(CodedInputStream input, WireFormat.WireType wire)
    {
        if (wire != WireFormat.WireType.Fixed64)
        {
            input.SkipLastField();
            return 0;
        }
        return input.ReadFixed64();
    }

    private static double ReadDouble(CodedInputStream input, WireFormat.WireType wire)
    {
        if (wire != WireFormat.WireType.Fixed64)
        {
            input.SkipLastField();
            return 0;
        }
        return input.ReadDouble();
    }

    private static byte[] Tag(int field, int wireType)
    {
        var tag = (uint)((field << 3) | wireType);
        var buffer = new byte[10];
        var size = 0;
        while (tag > 0x7F)
        {
            buffer[size++] = (byte)((tag & 0x7F) | 0x80);
            tag >>= 7;
        }
        buffer[size++] = (byte)tag;
        var written = new byte[size];
        Array.Copy(buffer, written, size);
        return written;
    }

    private static byte[] VarintField(int field, ulong value) => Concat(new[] { Tag(field, 0), Varint(value) });

    private static byte[] Varint(ulong value)
    {
        var buffer = new byte[10];
        var size = 0;
        while (value > 0x7F)
        {
            buffer[size++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        buffer[size++] = (byte)value;
        var written = new byte[size];
        Array.Copy(buffer, written, size);
        return written;
    }

    private static byte[] Fixed64Field(int field, ulong value)
    {
        var payload = new byte[8];
        BitConverter.TryWriteBytes(payload, value);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(payload);
        }
        return Concat(new[] { Tag(field, 1), payload });
    }

    private static byte[] StringField(int field, string value)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(value);
        return Concat(new[] { Tag(field, 2), Varint((ulong)payload.Length), payload });
    }

    private static byte[] BytesField(int field, byte[] value) =>
        Concat(new[] { Tag(field, 2), Varint((ulong)value.Length), value });

    private static byte[] LengthDelimited(int field, IReadOnlyList<byte[]> parts)
    {
        var payload = Concat(parts);
        return Concat(new[] { Tag(field, 2), Varint((ulong)payload.Length), payload });
    }

    internal static byte[] Concat(IReadOnlyList<byte[]> parts)
    {
        var length = 0;
        foreach (var part in parts)
        {
            length += part.Length;
        }
        var buffer = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, buffer, offset, part.Length);
            offset += part.Length;
        }
        return buffer;
    }

    private static byte[] HexToBytes(this string hex, int expected)
    {
        if (hex.Length != expected * 2)
        {
            throw new OtlpExportException("span id is not " + expected.ToString(CultureInfo.InvariantCulture) + " bytes");
        }
        var bytes = new byte[expected];
        for (var i = 0; i < expected; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return bytes;
    }
}

/// <summary>Encode and decode the log half of the probe OTLP wire.</summary>
public static class OtlpLogs
{
    public const string PayloadTypeKey = "sentinel.payload.type";
    public const string RequestIdKey = "sentinel.request_id";
    public const string SourceHandleKey = "sentinel.source_handle";
    public const string CompletenessType = "sentinel.model.v1.ProducerCompletenessSignal";
    public const string EventFilterType = "sentinel.model.v1.EventFilter";
    public const string VerdictType = "sentinel.model.v1.Verdict";
    public const string DecideResponseType = "sentinel.probe.v1.DecideResponse";

    public static byte[] EncodeCompleteness(string sourceHandle, ProducerCompletenessSignal signal)
    {
        if (string.IsNullOrWhiteSpace(sourceHandle))
        {
            throw new OtlpExportException("source handle is empty");
        }
        if (signal == null)
        {
            throw new OtlpExportException("completeness signal is missing");
        }
        return EncodeRecord(CompletenessType, signal.ToByteArray(), requestId: null, sourceHandle: sourceHandle);
    }

    public static byte[] EncodeRecord(string payloadType, byte[] body, string? requestId, string? sourceHandle)
    {
        var attributes = new List<byte[]> { StringAttribute(PayloadTypeKey, payloadType) };
        if (requestId != null)
        {
            attributes.Add(StringAttribute(RequestIdKey, requestId));
        }
        if (sourceHandle != null)
        {
            attributes.Add(StringAttribute(SourceHandleKey, sourceHandle));
        }
        var record = LogRecord(body, attributes);
        var scope = Length(2, new[] { record });
        var resource = Length(2, new[] { scope });
        return Length(1, new[] { resource });
    }

    public static IReadOnlyList<DecodedLog> Decode(byte[] export)
    {
        var records = new List<DecodedLog>();
        foreach (var resource in OtlpTraces.ReadRepeated(export, 1))
        {
            foreach (var scope in OtlpTraces.ReadRepeated(resource, 2))
            {
                foreach (var record in OtlpTraces.ReadRepeated(scope, 2))
                {
                    records.Add(ReadRecord(record));
                }
            }
        }
        return records;
    }

    private static DecodedLog ReadRecord(byte[] bytes)
    {
        var attributes = new List<DecodedAttribute>();
        uint dropped = 0;
        byte[]? body = null;
        var bodyIsBytes = false;
        var sawBody = false;
        using var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            var wire = WireFormat.GetTagWireType(tag);
            switch (field)
            {
                case 5:
                    sawBody = true;
                    var any = ReadAnyBytes(input, wire, out bodyIsBytes);
                    body = any;
                    break;
                case 6:
                    if (wire != WireFormat.WireType.LengthDelimited)
                    {
                        input.SkipLastField();
                        break;
                    }
                    attributes.Add(ReadStringAttribute(input.ReadBytes().ToByteArray()));
                    break;
                case 7:
                    if (wire == WireFormat.WireType.Varint)
                    {
                        dropped = (uint)input.ReadUInt32();
                    }
                    else
                    {
                        input.SkipLastField();
                    }
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        if (sawBody && !bodyIsBytes)
        {
            body = null;
        }
        return new DecodedLog
        {
            Attributes = attributes,
            DroppedAttributesCount = dropped,
            BodyBytes = body,
            BodyIsBytes = bodyIsBytes,
        };
    }

    private static byte[]? ReadAnyBytes(CodedInputStream input, WireFormat.WireType wire, out bool isBytes)
    {
        isBytes = false;
        if (wire != WireFormat.WireType.LengthDelimited)
        {
            input.SkipLastField();
            return null;
        }
        var bytes = input.ReadBytes().ToByteArray();
        byte[]? payload = null;
        var bytesField = false;
        using var any = new CodedInputStream(bytes);
        uint tag;
        while ((tag = any.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            if (field == 7 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                payload = any.ReadBytes().ToByteArray();
                bytesField = true;
            }
            else if (field is >= 1 and <= 6)
            {
                bytesField = false;
                any.SkipLastField();
            }
            else
            {
                any.SkipLastField();
            }
        }
        isBytes = bytesField;
        return payload;
    }

    private static DecodedAttribute ReadStringAttribute(byte[] bytes)
    {
        string key = "";
        OtlpValue value = OtlpValue.FromString("");
        using var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            var wire = WireFormat.GetTagWireType(tag);
            if (field == 1 && wire == WireFormat.WireType.LengthDelimited)
            {
                key = input.ReadString();
            }
            else if (field == 2 && wire == WireFormat.WireType.LengthDelimited)
            {
                value = ReadAnyValue(input.ReadBytes().ToByteArray());
            }
            else
            {
                input.SkipLastField();
            }
        }
        return new DecodedAttribute { Key = key, Value = value };
    }

    private static OtlpValue ReadAnyValue(byte[] bytes)
    {
        using var input = new CodedInputStream(bytes);
        uint tag;
        OtlpValue value = OtlpValue.FromString("");
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            var wire = WireFormat.GetTagWireType(tag);
            if (field == 1 && wire == WireFormat.WireType.LengthDelimited)
            {
                value = OtlpValue.FromString(input.ReadString());
            }
            else if (field == 4 && wire == WireFormat.WireType.Fixed64)
            {
                value = OtlpValue.FromDouble(input.ReadDouble());
            }
            else if (field == 7 && wire == WireFormat.WireType.LengthDelimited)
            {
                value = OtlpValue.FromBytes(input.ReadBytes().ToByteArray());
            }
            else
            {
                input.SkipLastField();
            }
        }
        return value;
    }

    private static byte[] LogRecord(byte[] body, IReadOnlyList<byte[]> attributes)
    {
        var fields = new List<byte[]> { Length(5, new[] { BytesField(7, body) }) };
        foreach (var attribute in attributes)
        {
            fields.Add(Length(6, new[] { attribute }));
        }
        return ConcatRaw(fields);
    }

    private static byte[] StringAttribute(string key, string value) =>
        ConcatRaw(AttributeParts(key, value));

    private static byte[][] AttributeParts(string key, string value) =>
        new[] { StringField(1, key), Length(2, new[] { StringField(1, value) }) };

    private static byte[] StringField(int field, string value)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(value);
        return ConcatRaw(new[] { Tag(field, 2), Varint((ulong)payload.Length), payload });
    }

    private static byte[] BytesField(int field, byte[] value) =>
        ConcatRaw(new[] { Tag(field, 2), Varint((ulong)value.Length), value });

    private static byte[] Length(int field, IReadOnlyList<byte[]> parts)
    {
        var payload = ConcatRaw(parts);
        return ConcatRaw(new[] { Tag(field, 2), Varint((ulong)payload.Length), payload });
    }

    private static byte[] Tag(int field, int wireType)
    {
        ulong tag = (ulong)((field << 3) | wireType);
        return Varint(tag);
    }

    private static byte[] Varint(ulong value)
    {
        var buffer = new byte[10];
        var size = 0;
        while (value > 0x7F)
        {
            buffer[size++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        buffer[size++] = (byte)value;
        var written = new byte[size];
        Array.Copy(buffer, written, size);
        return written;
    }

    private static byte[] ConcatRaw(IReadOnlyList<byte[]> parts) => OtlpTraces.Concat(parts);
}
