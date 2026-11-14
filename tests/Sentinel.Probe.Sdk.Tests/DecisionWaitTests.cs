using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Probe.V1;
using Google.Protobuf;
using Sentinel.Probe.Sdk.Enforcement;
using Sentinel.Probe.Sdk.Otlp;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

// The receiver-backed decision wait (#464): bounded by the request's remaining Transport Budget,
// timed by an injected TimeProvider, and releasing its timer and cancellation registration on
// every exit. No test reads a wall clock or sleeps: a ManualTimer fires only when told to.
public class DecisionWaitTests
{
    private const string RequestId = "req-charge-1";

    // decide-response.bin answers req-charge-1 with Deny.
    private static byte[] Answer() => OtlpWireTests.Golden("decide-response.bin");

    private static byte[] AnswerFor(string requestId)
    {
        var response = new DecideResponse { RequestId = requestId, Action = DecisionAction.Permit };
        return OtlpLogs.EncodeRecord(OtlpLogs.DecideResponseType, response.ToByteArray(), requestId, null);
    }

    private static ManualTimeProvider Clock() => ManualTimeProvider.AtUnixMilliseconds(0);

    [Fact]
    public async Task StoredAnswerWinsAndArmsNoTimer()
    {
        var clock = Clock();
        using var receiver = OtlpLogReceiver.Start(clock);
        Assert.Equal(200, receiver.Ingest(Answer()));
        var decision = await receiver.WaitForDecisionAsync(RequestId, null, CancellationToken.None);
        Assert.Equal(DecisionAction.Deny, decision.Action);
        Assert.Empty(clock.Timers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0UL)]
    [InlineData(99UL)] // floors to zero 100 ns ticks
    public async Task NoBudgetFailsAtOnceAndArmsNoTimer(ulong? budget)
    {
        var clock = Clock();
        using var receiver = OtlpLogReceiver.Start(clock);
        var wait = receiver.WaitForDecisionAsync(RequestId, budget, CancellationToken.None);
        Assert.True(wait.IsFaulted);
        await Assert.ThrowsAsync<OtlpExportException>(() => wait);
        Assert.Empty(clock.Timers);
        // Nothing was registered: a later answer is stored, not delivered to a ghost waiter.
        Assert.Equal(200, receiver.Ingest(Answer()));
        var decision = await receiver.WaitForDecisionAsync(RequestId, null, CancellationToken.None);
        Assert.Equal(DecisionAction.Deny, decision.Action);
    }

    [Fact]
    public async Task AnswerAfterAskIsDeliveredAndReleasesTheTimer()
    {
        var clock = Clock();
        using var receiver = OtlpLogReceiver.Start(clock);
        var wait = receiver.WaitForDecisionAsync(RequestId, 40_000_000UL, CancellationToken.None);
        var timer = Assert.Single(clock.Timers);
        Assert.Equal(TimeSpan.FromMilliseconds(40), timer.DueTime);
        Assert.Equal(Timeout.InfiniteTimeSpan, timer.Period);
        Assert.False(wait.IsCompleted);
        Assert.Equal(200, receiver.Ingest(Answer()));
        Assert.Equal(DecisionAction.Deny, (await wait).Action);
        Assert.True(timer.Disposed);
    }

    // The answer lands while the timer is being armed. It must find the waiter (registered before
    // the arm), and the timer handed back afterwards is released at once.
    [Fact]
    public async Task AnswerDeliveredWhileArmingFindsTheWaiter()
    {
        var clock = Clock();
        using var receiver = OtlpLogReceiver.Start(clock);
        clock.OnCreateTimer = _ => Assert.Equal(200, receiver.Ingest(Answer()));
        var wait = receiver.WaitForDecisionAsync(RequestId, 40_000_000UL, CancellationToken.None);
        Assert.Equal(DecisionAction.Deny, (await wait).Action);
        Assert.True(Assert.Single(clock.Timers).Disposed);
    }

    [Fact]
    public async Task ExpiryFailsWithTimeoutAndALateAnswerIsKept()
    {
        var clock = Clock();
        using var receiver = OtlpLogReceiver.Start(clock);
        var wait = receiver.WaitForDecisionAsync(RequestId, 40_000_000UL, CancellationToken.None);
        var timer = Assert.Single(clock.Timers);
        timer.Fire();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => wait);
        Assert.Equal("timeout", error.Message);
        Assert.True(timer.Disposed);
        // The late answer is retained, not delivered to the expired wait and not refused.
        Assert.Equal(200, receiver.Ingest(Answer()));
        var later = await receiver.WaitForDecisionAsync(RequestId, null, CancellationToken.None);
        Assert.Equal(DecisionAction.Deny, later.Action);
        Assert.Single(clock.Timers);
    }

    [Fact]
    public async Task AnswerForAnotherRequestDoesNotAnswer()
    {
        var clock = Clock();
        using var receiver = OtlpLogReceiver.Start(clock);
        var wait = receiver.WaitForDecisionAsync(RequestId, 40_000_000UL, CancellationToken.None);
        Assert.Equal(200, receiver.Ingest(AnswerFor("req-other")));
        Assert.False(wait.IsCompleted);
        var timer = Assert.Single(clock.Timers);
        Assert.False(timer.Disposed);
        timer.Fire();
        await Assert.ThrowsAsync<TimeoutException>(() => wait);
        Assert.True(timer.Disposed);
    }

    [Fact]
    public async Task DisposeFailsPendingWaitsAndLaterOnes()
    {
        var clock = Clock();
        var receiver = OtlpLogReceiver.Start(clock);
        var wait = receiver.WaitForDecisionAsync(RequestId, 40_000_000UL, CancellationToken.None);
        var timer = Assert.Single(clock.Timers);
        receiver.Dispose();
        await Assert.ThrowsAsync<OtlpExportException>(() => wait);
        Assert.True(timer.Disposed);
        var after = receiver.WaitForDecisionAsync("req-2", 40_000_000UL, CancellationToken.None);
        Assert.True(after.IsFaulted);
        await Assert.ThrowsAsync<OtlpExportException>(() => after);
        Assert.Single(clock.Timers);
    }

    [Fact]
    public async Task CancellationReleasesTheTimerAndTheWaiter()
    {
        var clock = Clock();
        using var receiver = OtlpLogReceiver.Start(clock);
        using var cancel = new CancellationTokenSource();
        var wait = receiver.WaitForDecisionAsync(RequestId, 40_000_000UL, cancel.Token);
        var timer = Assert.Single(clock.Timers);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.True(timer.Disposed);
        // The waiter is gone: a new wait for the same id registers instead of colliding (#535).
        var again = receiver.WaitForDecisionAsync(RequestId, 40_000_000UL, CancellationToken.None);
        Assert.Equal(200, receiver.Ingest(Answer()));
        Assert.Equal(DecisionAction.Deny, (await again).Action);
    }

    [Fact]
    public async Task ASecondWaiterForTheSameIdStillThrows()
    {
        var clock = Clock();
        using var receiver = OtlpLogReceiver.Start(clock);
        var wait = receiver.WaitForDecisionAsync(RequestId, 40_000_000UL, CancellationToken.None);
        Assert.Throws<OtlpExportException>(() => { _ = receiver.WaitForDecisionAsync(RequestId, 40_000_000UL, CancellationToken.None); });
        Assert.Single(clock.Timers);
        Assert.Equal(200, receiver.Ingest(Answer()));
        Assert.Equal(DecisionAction.Deny, (await wait).Action);
    }

    // A provider that cannot arm a timer. The wait fails with its error and is released, so it
    // neither leaks nor blocks a later wait for the same request id.
    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public int Calls { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Calls++;
            throw new InvalidOperationException("no timers");
        }
    }

    [Fact]
    public async Task ATimerThatCannotBeArmedFailsAndReleasesTheWait()
    {
        var clock = new ThrowingTimeProvider();
        using var receiver = OtlpLogReceiver.Start(clock);
        var wait = receiver.WaitForDecisionAsync(RequestId, 40_000_000UL, CancellationToken.None);
        Assert.True(wait.IsFaulted);
        Assert.Equal("no timers", (await Assert.ThrowsAsync<InvalidOperationException>(() => wait)).Message);
        // Released, not still registered: a second wait arms again instead of colliding (#535).
        var again = receiver.WaitForDecisionAsync(RequestId, 40_000_000UL, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => again);
        Assert.Equal(2, clock.Calls);
        // And a later answer is stored for the next wait, not handed to a leaked waiter.
        Assert.Equal(200, receiver.Ingest(Answer()));
        var decision = await receiver.WaitForDecisionAsync(RequestId, null, CancellationToken.None);
        Assert.Equal(DecisionAction.Deny, decision.Action);
    }

    [Theory]
    [InlineData(100UL, 1L)]
    [InlineData(199UL, 1L)] // floors, never rounds up past the budget
    [InlineData(1_000_000UL, 10_000L)]
    [InlineData(1_999_999UL, 19_999L)]
    [InlineData(40_000_000UL, 400_000L)]
    public void DueFloorsToWholeTicks(ulong budgetNs, long ticks)
    {
        Assert.Equal(TimeSpan.FromTicks(ticks), OtlpLogReceiver.Due(budgetNs));
    }

    [Fact]
    public void DueIsCappedAtTheTimerLimit()
    {
        var cap = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);
        Assert.Equal(cap, OtlpLogReceiver.Due(ulong.MaxValue));
        Assert.Equal(cap, OtlpLogReceiver.Due((uint.MaxValue - 1UL) * 1_000_000UL));
        Assert.True(OtlpLogReceiver.Due((uint.MaxValue - 2UL) * 1_000_000UL) < cap);
    }

    // The gate arms the wait for exactly the remaining Transport Budget and, on expiry, applies
    // the fail mode. Specification budget 50 ms; clock reads at 1 ms and 11 ms leave 40 ms.
    [Theory]
    [InlineData(false, OutcomeKind.FailOpenPermit)]
    [InlineData(true, OutcomeKind.FailClosedDeny)]
    public async Task GateAppliesTheFailModeWhenTheWaitExpires(bool closed, OutcomeKind want)
    {
        var clock = Clock();
        using var receiver = OtlpLogReceiver.Start(clock);
        var reads = new Queue<long>(new[] { 1_000_000L, 11_000_000L });
        var spec = closed ? TestSupport.ClosedAskAndBlockSpec() : TestSupport.AskAndBlockSpec();
        spec.LatencyBudgetNanoseconds = 50_000_000;
        var options = new Options { SourceHandle = "gateway.tool-calls", RequestId = RequestId, IdempotencyKey = "idem-1" };
        var gate = Gate.GateAsync(
            TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(4UL, spec),
            null,
            new Deps
            {
                Decide = Gate.FromLogReceiver(receiver),
                NowMonotonicNs = () => reads.Dequeue(),
                AcceptedFailModeFor = closed ? TestSupport.AlwaysClosed : TestSupport.AlwaysOpen,
            },
            options,
            CancellationToken.None);
        var timer = Assert.Single(clock.Timers);
        Assert.Equal(TimeSpan.FromMilliseconds(40), timer.DueTime);
        Assert.Empty(reads);
        timer.Fire();
        var outcome = await gate;
        Assert.Equal(want, outcome.Kind);
        Assert.True(timer.Disposed);
    }

    [Fact]
    public async Task GateAnswerAfterAskIsHonoured()
    {
        var clock = Clock();
        using var receiver = OtlpLogReceiver.Start(clock);
        var reads = new Queue<long>(new[] { 1_000_000L, 11_000_000L });
        var spec = TestSupport.AskAndBlockSpec();
        spec.LatencyBudgetNanoseconds = 50_000_000;
        var options = new Options { SourceHandle = "gateway.tool-calls", RequestId = RequestId, IdempotencyKey = "idem-1" };
        var gate = Gate.GateAsync(
            TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(4UL, spec),
            null,
            new Deps
            {
                Decide = Gate.FromLogReceiver(receiver),
                NowMonotonicNs = () => reads.Dequeue(),
                AcceptedFailModeFor = TestSupport.AlwaysOpen,
            },
            options,
            CancellationToken.None);
        Assert.Equal(200, receiver.Ingest(Answer()));
        var outcome = await gate;
        Assert.Equal(OutcomeKind.Deny, outcome.Kind);
        Assert.True(Assert.Single(clock.Timers).Disposed);
    }
}
