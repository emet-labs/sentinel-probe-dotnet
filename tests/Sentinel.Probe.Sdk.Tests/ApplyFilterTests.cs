using System.Collections.Generic;
using System.Linq;
using Xunit;
using Emet.Sentinel.Model.V1;
using Int128Value = Emet.Sentinel.Model.V1.Int128;
using Sentinel.Probe.Sdk.Filter;

namespace Sentinel.Probe.Sdk.Tests;

public class ApplyFilterTests
{
    private static AttributeEntry Attr(string key, string value) =>
        new() { Key = key, Value = new AttributeValue { Case = AttributeValueOneofCase.StringValue, StringValue = value } };

    private static ProducerEvent Event(string kind, List<AttributeEntry> attrs, params string[] predecessors) =>
        new()
        {
            Id = "test-event",
            Kind = kind,
            SchemaVersion = "sentinel.model.v1",
            Attributes = attrs,
            CausalPredecessorIds = new List<string>(predecessors),
        };

    private static SpecificationFilter Spec(List<string>? kinds, List<string>? keys) =>
        new()
        {
            SpecificationId = "spec-1",
            SpecificationVersion = "1.0.0",
            EventMatch = new EventMatch
            {
                EventKinds = kinds ?? new(),
                ProjectedAttributeKeys = keys ?? new(),
                DeliveryMode = DeliveryMode.DeliveryModeShipAsync,
            },
        };

    private static EventFilter EventFilter(params SpecificationFilter[] specs) =>
        new() { Epoch = 5, Specifications = new List<SpecificationFilter>(specs) };

    private static List<string> KeysOf(ProducerEvent e) => e.Attributes.Select(a => a.Key).ToList();

    [Fact]
    public void NoSpecSelectsDrops()
    {
        var e = Event("unrelated", new() { Attr("k", "v") });
        Assert.Null(ApplyFilter.Apply(e, EventFilter(Spec(new() { "other" }, null))));
    }

    [Fact]
    public void ProjectsSubset()
    {
        var e = Event("x", new() { Attr("keep", "1"), Attr("drop", "2") });
        var got = ApplyFilter.Apply(e, EventFilter(Spec(new() { "x" }, new() { "keep" })));
        Assert.NotNull(got);
        Assert.Equal(new() { "keep" }, KeysOf(got!));
    }

    [Fact]
    public void EmptyProjectionKeepsAll()
    {
        var e = Event("x", new() { Attr("a", "1"), Attr("b", "2") });
        var got = ApplyFilter.Apply(e, EventFilter(Spec(new() { "x" }, new())));
        Assert.NotNull(got);
        Assert.Equal(2, got!.Attributes.Count);
    }

    [Fact]
    public void UnionsProjectedKeys()
    {
        var e = Event("x", new() { Attr("a", "1"), Attr("b", "2"), Attr("c", "3") });
        var got = ApplyFilter.Apply(e, EventFilter(
            Spec(new() { "x" }, new() { "a" }),
            Spec(new() { "x" }, new() { "b" })));
        Assert.Equal(new() { "a", "b" }, KeysOf(got!));
    }

    [Fact]
    public void ProjectAllWinsOverSubset()
    {
        var e = Event("x", new() { Attr("a", "1"), Attr("b", "2") });
        var got = ApplyFilter.Apply(e, EventFilter(
            Spec(new() { "x" }, new() { "a" }),
            Spec(new() { "x" }, new()))); // empty projection -> keep everything
        Assert.Equal(2, got!.Attributes.Count);
    }

    [Fact]
    public void EmptyFilterDrops() => Assert.Null(ApplyFilter.Apply(Event("x", new() { Attr("k", "v") }), EventFilter()));

    [Fact]
    public void NullFilterDrops() => Assert.Null(ApplyFilter.Apply(Event("x", new() { Attr("k", "v") }), null));

    [Fact]
    public void EmptyEventKindsMatchesAll()
    {
        var e = Event("anything", new() { Attr("k", "v") });
        var got = ApplyFilter.Apply(e, EventFilter(Spec(new(), new() { "k" })));
        Assert.NotNull(got);
    }

    [Fact]
    public void NeverTrimsCausalPredecessors()
    {
        var e = Event("x", new() { Attr("k", "v") }, "p1", "p2");
        var got = ApplyFilter.Apply(e, EventFilter(Spec(new() { "x" }, new() { "k" })));
        Assert.Equal(new() { "p1", "p2" }, got!.CausalPredecessorIds);
    }

    [Fact]
    public void DropsUnprojectedAttribute()
    {
        var e = Event("x", new() { Attr("keep", "1"), Attr("drop", "2") });
        var got = ApplyFilter.Apply(e, EventFilter(Spec(new() { "x" }, new() { "keep" })));
        Assert.DoesNotContain("drop", KeysOf(got!));
    }

    [Fact]
    public void PreservesIdentityFields()
    {
        var source = new ProducerEvent
        {
            Id = "evt",
            Kind = "x",
            SchemaVersion = "sentinel.model.v1",
            Sequence = new SequenceCoordinate { Epoch = 3, Sequence = 9 },
            Attributes = new() { Attr("k", "v") },
        };
        var got = ApplyFilter.Apply(source, EventFilter(Spec(new() { "x" }, new() { "k" })));
        Assert.Equal("evt", got!.Id);
        Assert.Equal(9UL, got.Sequence!.Sequence);
        Assert.Equal(3UL, got.Sequence!.Epoch);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(42UL)]
    public void PreservesAcknowledgedEpochIncludingZero(ulong epoch)
    {
        var source = new ProducerEvent
        {
            Id = "evt", Kind = "x", SchemaVersion = "sentinel.model.v1",
            AcknowledgedFilterEpoch = epoch,
            Attributes = new() { Attr("k", "v") },
        };
        var got = ApplyFilter.Apply(source, EventFilter(Spec(new() { "x" }, new() { "k" })));
        Assert.NotNull(got);
        Assert.NotNull(got!.AcknowledgedFilterEpoch);
        Assert.Equal(epoch, got.AcknowledgedFilterEpoch!.Value);
    }

    [Fact]
    public void AbsentAcknowledgedEpochStaysAbsent()
    {
        var absent = new ProducerEvent { Id = "evt", Kind = "x", Attributes = new() { Attr("k", "v") } };
        var got = ApplyFilter.Apply(absent, EventFilter(Spec(new() { "x" }, new() { "k" })));
        Assert.NotNull(got);
        Assert.Null(got!.AcknowledgedFilterEpoch);
    }

    [Fact]
    public void PreservesCapabilitiesSensitivityAndTiming()
    {
        var source = new ProducerEvent
        {
            Id = "evt", Kind = "x", SchemaVersion = "sentinel.model.v1",
            Sequence = new SequenceCoordinate { Epoch = 3, Sequence = 9 },
            OccurrenceTime = new OccurrenceTime { ClockDomainId = "unix", Nanoseconds = new Int128Value { High = 0, Low = 1700000000123456789 } },
            ClaimedCapabilities = new() { SourceCapability.SourceCapabilityCausalEdges },
            ClaimedSensitivity = Sensitivity.SensitivityConfidential,
            Attributes = new() { Attr("k", "v") },
        };
        var got = ApplyFilter.Apply(source, EventFilter(Spec(new() { "x" }, new() { "k" })));
        Assert.Equal(9UL, got!.Sequence!.Sequence);
        Assert.Equal("unix", got.OccurrenceTime!.ClockDomainId);
        Assert.Equal(1700000000123456789UL, got.OccurrenceTime!.Nanoseconds!.Low);
        Assert.Equal(source.ClaimedCapabilities, got.ClaimedCapabilities);
        Assert.Equal(Sensitivity.SensitivityConfidential, got.ClaimedSensitivity);
    }

    // The returned event must not share its attribute list with the input (aliasing contract).
    [Fact]
    public void AllocatesAFreshAttributeSlice()
    {
        var source = Event("x", new() { Attr("a", "1"), Attr("b", "2") });
        var got = ApplyFilter.Apply(source, EventFilter(Spec(new() { "x" }, new())));
        Assert.Equal(2, got!.Attributes.Count);
        got.Attributes[0] = Attr("swapped", "z");
        Assert.Equal("a", source.Attributes[0].Key); // input untouched
        // The entries themselves remain shared by design (shallow aliasing).
        Assert.Same(source.Attributes[1], got.Attributes[1]);
    }
}
