using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Xunit;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Emission;
using Sentinel.Probe.Sdk.Int128;

namespace Sentinel.Probe.Sdk.Tests;

public class SpanToEventTests
{
    private static ProducerEvent Convert(Activity activity, SpanConversion? conversion = null) =>
        SpanToEventConverter.SpanToEvent(conversion ?? new SpanConversion
        {
            Activity = activity,
            SchemaVersion = "sentinel.model.v1",
            EventId = "evt-1",
        });

    private static AttributeValue? AttrValue(ProducerEvent e, string key)
    {
        foreach (var entry in e.Attributes)
        {
            if (entry.Key == key) return entry.Value;
        }
        Assert.Fail($"attribute {key} not emitted; got {e.Attributes.Count} entries");
        return null;
    }

    [Fact]
    public void SpanNameBecomesEventKind()
    {
        var e = Convert(ActivityBuilder.Build(name: "transfer.initiated"));
        Assert.Equal("transfer.initiated", e.Kind);
    }

    [Fact]
    public void EventIdAndSchemaVersionArePassedThrough()
    {
        var e = Convert(ActivityBuilder.Build());
        Assert.Equal("evt-1", e.Id);
        Assert.Equal("sentinel.model.v1", e.SchemaVersion);
    }

    [Fact]
    public void AcknowledgedEpochIsStampedIncludingZero()
    {
        foreach (ulong epoch in new[] { 0UL, 42UL })
        {
            var e = Convert(ActivityBuilder.Build(), new SpanConversion { Activity = ActivityBuilder.Build(), AcknowledgedEpoch = epoch });
            Assert.NotNull(e.AcknowledgedFilterEpoch);
            Assert.Equal(epoch, e.AcknowledgedFilterEpoch!.Value);
        }
    }

    [Fact]
    public void SequenceCapabilitiesAndSensitivityArePassedThrough()
    {
        var e = SpanToEventConverter.SpanToEvent(new SpanConversion
        {
            Activity = ActivityBuilder.Build(),
            Sequence = new SequenceCoordinate { Epoch = 2, Sequence = 11 },
            ClaimedCapabilities = new() { SourceCapability.SourceCapabilityCausalEdges, SourceCapability.SourceCapabilityObserveBeforeEffect },
            ClaimedSensitivity = Sensitivity.SensitivityRestricted,
        });
        Assert.Equal(2UL, e.Sequence!.Epoch);
        Assert.Equal(11UL, e.Sequence!.Sequence);
        Assert.Equal(2, e.ClaimedCapabilities.Count);
        Assert.Equal(Sensitivity.SensitivityRestricted, e.ClaimedSensitivity);
    }

    [Fact]
    public void StringAttributeMapsToStringValue()
    {
        var e = Convert(ActivityBuilder.Build(tags: new[] { KVP("sagashop.order.id", "ord-9") }));
        Assert.Equal("ord-9", AttrValue(e, "sagashop.order.id")!.StringValue);
    }

    [Fact]
    public void BoolAttributeMapsToBoolValue()
    {
        var e = Convert(ActivityBuilder.Build(tags: new[] { KVP("approved", true) }));
        var v = AttrValue(e, "approved")!;
        Assert.Equal(AttributeValueOneofCase.BoolValue, v.Case);
        Assert.True(v.BoolValue);
    }

    [Fact]
    public void Int64AttributeMapsToIntegerValue()
    {
        var e = Convert(ActivityBuilder.Build(tags: new[] { KVP("sagashop.amount_cents", -4200L) }));
        var v = AttrValue(e, "sagashop.amount_cents")!;
        Assert.Equal(AttributeValueOneofCase.IntegerValue, v.Case);
        Assert.Equal(-4200L, v.IntegerValue);
    }

    // D14: type-directed, so a double stays a double even when integral.
    [Fact]
    public void DoubleAttributeMapsToDoubleValueEvenWhenIntegral()
    {
        var e = Convert(ActivityBuilder.Build(tags: new[] { KVP("ratio", 0.25), KVP("integral", 3.0) }));
        Assert.Equal(0.25, AttrValue(e, "ratio")!.DoubleValue);
        Assert.Equal(AttributeValueOneofCase.DoubleValue, AttrValue(e, "integral")!.Case);
    }

    [Fact]
    public void ByteSliceAttributeMapsToBytesValue()
    {
        var e = Convert(ActivityBuilder.Build(tags: new[] { KVP("payload", new byte[] { 0xde, 0xad, 0xbe, 0xef }) }));
        var v = AttrValue(e, "payload")!;
        Assert.Equal(AttributeValueOneofCase.BytesValue, v.Case);
        Assert.Equal(new byte[] { 0xde, 0xad, 0xbe, 0xef }, v.BytesValue.ToByteArray());
    }

    [Fact]
    public void HomogeneousSlicesMapToArrayValue()
    {
        var e = Convert(ActivityBuilder.Build(tags: new[]
        {
            KVP("tags", new[] { "a", "b" }),
            KVP("counts", new long[] { 1, 2, 3 }),
            KVP("ratios", new double[] { 0.5 }),
            KVP("flags", new bool[] { true, false }),
        }));
        var tags = AttrValue(e, "tags")!.ArrayValue!;
        Assert.Equal(2, tags.Values.Count);
        Assert.Equal("b", tags.Values[1].StringValue);
        var counts = AttrValue(e, "counts")!.ArrayValue!;
        Assert.Equal(3, counts.Values.Count);
        Assert.Equal(3L, counts.Values[2].IntegerValue);
        var ratios = AttrValue(e, "ratios")!.ArrayValue!;
        Assert.Single(ratios.Values);
        Assert.Equal(0.5, ratios.Values[0].DoubleValue);
        var flags = AttrValue(e, "flags")!.ArrayValue!;
        Assert.Equal(2, flags.Values.Count);
        Assert.True(flags.Values[0].BoolValue);
        Assert.False(flags.Values[1].BoolValue);
    }

    [Fact]
    public void HeterogeneousSliceMapsToArrayValue()
    {
        var e = Convert(ActivityBuilder.Build(tags: new[] { KVP("mixed", new object?[] { "a", 7L, true }) }));
        var values = AttrValue(e, "mixed")!.ArrayValue!.Values;
        Assert.Equal(3, values.Count);
        Assert.Equal("a", values[0].StringValue);
        Assert.Equal(7L, values[1].IntegerValue);
        Assert.True(values[2].BoolValue);
    }

    [Fact]
    public void MapAttributeMapsToMapValue()
    {
        var nested = new Dictionary<string, object?>
        {
            ["inner"] = "v",
            ["count"] = 2L,
            ["deeper"] = new Dictionary<string, object?> { ["flag"] = true },
        };
        var e = Convert(ActivityBuilder.Build(tags: new[] { KVP("nested", nested) }));
        var entries = AttrValue(e, "nested")!.MapValue!.Entries;
        Assert.Equal(3, entries.Count);
        static AttributeValue? ByKey(List<AttributeEntry> es, string key)
        {
            foreach (var en in es) if (en.Key == key) return en.Value;
            return null;
        }
        Assert.Equal("v", ByKey(entries, "inner")!.StringValue);
        Assert.Equal(2L, ByKey(entries, "count")!.IntegerValue);
        var deeper = ByKey(entries, "deeper")!.MapValue!.Entries;
        Assert.Single(deeper);
        Assert.True(deeper[0].Value!.BoolValue);
    }

    [Fact]
    public void EmptySlicesAndMapsMapToEmptyContainers()
    {
        var e = Convert(ActivityBuilder.Build(tags: new[]
        {
            KVP("empty_array", new string[0]),
            KVP("empty_map", new Dictionary<string, object?>()),
        }));
        Assert.Empty(AttrValue(e, "empty_array")!.ArrayValue!.Values);
        Assert.Empty(AttrValue(e, "empty_map")!.MapValue!.Entries);
    }

    [Fact]
    public void EmptyAttributeValueIsSkipped()
    {
        var e = Convert(ActivityBuilder.Build(tags: new[]
        {
            KVP("empty", null),
            KVP("kept", "v"),
        }));
        Assert.Single(e.Attributes);
        Assert.Equal("kept", e.Attributes[0].Key);
    }

    [Fact]
    public void ReservedKeysAreExcludedFromAttributes()
    {
        var e = Convert(ActivityBuilder.Build(tags: new[]
        {
            KVP(ReservedAttributes.AttributeEventId, "evt-1"),
            KVP(ReservedAttributes.AttributeParentEventID, "evt-parent"),
            KVP("k", "v"),
        }));
        Assert.Single(e.Attributes);
        Assert.Equal("k", e.Attributes[0].Key);
    }

    [Fact]
    public void AttributeOrderIsDeterministic()
    {
        var e = Convert(ActivityBuilder.Build(tags: new[]
        {
            KVP("z", "1"), KVP("a", "2"), KVP("m", "3"),
        }));
        Assert.Equal(new[] { "z", "a", "m" }, e.Attributes.Select(a => a.Key).ToArray());
    }

    [Fact]
    public void ParentEventIdComesFromReservedAttribute()
    {
        var e = Convert(ActivityBuilder.Build(
            parentSpanHex: "1112131415161718",
            tags: new[] { KVP(ReservedAttributes.AttributeParentEventID, "evt-parent") }));
        Assert.Equal(new[] { "evt-parent" }, e.CausalPredecessorIds);
    }

    [Fact]
    public void LinkPredecessorComesFromThatLinksOwnAttributes()
    {
        var e = Convert(ActivityBuilder.Build(links: new (string, IEnumerable<KeyValuePair<string, object?>>?)[]
        {
            ("2122232425262728", (IEnumerable<KeyValuePair<string, object?>>)new[] { KVP(ReservedAttributes.AttributeEventId, "evt-linked") }),
        }));
        Assert.Equal(new[] { "evt-linked" }, e.CausalPredecessorIds);
    }

    [Fact]
    public void ParentAndLinkProduceExactlyTheJoinableEdges()
    {
        var malformed = new List<string>();
        var e = SpanToEventConverter.SpanToEvent(new SpanConversion
        {
            Activity = ActivityBuilder.Build(
                parentSpanHex: "1112131415161718",
                tags: new[] { KVP(ReservedAttributes.AttributeParentEventID, "evt-parent") },
                links: new (string, IEnumerable<KeyValuePair<string, object?>>?)[]
                {
                    ("2122232425262728", (IEnumerable<KeyValuePair<string, object?>>)new[] { KVP(ReservedAttributes.AttributeEventId, "evt-second") }),
                    ("3132333435363738", (IEnumerable<KeyValuePair<string, object?>>)new[] { KVP(ReservedAttributes.AttributeEventId, "evt-third") }),
                }),
            EventId = "evt-1",
            OnMalformedLink = (source, spanId) => malformed.Add(source + ":" + spanId),
        });
        Assert.Equal(new[] { "evt-parent", "evt-second", "evt-third" }, e.CausalPredecessorIds);
        Assert.Empty(malformed);
    }

    [Fact]
    public void MissingLinkEventIdFailsAndDoesNotUseTheSpanId()
    {
        var error = Assert.Throws<Sentinel.Probe.Sdk.Otlp.OtlpExportException>(() => SpanToEventConverter.SpanToEvent(new SpanConversion
        {
            Activity = ActivityBuilder.Build(links: new (string, IEnumerable<KeyValuePair<string, object?>>?)[] { ("2122232425262728", null) }),
        }));
        Assert.DoesNotContain("2122232425262728", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingParentEventIdFailsAndDoesNotUseTheSpanId()
    {
        var error = Assert.Throws<Sentinel.Probe.Sdk.Otlp.OtlpExportException>(() => SpanToEventConverter.SpanToEvent(new SpanConversion
        {
            Activity = ActivityBuilder.Build(parentSpanHex: "1112131415161718"),
        }));
        Assert.DoesNotContain("1112131415161718", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MistypedParentEventIdFailsAndDoesNotUseTheSpanId()
    {
        var error = Assert.Throws<Sentinel.Probe.Sdk.Otlp.OtlpExportException>(() => SpanToEventConverter.SpanToEvent(new SpanConversion
        {
            Activity = ActivityBuilder.Build(
                parentSpanHex: "1112131415161718",
                tags: new[] { KVP(ReservedAttributes.AttributeParentEventID, 7L) }),
        }));
        Assert.DoesNotContain("1112131415161718", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoParentAndNoLinksYieldsNoPredecessors()
    {
        bool called = false;
        var e = SpanToEventConverter.SpanToEvent(new SpanConversion
        {
            Activity = ActivityBuilder.Build(),
            OnMalformedLink = (_, _) => called = true,
        });
        Assert.Empty(e.CausalPredecessorIds);
        Assert.False(called);
    }

    [Fact]
    public void InvalidParentIsNotAnEdge()
    {
        var e = Convert(ActivityBuilder.Build()); // no parent set -> default SpanId
        Assert.Empty(e.CausalPredecessorIds);
    }

    [Fact]
    public void MissingLinkWithoutACallbackStillFailsTheExport()
    {
        var error = Assert.Throws<Sentinel.Probe.Sdk.Otlp.OtlpExportException>(() => SpanToEventConverter.SpanToEvent(new SpanConversion
        {
            Activity = ActivityBuilder.Build(links: new (string, IEnumerable<KeyValuePair<string, object?>>?)[] { ("2122232425262728", null) }),
        }));
        Assert.DoesNotContain("2122232425262728", error.Message, StringComparison.Ordinal);
    }

    private static KeyValuePair<string, object?> KVP(string key, object? value) => new(key, value);
}
