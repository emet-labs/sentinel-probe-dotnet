using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Sentinel.Probe.Sdk.Connect;
using Sentinel.Probe.Sdk.Enforcement;

namespace Sentinel.Probe.Sdk.Tests;

public class GateTests
{
    private static readonly CancellationToken CanceledToken;

    static GateTests()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        CanceledToken = cts.Token;
    }

    private static long? I64(long v) => v;
    private static ulong? U64(ulong v) => v;

    [Fact]
    public async Task Permit()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionPermit) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);

        Assert.Equal(OutcomeKind.Permit, outcome.Kind);
        Assert.Equal(TestSupport.TestEpoch, outcome.FilterEpoch);
        Assert.True(outcome.Kind.Permitted());
    }

    [Fact]
    public async Task Deny()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionDeny) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.Deny, outcome.Kind);
    }

    [Fact]
    public async Task DeferWithBudgetRemaining()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionDefer) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.Defer, outcome.Kind);
    }

    [Fact]
    public async Task DeferBudgetExhaustedFailsOpen()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionDefer) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 20000), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailOpenPermit, outcome.Kind);
    }

    [Fact]
    public async Task DeferBudgetExhaustedFailsClosedWhenContracted()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionDefer) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.ClosedAskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 20000, TestSupport.AlwaysClosed), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailClosedDeny, outcome.Kind);
    }

    [Fact]
    public async Task TransportErrorFailsOpenByDefault()
    {
        var mock = new MockDecider { Error = new Exception("boom") };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailOpenPermit, outcome.Kind);
    }

    [Fact]
    public async Task TransportErrorFailsClosedWhenContracted()
    {
        var mock = new MockDecider { Error = new Exception("boom") };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.ClosedAskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0, TestSupport.AlwaysClosed), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailClosedDeny, outcome.Kind);
    }

    // Declaring CLOSED is not enough; an operator must have agreed to be blocked.
    [Fact]
    public async Task FailClosedRequiresAnAcceptedContract()
    {
        var mock = new MockDecider { Error = new Exception("boom") };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.ClosedAskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0, TestSupport.AlwaysOpen), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailOpenPermit, outcome.Kind);
    }

    // A missing contract source is a wiring bug, not a default.
    [Fact]
    public async Task NilAcceptedFailModeForThrows()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionPermit) };
        var deps = new Deps
        {
            Decide = mock.DecideAsync,
            NowMonotonicNs = () => 0,
            AcceptedFailModeFor = null!,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), deps, TestSupport.TestOptions, CancellationToken.None));
    }

    [Fact]
    public async Task WithoutEnforcingSpecsNeedsNoContractSource()
    {
        var mock = new MockDecider();
        var deps = new Deps
        {
            Decide = mock.DecideAsync,
            NowMonotonicNs = () => 0,
            AcceptedFailModeFor = null!,
        };
        // No ask-and-block spec -> ship-async, no contract source required.
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.ShipAsyncSpec()),
            I64(10000), deps, TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.Permit, outcome.Kind);
        Assert.Equal(0, mock.CallCount);

        // No filter at all needs no dependencies either.
        var noDeps = new Deps { Decide = mock.DecideAsync, NowMonotonicNs = () => 0, AcceptedFailModeFor = null! };
        var noFilter = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind), null, I64(10000), noDeps, TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.NoFilter, noFilter.Kind);
    }

    [Fact]
    public async Task AggregateFailClosedWins()
    {
        var mock = new MockDecider { Error = new Exception("boom") };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch),
                TestSupport.AskAndBlockSpec(), // open
                TestSupport.ClosedAskAndBlockSpec()), // closed + accepted
            I64(10000), TestSupport.MakeDeps(mock, 0, TestSupport.AlwaysClosed), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailClosedDeny, outcome.Kind);
    }

    [Fact]
    public async Task UnspecifiedFailModeIsOpen()
    {
        var mock = new MockDecider { Error = new Exception("boom") };
        var spec = TestSupport.MakeSpec("s", new() { TestSupport.TestKind }, FailMode.FailModeUnspecified, DeliveryMode.DeliveryModeAskAndBlock);
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), spec),
            I64(10000), TestSupport.MakeDeps(mock, 0, _ => FailMode.FailModeOpen), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailOpenPermit, outcome.Kind);
    }

    [Fact]
    public async Task ShipAsyncSpecsArePermittedWithoutAsking()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionPermit) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.ShipAsyncSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.Permit, outcome.Kind);
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public async Task NonSelectingSpecIsNotEnforcing()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionPermit) };
        var spec = TestSupport.MakeSpec("s", new() { "other.kind" }, FailMode.FailModeClosed, DeliveryMode.DeliveryModeAskAndBlock);
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), spec),
            I64(10000), TestSupport.MakeDeps(mock, 0, TestSupport.AlwaysClosed), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.Permit, outcome.Kind);
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public async Task BudgetExhaustedSkipsDecide()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionPermit) };
        await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 20000), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public async Task UnspecifiedActionIsNotABlindPermit()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionUnspecified) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailOpenPermit, outcome.Kind);
    }

    [Fact]
    public async Task UnspecifiedActionFailsClosedWhenContracted()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionUnspecified) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.ClosedAskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0, TestSupport.AlwaysClosed), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailClosedDeny, outcome.Kind);
    }

    [Fact]
    public async Task BudgetComesFromTheInjectedClock()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionPermit) };
        await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 3000), TestSupport.TestOptions, CancellationToken.None);
        var req = mock.LastRequest!;
        Assert.Equal(7000UL, req.RemainingTransportBudgetNanoseconds);
    }

    [Fact]
    public async Task WithoutBudgetPermits()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionPermit) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            null, TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.Permit, outcome.Kind);
        Assert.Null(mock.LastRequest!.RemainingTransportBudgetNanoseconds);
    }

    [Fact]
    public async Task WithoutBudgetDefersIndefinitely()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionDefer) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            null, TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.Defer, outcome.Kind);
    }

    [Fact]
    public async Task WithoutBudgetTransportErrorFailsOpen()
    {
        var mock = new MockDecider { Error = new Exception("boom") };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            null, TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailOpenPermit, outcome.Kind);
    }

    [Fact]
    public async Task NoFilterHeld()
    {
        var mock = new MockDecider();
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind), null,
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.NoFilter, outcome.Kind);
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public async Task FilterWithoutEpochIsNoFilter()
    {
        var mock = new MockDecider();
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(null, TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.NoFilter, outcome.Kind);
    }

    [Fact]
    public async Task EpochZeroIsAFilter()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionPermit) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(0), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.Permit, outcome.Kind);
        Assert.Equal(0UL, outcome.FilterEpoch);
    }

    [Fact]
    public async Task RequestCarriesIdentifiersAndEvent()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionPermit) };
        var @event = TestSupport.MakeEvent(TestSupport.TestKind);
        var options = new Options { SourceHandle = "gateway.tool-calls", RequestId = "req-1", IdempotencyKey = "idem-key-42" };
        await Gate.GateAsync(@event, TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), options, CancellationToken.None);
        var req = mock.LastRequest!;
        Assert.Equal("idem-key-42", req.IdempotencyKey);
        Assert.Equal("req-1", req.RequestId);
        Assert.Equal("gateway.tool-calls", req.SourceHandle);
        Assert.Same(@event, req.ProducerEvent);
        Assert.Equal(TestSupport.TestEpoch, req.FilterEpoch);
    }

    [Fact]
    public async Task AuditsFilterEpochInEveryOutcome()
    {
        const ulong epoch = 42;
        var cases = new (string name, MockDecider mock)[]
        {
            ("permit", new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionPermit) }),
            ("deny", new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionDeny) }),
            ("defer", new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionDefer) }),
            ("unspecified", new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionUnspecified) }),
            ("transport error", new MockDecider { Error = new Exception("boom") }),
        };
        foreach (var (name, mock) in cases)
        {
            var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
                TestSupport.MakeFilter(epoch, TestSupport.AskAndBlockSpec()),
                I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
            Assert.Equal(epoch, outcome.FilterEpoch);
            Assert.NotEmpty(outcome.Reason);
            Assert.True(name.Length > 0); // keep name referenced
        }
    }

    [Fact]
    public async Task SurfacesSpecificationDecisions()
    {
        var reason = UnresolvedReason.UnresolvedReasonEvidenceGap;
        var mock = new MockDecider
        {
            Response = TestSupport.MakeResponse(DecisionAction.DecisionActionDefer,
                TestSupport.MakeDecision("spec-1", DecisionAction.DecisionActionDefer, reason)),
        };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.Defer, outcome.Kind);
        var decision = Assert.Single(outcome.Specifications);
        Assert.Equal("spec-1", decision.SpecificationId);
        Assert.Equal(UnresolvedReason.UnresolvedReasonEvidenceGap, decision.UnresolvedReason);
    }

    [Fact]
    public async Task SurfacesSpecificationDecisionsOnFailMode()
    {
        var reason = UnresolvedReason.UnresolvedReasonTimeout;
        var mock = new MockDecider
        {
            Response = TestSupport.MakeResponse(DecisionAction.DecisionActionUnspecified,
                TestSupport.MakeDecision("spec-1", DecisionAction.DecisionActionUnspecified, reason)),
        };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailOpenPermit, outcome.Kind);
        var decision = Assert.Single(outcome.Specifications);
        Assert.NotNull(decision.UnresolvedReason);
    }

    // A dead cancellation token is a transport error: never a permit, never a throw, no ask issued.
    [Fact]
    public async Task AlreadyCancelledContextTakesTheFailModePath()
    {
        var mock = new MockDecider { Response = TestSupport.MakeResponse(DecisionAction.DecisionActionPermit) };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            null, TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CanceledToken);
        Assert.Equal(OutcomeKind.FailOpenPermit, outcome.Kind);
        Assert.Equal(0, mock.CallCount);
        Assert.StartsWith("context-canceled", outcome.Reason);
    }

    [Fact]
    public async Task AlreadyCancelledContextFailsClosedWhenContracted()
    {
        var mock = new MockDecider();
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.ClosedAskAndBlockSpec()),
            null, TestSupport.MakeDeps(mock, 0, TestSupport.AlwaysClosed), TestSupport.TestOptions, CanceledToken);
        Assert.Equal(OutcomeKind.FailClosedDeny, outcome.Kind);
    }

    [Fact]
    public async Task ClassifiesContextDeadlineFromDecide()
    {
        var mock = new MockDecider { Error = new TimeoutException("deadline") };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            null, TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailOpenPermit, outcome.Kind);
        Assert.StartsWith("context-deadline-exceeded", outcome.Reason);
    }

    [Fact]
    public async Task ClassifiesConnectErrors()
    {
        var mock = new MockDecider { Error = new ConnectException(ConnectCode.Unavailable, "endpoint down") };
        var outcome = await Gate.GateAsync(TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(U64(TestSupport.TestEpoch), TestSupport.AskAndBlockSpec()),
            I64(10000), TestSupport.MakeDeps(mock, 0), TestSupport.TestOptions, CancellationToken.None);
        Assert.Equal(OutcomeKind.FailOpenPermit, outcome.Kind);
        Assert.StartsWith("connect-unavailable", outcome.Reason);
    }

    [Fact]
    public void OutcomeKindNameAndPermitted()
    {
        Assert.Equal("permit", OutcomeKind.Permit.Name());
        Assert.Equal("deny", OutcomeKind.Deny.Name());
        Assert.Equal("defer", OutcomeKind.Defer.Name());
        Assert.Equal("fail-open-permit", OutcomeKind.FailOpenPermit.Name());
        Assert.Equal("fail-closed-deny", OutcomeKind.FailClosedDeny.Name());
        Assert.Equal("no-filter", OutcomeKind.NoFilter.Name());
        Assert.Equal("unspecified", OutcomeKind.Unspecified.Name());
        // The zero value must never read as permitted.
        Assert.False(OutcomeKind.Unspecified.Permitted());
        Assert.True(OutcomeKind.NoFilter.Permitted());
        Assert.True(OutcomeKind.FailOpenPermit.Permitted());
        Assert.False(OutcomeKind.Defer.Permitted());
        Assert.False(OutcomeKind.FailClosedDeny.Permitted());
    }
}
