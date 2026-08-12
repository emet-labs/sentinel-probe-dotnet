using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Sentinel.Probe.Sdk.Client;
using Sentinel.Probe.Sdk.Connect;
using Sentinel.Probe.Sdk.Enforcement;

namespace Sentinel.Probe.Sdk.Tests;

public class TransportLoopbackTests
{
    private static readonly Options Options = TestSupport.TestOptions;

    private static async Task<GateOutcome> RunOverWire(LoopbackServer server, EventFilter filter, Func<SpecificationFilter, FailMode> accepted)
    {
        var transport = new SentinelTransport(new SentinelTransportOptions { BaseURL = server.BaseURL });
        DecideFunc decide = ProbeClient.DecideFunc(transport);
        return await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind), filter, 10000L,
            new Deps { Decide = decide, NowMonotonicNs = () => 0, AcceptedFailModeFor = accepted },
            Options, CancellationToken.None);
    }

    [Fact]
    public async Task ConnectLoopbackDeny()
    {
        using var server = new LoopbackServer(DecisionAction.DecisionActionDeny);
        var outcome = await RunOverWire(server, TestSupport.MakeFilter(5UL, TestSupport.AskAndBlockSpec()), TestSupport.AlwaysOpen);
        Assert.Equal(OutcomeKind.Deny, outcome.Kind);

        // The request survived proto encoding, HTTP transport and decoding intact.
        var received = Assert.Single(server.Requests);
        Assert.Equal("req-1", received.RequestId);
        Assert.Equal("idem-1", received.IdempotencyKey);
        Assert.Equal(TestSupport.TestKind, received.ProducerEvent!.Kind);
        Assert.Equal(5UL, received.FilterEpoch);
        Assert.Equal(10000UL, received.RemainingTransportBudgetNanoseconds);
    }

    [Fact]
    public async Task ConnectLoopbackPermit()
    {
        using var server = new LoopbackServer(DecisionAction.DecisionActionPermit);
        var outcome = await RunOverWire(server, TestSupport.MakeFilter(5UL, TestSupport.AskAndBlockSpec()), TestSupport.AlwaysOpen);
        Assert.Equal(OutcomeKind.Permit, outcome.Kind);
    }

    // The handler returns a coded error; the transport surfaces a ConnectException and the gate
    // routes it into the contracted fail mode with the code recorded for audit.
    [Fact]
    public async Task ConnectLoopbackServerErrorFailsClosed()
    {
        using var server = new LoopbackServer(DecisionAction.DecisionActionPermit,
            new ConnectException(ConnectCode.Unavailable, "sentinel unavailable"));
        var outcome = await RunOverWire(server, TestSupport.MakeFilter(5UL, TestSupport.ClosedAskAndBlockSpec()), TestSupport.AlwaysClosed);
        Assert.Equal(OutcomeKind.FailClosedDeny, outcome.Kind);
        Assert.StartsWith("connect-unavailable", outcome.Reason);
    }

    // Nothing is listening, so the client's own transport error reaches the gate.
    [Fact]
    public async Task ConnectLoopbackUnreachableEndpointFailsOpen()
    {
        var dead = new LoopbackServer(DecisionAction.DecisionActionPermit);
        string url = dead.BaseURL;
        dead.Dispose(); // stop the listener; nothing is listening any more

        var transport = new SentinelTransport(new SentinelTransportOptions { BaseURL = url });
        DecideFunc decide = ProbeClient.DecideFunc(transport);
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(5UL, TestSupport.AskAndBlockSpec()), 10000L,
            new Deps { Decide = decide, NowMonotonicNs = () => 0, AcceptedFailModeFor = TestSupport.AlwaysOpen },
            Options, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailOpenPermit, outcome.Kind);
        Assert.NotEmpty(outcome.Reason);
    }

    // The transport round-trips a populated ProducerEvent over the wire.
    [Fact]
    public async Task ConnectLoopbackPreservesEventAttributes()
    {
        using var server = new LoopbackServer(DecisionAction.DecisionActionPermit);
        var transport = new SentinelTransport(new SentinelTransportOptions { BaseURL = server.BaseURL });

        var @event = new ProducerEvent
        {
            Id = "evt-9",
            Kind = TestSupport.TestKind,
            SchemaVersion = "sentinel.model.v1",
            AcknowledgedFilterEpoch = 7,
            Attributes = new()
            {
                new AttributeEntry { Key = "amount", Value = new AttributeValue { Case = AttributeValueOneofCase.IntegerValue, IntegerValue = -4200 } },
                new AttributeEntry { Key = "tag", Value = new AttributeValue { Case = AttributeValueOneofCase.StringValue, StringValue = "x" } },
            },
            ClaimedSensitivity = Sensitivity.SensitivityConfidential,
        };
        var request = new DecideRequest
        {
            RequestId = "req-1",
            IdempotencyKey = "idem-1",
            SourceHandle = "gateway.tool-calls",
            FilterEpoch = 5,
            ProducerEvent = @event,
        };
        var result = await transport.DecideAsync(request, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.NotNull(result.Response);

        var received = Assert.Single(server.Requests);
        Assert.Equal("evt-9", received.ProducerEvent!.Id);
        Assert.Equal(7UL, received.ProducerEvent!.AcknowledgedFilterEpoch);
        Assert.Equal(2, received.ProducerEvent!.Attributes.Count);
        Assert.Equal(-4200L, received.ProducerEvent!.Attributes[0].Value!.IntegerValue);
        Assert.Equal(Sensitivity.SensitivityConfidential, received.ProducerEvent!.ClaimedSensitivity);
    }
}
