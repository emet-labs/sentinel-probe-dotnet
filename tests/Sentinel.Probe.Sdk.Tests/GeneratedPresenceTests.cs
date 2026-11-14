using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Google.Protobuf;
using Sentinel.Probe.Sdk.Client;
using Sentinel.Probe.Sdk.Filter;
using Sentinel.Probe.Sdk.Proto;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

// Regression tests for the move from the hand-written proto classes to protoc-gen-csharp output
// (#466). Each case pins a defect the hand-written tree had, or a hazard the generated API
// introduces: proto3 `optional` fields read as a plain `ulong` that widens silently into a
// `ulong?`, so "absent" and 0 must stay distinguishable at every SDK boundary.
public class GeneratedPresenceTests
{
    private static T RoundTrip<T>(T message)
        where T : IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());
        return parser.ParseFrom(message.ToByteArray());
    }

    // ---- FilterStore: an epoch-less held filter is not epoch 0 -------------------------------

    [Fact]
    public void EpochLessHeldFilterReportsNoEpoch()
    {
        var store = new FilterStore(new EventFilter());
        Assert.NotNull(store.Get());
        Assert.Null(store.Epoch());
    }

    [Fact]
    public void SetEpochZeroOverEpochLessHeldFilterUpdates()
    {
        var store = new FilterStore(new EventFilter());
        Assert.True(store.Set(new EventFilter { Epoch = 0 }));
        Assert.Equal(0UL, store.Epoch());
    }

    [Fact]
    public void SetEpochLessOverEpochZeroHeldFilterIsRefused()
    {
        // Epoch 0 is a present epoch, so an epoch-less Filter never replaces it (#464).
        var store = new FilterStore(new EventFilter { Epoch = 0 });
        Assert.False(store.Set(new EventFilter()));
        Assert.Equal(0UL, store.Epoch());
    }

    [Fact]
    public void ShouldRefreshZeroWithEpochLessHeldFilterRefreshes()
    {
        var store = new FilterStore(new EventFilter());
        Assert.True(store.ShouldRefresh(0UL));
    }

    // ---- Presence helpers and the request builders ------------------------------------------

    [Fact]
    public void BuildDecideRequestWithEpochLessFilterLeavesFilterEpochAbsent()
    {
        var client = new ProbeClient(
            new ProbeClient.Config { SourceHandle = "src", InitialFilter = new EventFilter() },
            new NoDecider());
        var request = client.BuildDecideRequest(new ProducerEvent { Kind = "x" }, "r", "i", null);
        Assert.False(request.HasFilterEpoch);
        Assert.False(request.HasRemainingTransportBudgetNanoseconds);
    }

    [Fact]
    public void BuildDecideRequestWithEpochZeroStampsPresentZero()
    {
        var client = new ProbeClient(
            new ProbeClient.Config { SourceHandle = "src", InitialFilter = new EventFilter { Epoch = 0 } },
            new NoDecider());
        var request = RoundTrip(client.BuildDecideRequest(new ProducerEvent { Kind = "x" }, "r", "i", 0UL));
        Assert.True(request.HasFilterEpoch);
        Assert.Equal(0UL, request.FilterEpoch);
        Assert.True(request.HasRemainingTransportBudgetNanoseconds);
        Assert.Equal(0UL, request.RemainingTransportBudgetNanoseconds);
    }

    [Fact]
    public void PresenceHelpersDistinguishAbsentFromZero()
    {
        Assert.Null(new EventFilter().EpochOrNull());
        Assert.Equal(0UL, new EventFilter { Epoch = 0 }.EpochOrNull());
        Assert.Null(new SpecificationFilter().LatencyBudgetNanosecondsOrNull());
        Assert.Equal(0UL, new SpecificationFilter { LatencyBudgetNanoseconds = 0 }.LatencyBudgetNanosecondsOrNull());
        Assert.Null(new ProducerEvent().AcknowledgedFilterEpochOrNull());

        var @event = new ProducerEvent();
        @event.SetAcknowledgedFilterEpoch(0UL);
        Assert.True(@event.HasAcknowledgedFilterEpoch);
        @event.SetAcknowledgedFilterEpoch(null);
        Assert.False(@event.HasAcknowledgedFilterEpoch);
    }

    [Fact]
    public void ApplyFilterKeepsAbsentAcknowledgedEpochAbsentAcrossTheWire()
    {
        var source = new ProducerEvent
        {
            Id = "evt",
            Kind = "x",
            Attributes = { new AttributeEntry { Key = "k", Value = new AttributeValue { StringValue = "v" } } },
        };
        var filter = TestSupport.MakeFilter(0UL, new SpecificationFilter { EventMatch = new EventMatch { EventKinds = { "x" } } });
        var projected = ApplyFilter.Apply(source, filter);
        Assert.NotNull(projected);
        Assert.False(RoundTrip(projected!).HasAcknowledgedFilterEpoch);
    }

    // ---- The wire: one pin, then the real defects of the hand-written tree ------------------

    // Not a defect of the hand-written tree: its EventFilter did keep epoch 0 apart from absence
    // (a ulong? written with WOptUInt64). This pins that the generated EventFilter does the same,
    // keeping HasEpoch across an encode and parse, so the migration loses nothing here.
    [Fact]
    public void EventFilterEpochZeroSurvivesTheWire()
    {
        var parsed = EventFilter.Parser.ParseFrom(new EventFilter { Epoch = 0 }.ToByteArray());
        Assert.True(parsed.HasEpoch);
        Assert.Equal(0UL, parsed.Epoch);

        var absent = EventFilter.Parser.ParseFrom(new EventFilter().ToByteArray());
        Assert.False(absent.HasEpoch);
    }

    // The hand-written ProtoWire skipped default values even inside a oneof, so an arm holding
    // "", false, 0, 0.0 or empty bytes reached the receiver as an unset oneof.
    public static TheoryData<AttributeValue, AttributeValue.ValueOneofCase> DefaultArms() => new()
    {
        { new AttributeValue { StringValue = "" }, AttributeValue.ValueOneofCase.StringValue },
        { new AttributeValue { BoolValue = false }, AttributeValue.ValueOneofCase.BoolValue },
        { new AttributeValue { IntegerValue = 0 }, AttributeValue.ValueOneofCase.IntegerValue },
        { new AttributeValue { DoubleValue = 0.0 }, AttributeValue.ValueOneofCase.DoubleValue },
        { new AttributeValue { BytesValue = ByteString.Empty }, AttributeValue.ValueOneofCase.BytesValue },
        { new AttributeValue { ArrayValue = new AttributeArray() }, AttributeValue.ValueOneofCase.ArrayValue },
        { new AttributeValue { MapValue = new AttributeMap() }, AttributeValue.ValueOneofCase.MapValue },
    };

    [Theory]
    [MemberData(nameof(DefaultArms))]
    public void AttributeValueArmHoldingADefaultKeepsItsCase(AttributeValue value, AttributeValue.ValueOneofCase want)
    {
        Assert.Equal(want, value.ValueCase);
        Assert.Equal(want, RoundTrip(value).ValueCase);

        // The same value inside a DecideRequest, the Connect path the defect affected.
        var request = new DecideRequest
        {
            RequestId = "r",
            ProducerEvent = new ProducerEvent { Attributes = { new AttributeEntry { Key = "k", Value = value } } },
        };
        var parsed = RoundTrip(request);
        Assert.Equal(want, parsed.ProducerEvent.Attributes[0].Value.ValueCase);
    }

    // The hand-written AttributeEntry had no claimed_sensitivity = 3 and dropped it on decode.
    [Fact]
    public void AttributeEntryClaimedSensitivitySurvivesTheWire()
    {
        var entry = new AttributeEntry
        {
            Key = "card",
            Value = new AttributeValue { StringValue = "4242" },
            ClaimedSensitivity = Sensitivity.Restricted,
        };
        var parsed = RoundTrip(entry);
        Assert.Equal(Sensitivity.Restricted, parsed.ClaimedSensitivity);
        Assert.Equal("card", parsed.Key);
        Assert.Equal("4242", parsed.Value.StringValue);
    }

    private sealed class NoDecider : ISentinelDecisionServiceClient
    {
        public Task<DecideResult> DecideAsync(DecideRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the request builder must not call Decide");
    }
}
