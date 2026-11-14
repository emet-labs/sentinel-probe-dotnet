using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Sentinel.Probe.Sdk.Client;

namespace Sentinel.Probe.Sdk.Tests;

public class ClientTests
{
    private sealed class StubDecider : ISentinelDecisionServiceClient
    {
        public DecideRequest? LastRequest;
        public DecideResponse Response = new() { Action = DecisionAction.Permit };
        public Task<DecideResult> DecideAsync(DecideRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new DecideResult(Response, null));
        }
    }

    private static ProbeClient NewClient(StubDecider? decider = null, EventFilter? initial = null) =>
        new(new ProbeClient.Config { SourceHandle = "gateway.tool-calls", InitialFilter = initial }, decider ?? new StubDecider());

    [Fact]
    public void CurrentFilterIsNullBeforeFirstSet()
    {
        var c = NewClient();
        Assert.Null(c.CurrentFilter());
        Assert.Null(c.AcknowledgedEpoch());
    }

    [Fact]
    public void SetFilterSwapsAndReportsUpdate()
    {
        var c = NewClient();
        Assert.True(c.SetFilter(new EventFilter { Epoch = 5 }));
        Assert.False(c.SetFilter(new EventFilter { Epoch = 5 })); // unchanged epoch -> no update
        Assert.True(c.SetFilter(new EventFilter { Epoch = 6 }));
        Assert.Equal(6UL, c.AcknowledgedEpoch());
    }

    [Fact]
    public void RefreshOnEpochMirrorsStore()
    {
        var c = NewClient(initial: new EventFilter { Epoch = 5 });
        Assert.False(c.RefreshOnEpoch(5));
        Assert.True(c.RefreshOnEpoch(6));
        Assert.False(c.RefreshOnEpoch(null));
    }

    [Fact]
    public void BuildDecideRequestStampsIdentifiersEpochAndEvent()
    {
        var c = NewClient(initial: new EventFilter { Epoch = 5 });
        var @event = new ProducerEvent { Id = "evt", Kind = "x" };
        var req = c.BuildDecideRequest(@event, "req-9", "idem-9", 1000UL);
        Assert.Equal("req-9", req.RequestId);
        Assert.Equal("idem-9", req.IdempotencyKey);
        Assert.Equal("gateway.tool-calls", req.SourceHandle);
        Assert.True(req.HasFilterEpoch);
        Assert.Equal(5UL, req.FilterEpoch);
        Assert.Same(@event, req.ProducerEvent);
        Assert.True(req.HasRemainingTransportBudgetNanoseconds);
        Assert.Equal(1000UL, req.RemainingTransportBudgetNanoseconds);
    }

    [Fact]
    public void BuildDecideRequestWithoutBudgetLeavesItNull()
    {
        var c = NewClient(initial: new EventFilter { Epoch = 5 });
        var req = c.BuildDecideRequest(new ProducerEvent { Kind = "x" }, "r", "i", null);
        Assert.False(req.HasRemainingTransportBudgetNanoseconds);
    }

    [Fact]
    public void DeciderAndSourceHandleExposed()
    {
        var stub = new StubDecider();
        var c = NewClient(stub);
        Assert.Same(stub, c.Decider());
        Assert.Equal("gateway.tool-calls", c.SourceHandle());
    }

    [Fact]
    public async Task DecideFuncAdaptsTheDecider()
    {
        var stub = new StubDecider { Response = new DecideResponse { Action = DecisionAction.Deny } };
        var c = NewClient(stub);
        var decide = c.DecideFunc();
        var outcome = await decide(c.BuildDecideRequest(new ProducerEvent { Kind = "x" }, "r", "i", null), CancellationToken.None);
        Assert.NotNull(outcome.Response);
        Assert.Equal(DecisionAction.Deny, outcome.Response!.Action);
        Assert.Null(outcome.Error);
        Assert.NotNull(stub.LastRequest);
    }
}
