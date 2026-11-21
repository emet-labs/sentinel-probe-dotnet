using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using Google.Protobuf;
using Sentinel.Probe.Sdk.Emission;
using Sentinel.Probe.Sdk.Int128;
using Sentinel.Probe.Sdk.Otlp;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

public class CompletenessHeartbeatTests
{
    private const long StartMs = 1_794_000_000_000L;
    private const long StartNs = StartMs * 1_000_000L;

    private readonly ManualTimeProvider _clock = ManualTimeProvider.AtUnixMilliseconds(StartMs);
    private readonly SequenceAllocator _sequence;
    private readonly List<ProducerCompletenessSignal> _sent = new();
    private readonly List<Exception> _errors = new();

    public CompletenessHeartbeatTests()
    {
        _sequence = new SequenceAllocator(_clock);
    }

    private Task Record(ProducerCompletenessSignal signal, CancellationToken cancellationToken)
    {
        lock (_sent) _sent.Add(signal);
        return Task.CompletedTask;
    }

    private CompletenessHeartbeat Heartbeat(
        Func<ProducerCompletenessSignal, CancellationToken, Task>? send = null,
        Func<ulong?>? filterEpoch = null,
        TimeSpan? interval = null) =>
        new(_sequence, send ?? Record, filterEpoch, _clock, interval ?? TimeSpan.Zero, error =>
        {
            lock (_errors) _errors.Add(error);
        });

    private int SentCount()
    {
        lock (_sent) return _sent.Count;
    }

    [Fact]
    public async Task TheEncodedSignalSetsPresenceOnZeroCountersAndAZeroFilterEpoch()
    {
        var reports = await Heartbeat().ReportAsync();
        Assert.Equal(new[] { new CompletenessReport(_sequence.Epoch(), 0, 0, 0, true) }, reports);
        var signal = ProducerCompletenessSignal.Parser.ParseFrom(Assert.Single(_sent).ToByteArray());
        Assert.True(signal.HasLastEmitted);
        Assert.True(signal.HasCompleteThrough);
        Assert.True(signal.HasAcknowledgedFilterEpoch);
        Assert.Equal(0UL, signal.LastEmitted);
        Assert.Equal(0UL, signal.CompleteThrough);
        Assert.Equal(0UL, signal.AcknowledgedFilterEpoch);
        Assert.Equal(SourceActivityState.Active, signal.Activity);
        foreach (var time in new[] { signal.ProducerReportedAt, signal.ActivitySince })
        {
            Assert.Equal("unix", time.ClockDomainId);
            Assert.Equal(0UL, time.UncertaintyNanoseconds);
            Assert.Equal(StartNs, (long)Int128Codec.ToBigInteger(time.Nanoseconds));
        }
    }

    [Fact]
    public void BuildSetsEveryFieldTheRestatementCheckCompares()
    {
        var signal = CompletenessSignals.Build(new EpochDeclaration(7, 3, 1), 2, SourceActivityState.IntentionallyIdle, 20, 10);
        Assert.Equal(7UL, signal.SequenceEpoch);
        Assert.Equal(3UL, signal.LastEmitted);
        Assert.Equal(1UL, signal.CompleteThrough);
        Assert.Equal(2UL, signal.AcknowledgedFilterEpoch);
        Assert.Equal(SourceActivityState.IntentionallyIdle, signal.Activity);
        Assert.Equal(20L, (long)Int128Codec.ToBigInteger(signal.ProducerReportedAt.Nanoseconds));
        Assert.Equal(10L, (long)Int128Codec.ToBigInteger(signal.ActivitySince.Nanoseconds));
    }

    private static long NanosOf(OccurrenceTime time) => (long)Int128Codec.ToBigInteger(time.Nanoseconds);

    private void HookEntryThenExport(bool exported = true)
    {
        _sequence.NoteAttempt();
        var slot = _sequence.Allocate();
        if (exported)
        {
            _sequence.MarkExported(slot);
        }
    }

    [Fact]
    public void ALegacyDeclarationSendsNoneOfTheFourEpochChainFields()
    {
        var signal = CompletenessSignals.Build(new EpochDeclaration(7, 3, 1), 0, SourceActivityState.Active, 20, 10);
        Assert.False(signal.HasAttempted);
        Assert.False(signal.HasPredecessorEpoch);
        Assert.Null(signal.SealedAt);
        Assert.False(signal.HasSuperseded);
    }

    [Fact]
    public async Task AFreshEpochRowCarriesAttemptedAndNoPredecessorSealedAtOrSuperseded()
    {
        HookEntryThenExport(exported: false);
        await Heartbeat().ReportAsync();
        var signal = Assert.Single(_sent);
        Assert.True(signal.HasAttempted);
        Assert.Equal(1UL, signal.Attempted);
        Assert.False(signal.HasPredecessorEpoch);
        Assert.Null(signal.SealedAt);
        Assert.False(signal.HasSuperseded);
    }

    [Fact]
    public async Task ALostAttemptShowsAttemptedAboveLastEmittedOnTheWire()
    {
        HookEntryThenExport();
        _sequence.NoteAttempt(); // the hook entry failed before Allocate()
        await Heartbeat().ReportAsync();
        var signal = Assert.Single(_sent);
        Assert.Equal(1UL, signal.LastEmitted);
        Assert.Equal(2UL, signal.Attempted);
    }

    [Fact]
    public async Task AllocateCloseAllocateRollsWithThePredecessorAndResendsTheUnacceptedSealedEpochSuperseded()
    {
        var seen = new List<ProducerCompletenessSignal>();
        var refuse = true;
        var heartbeat = Heartbeat(send: (signal, ct) =>
        {
            seen.Add(signal);
            if (refuse)
            {
                return Task.FromException(new Sentinel.Probe.Sdk.Otlp.OtlpExportException("HTTP 503"));
            }
            return Record(signal, ct);
        });
        var first = _sequence.Epoch();
        HookEntryThenExport();
        _clock.SetUnixMilliseconds(StartMs + 1);
        await heartbeat.ReportAsync();
        var sealedRow = Assert.Single(seen);
        Assert.NotNull(sealedRow.SealedAt);
        var sealedAt = NanosOf(sealedRow.SealedAt);
        Assert.Equal(NanosOf(sealedRow.ProducerReportedAt), sealedAt);
        refuse = false;
        _clock.SetUnixMilliseconds(StartMs + 6);
        HookEntryThenExport(exported: false);
        await heartbeat.ReportAsync();
        Assert.Equal(2, _sent.Count);
        var resend = _sent[0];
        var current = _sent[1];
        Assert.Equal(first, resend.SequenceEpoch);
        Assert.True(resend.HasSuperseded && resend.Superseded);
        Assert.Equal(sealedAt, NanosOf(resend.SealedAt));
        Assert.Equal(sealedRow.Attempted, resend.Attempted);
        Assert.Equal(sealedRow.LastEmitted, resend.LastEmitted);
        Assert.True(NanosOf(resend.ProducerReportedAt) >= sealedAt);
        Assert.False(resend.HasPredecessorEpoch);
        Assert.Equal(first, current.PredecessorEpoch);
        Assert.False(current.HasSuperseded);
        Assert.Null(current.SealedAt);
        Assert.Equal(1UL, current.Attempted);
    }

    [Fact]
    public async Task SealedAtIsConstantAcrossIdleRedeclarationsAndNotAfterAnyLaterReport()
    {
        var heartbeat = Heartbeat();
        HookEntryThenExport();
        await heartbeat.ReportAsync();
        var sealedAt = NanosOf(Assert.Single(_sent).SealedAt);
        for (var i = 1; i <= 3; i++)
        {
            _clock.SetUnixMilliseconds(StartMs + (i * 1_000));
            await heartbeat.ReportAsync();
        }
        Assert.Equal(4, _sent.Count);
        foreach (var signal in _sent)
        {
            Assert.Equal(sealedAt, NanosOf(signal.SealedAt));
            Assert.True(sealedAt <= NanosOf(signal.ProducerReportedAt));
        }
    }

    [Fact]
    public async Task ARowIsNeverReportedBeforeTheInstantItsEpochWasSealedEvenOnAnotherClock()
    {
        var ahead = ManualTimeProvider.AtUnixMilliseconds(StartMs + 10_000);
        var sequence = new SequenceAllocator(ahead);
        sequence.NoteAttempt();
        sequence.MarkExported(sequence.Allocate());
        var heartbeat = new CompletenessHeartbeat(sequence, Record, null, _clock, TimeSpan.Zero);
        await heartbeat.ReportAsync();
        var signal = Assert.Single(_sent);
        Assert.True(NanosOf(signal.SealedAt) <= NanosOf(signal.ProducerReportedAt));
    }


    [Fact]
    public async Task ASendThatThrowsIsReportedAndIsNotAccepted()
    {
        var refusal = new OtlpExportException("HTTP 409");
        var reports = await Heartbeat(send: (_, _) => throw refusal).ReportAsync();
        Assert.Equal(new[] { new CompletenessReport(_sequence.Epoch(), 0, 0, 0, false) }, reports);
        Assert.Same(refusal, Assert.Single(_errors));
    }

    [Fact]
    public async Task AFilterEpochRegressionSendsNothingAndReportsTheStableCode()
    {
        ulong filter = 3;
        var heartbeat = Heartbeat(filterEpoch: () => filter);
        _sequence.Allocate();
        await heartbeat.ReportAsync();
        filter = 2;
        Assert.Empty(await heartbeat.ReportAsync());
        Assert.Single(_sent);
        Assert.Equal(SequenceException.FilterEpochRegressed, Assert.IsType<SequenceException>(Assert.Single(_errors)).Code);
    }

    [Fact]
    public async Task AReportRequestedDuringASendCoalescesIntoOneFollowUpBuiltAfterIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = Heartbeat(send: async (signal, cancellationToken) =>
        {
            int count;
            lock (_sent)
            {
                _sent.Add(signal);
                count = _sent.Count;
            }
            if (count == 1)
            {
                entered.SetResult();
                await release.Task;
            }
        });
        var first = Task.Run(() => heartbeat.ReportAsync());
        await entered.Task;
        var followers = Enumerable.Range(0, 3).Select(_ => heartbeat.ReportAsync()).ToArray();
        Assert.All(followers, follower => Assert.False(follower.IsCompleted));
        _sequence.Allocate();
        release.SetResult();
        await first;
        var results = await Task.WhenAll(followers);
        Assert.Equal(2, SentCount());
        Assert.Equal(0UL, _sent[0].LastEmitted);
        Assert.Equal(1UL, _sent[1].LastEmitted);
        var expected = new[] { new CompletenessReport(_sequence.Epoch(), 1, 0, 0, true) };
        Assert.All(results, result => Assert.Equal(expected, result));
    }

    [Fact]
    public async Task StopSendsNothingAndALateTickIsInert()
    {
        var heartbeat = Heartbeat(interval: TimeSpan.FromSeconds(15));
        heartbeat.Start();
        var timer = Assert.Single(_clock.Timers);
        heartbeat.Stop();
        Assert.True(timer.Disposed);
        timer.Fire();
        Assert.Empty(await heartbeat.ReportAsync());
        Assert.Empty(await heartbeat.ShutdownAsync());
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task ShutdownWaitsForARunningReportAndThenSendsOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = Heartbeat(send: async (signal, cancellationToken) =>
        {
            int count;
            lock (_sent)
            {
                _sent.Add(signal);
                count = _sent.Count;
            }
            if (count == 1)
            {
                entered.SetResult();
                await release.Task;
            }
        });
        var slot = _sequence.Allocate();
        var running = Task.Run(() => heartbeat.ReportAsync());
        await entered.Task;
        var closing = heartbeat.ShutdownAsync();
        Assert.False(closing.IsCompleted);
        _sequence.MarkExported(slot);
        release.SetResult();
        await running;
        var final = await closing;
        Assert.Equal(new[] { new CompletenessReport(slot.Epoch, 1, 1, 0, true) }, final);
        Assert.Equal(new[] { 0UL, 1UL }, _sent.Select(signal => signal.CompleteThrough).ToArray());
        Assert.Empty(await heartbeat.ReportAsync());
        Assert.Equal(2, SentCount());
    }

    [Fact]
    public async Task TheTimerRunsOneReportPerTickAndNoneAfterShutdown()
    {
        var reported = new SemaphoreSlim(0);
        var heartbeat = Heartbeat(
            send: (signal, cancellationToken) =>
            {
                lock (_sent) _sent.Add(signal);
                reported.Release();
                return Task.CompletedTask;
            },
            interval: TimeSpan.FromSeconds(15));
        heartbeat.Start();
        heartbeat.Start();
        var timer = Assert.Single(_clock.Timers);
        Assert.Equal(TimeSpan.FromSeconds(15), timer.Period);
        timer.Fire();
        await reported.WaitAsync();
        timer.Fire();
        await reported.WaitAsync();
        Assert.Equal(2, SentCount());
        await heartbeat.ShutdownAsync();
        Assert.Equal(3, SentCount());
        Assert.True(timer.Disposed);
        timer.Fire();
        Assert.Equal(3, SentCount());
    }

    [Fact]
    public async Task AFailingTimedReportIsReportedAndTheTimerKeepsRunning()
    {
        var reported = new SemaphoreSlim(0);
        var errored = new SemaphoreSlim(0);
        var failure = new InvalidOperationException("clock unavailable");
        var heartbeat = new CompletenessHeartbeat(
            _sequence,
            (signal, cancellationToken) =>
            {
                lock (_sent) _sent.Add(signal);
                reported.Release();
                return Task.CompletedTask;
            },
            null,
            _clock,
            TimeSpan.FromSeconds(15),
            error =>
            {
                lock (_errors) _errors.Add(error);
                errored.Release();
            });
        heartbeat.Start();
        var timer = Assert.Single(_clock.Timers);
        _clock.FailNextRead = failure;
        timer.Fire();
        await errored.WaitAsync();
        Assert.Same(failure, Assert.Single(_errors));
        timer.Fire();
        await reported.WaitAsync();
        Assert.Equal(1, SentCount());
        heartbeat.Stop();
    }

    [Fact]
    public void AnIntervalOfZeroArmsNoTimer()
    {
        Heartbeat(interval: TimeSpan.Zero).Start();
        Assert.Empty(_clock.Timers);
    }

    [Fact]
    public void TheIntervalMustBeZeroOrBetweenOneSecondAndTheSilenceBound()
    {
        foreach (var milliseconds in new[] { -1, 999, 60_000 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Heartbeat(interval: TimeSpan.FromMilliseconds(milliseconds)));
        }
        foreach (var milliseconds in new[] { 0, 1_000, 59_999 })
        {
            Heartbeat(interval: TimeSpan.FromMilliseconds(milliseconds));
        }
        Assert.Equal(TimeSpan.FromSeconds(15), CompletenessHeartbeat.DefaultInterval);
    }
}
