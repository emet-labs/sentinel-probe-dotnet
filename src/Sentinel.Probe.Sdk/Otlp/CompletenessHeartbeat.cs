using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Emission;

namespace Sentinel.Probe.Sdk.Otlp;

// CompletenessHeartbeat reports a SequenceAllocator's declaration to Sentinel (#461).
//
// Each report builds one ProducerCompletenessSignal from the allocator's Declare() at the moment
// it is sent (CompletenessSignals.Build). Between two reports with nothing new, every field except
// producer_reported_at's nanoseconds is identical, so Sentinel sees a restatement and writes no
// Verdict revision.
//
// Guarantees, in order of importance:
//   - complete_through comes from the allocator, so it never runs ahead of a successful export;
//   - a report never regresses the epoch, the counters or the acknowledged Filter epoch, and never
//     advances a declared close (the allocator opens a new epoch instead);
//   - before the first accepted signal of a new epoch, the previous epoch's final declaration is
//     restated once per report, so a close whose post failed, or a Filter acknowledgement that
//     arrived while the epoch was closed, is not lost at the roll.
//
// Reports run one at a time. A ReportAsync requested while one is sending coalesces with every
// other such request into one follow-up, built after the running one. The clock and the timer
// both come from the injected TimeProvider (a PeriodicTimer over it), and the send function is
// injected, so tests read no wall clock and arm no real timer.

/// <summary>One signal a report sent, and whether Sentinel accepted it.</summary>
public sealed record CompletenessReport(
    ulong SequenceEpoch,
    ulong LastEmitted,
    ulong CompleteThrough,
    ulong AcknowledgedFilterEpoch,
    bool Accepted);

/// <summary>Sends a source's completeness declaration to Sentinel on a cadence.</summary>
public sealed class CompletenessHeartbeat : IDisposable
{
    /// <summary>The default report interval.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    /// <summary>The shortest non-zero interval.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>The longest interval, below the 60 s active-silence bound.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMilliseconds(59_999);

    private const long NanosecondsPerTick = 100;

    private readonly SequenceAllocator _sequence;
    private readonly Func<ProducerCompletenessSignal, CancellationToken, Task> _send;
    private readonly Func<ulong?> _filterEpoch;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly Action<Exception> _onError;

    // _runLock serialises reports; _lastAccepted and _ackFloor are touched only under it.
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private long _runsStarted;
    private volatile Run? _lastRun;
    private Accepted? _lastAccepted;
    private readonly Dictionary<ulong, ulong> _ackFloor = new();

    // _stateLock guards the activity fields, which DeclareIdle and DeclareActive change from any
    // thread.
    private readonly object _stateLock = new();
    private SourceActivityState _activity = SourceActivityState.Active;
    private long _activitySince;
    private (ulong Epoch, ulong LastEmitted)? _idleMark;
    private long _lastReportedAt;

    private volatile bool _stopped;
    private readonly object _scheduleLock = new();
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _loopCancellation;

    private sealed record Accepted(EpochDeclaration Declaration, ulong AcknowledgedFilterEpoch);

    private sealed record Run(long Id, IReadOnlyList<CompletenessReport> Reports);

    private readonly record struct Moment(long ReportedAt, SourceActivityState Activity, long ActivitySince);

    /// <param name="sequence">The allocator whose declaration this heartbeat reports.</param>
    /// <param name="send">
    /// Posts one signal and completes only when Sentinel accepted it, for example
    /// <c>(signal, ct) =&gt; exporter.ExportCompletenessAsync(handle, signal, ct)</c>. A fault is a
    /// signal Sentinel did not accept.
    /// </param>
    /// <param name="filterEpoch">The acknowledged Filter epoch, for example <c>filterStore.Epoch</c>. Null is sent as 0.</param>
    /// <param name="timeProvider">The clock and the timer source. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="interval">Zero (no timer, ReportAsync only) or 1 s to 59.999 s. Defaults to 15 s.</param>
    /// <param name="onError">
    /// Receives every signal Sentinel did not accept, a suppressed report
    /// (SequenceException FilterEpochRegressed) and any failure of a timed report.
    /// </param>
    public CompletenessHeartbeat(
        SequenceAllocator sequence,
        Func<ProducerCompletenessSignal, CancellationToken, Task> send,
        Func<ulong?>? filterEpoch = null,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null,
        Action<Exception>? onError = null)
    {
        _sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _filterEpoch = filterEpoch ?? (() => null);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
        if (_interval != TimeSpan.Zero && (_interval < MinInterval || _interval > MaxInterval))
        {
            throw new ArgumentOutOfRangeException(nameof(interval), _interval, "interval must be zero or 1 s to 59.999 s");
        }
        _onError = onError ?? (_ => { });
        _activitySince = NowUnixNanoseconds();
    }

    /// <summary>Reports every interval on a PeriodicTimer over the TimeProvider. A zero interval arms nothing.</summary>
    public void Start()
    {
        if (_interval == TimeSpan.Zero)
        {
            return;
        }
        lock (_scheduleLock)
        {
            if (_stopped || _timer != null)
            {
                return;
            }
            _timer = new PeriodicTimer(_interval, _timeProvider);
            _loopCancellation = new CancellationTokenSource();
            _ = TickAsync(_timer, _loopCancellation.Token);
        }
    }

    /// <summary>
    /// Sends the current declaration and returns one report per signal sent. A call made while
    /// another report runs waits for, and returns, the one follow-up report built after it.
    /// </summary>
    public async Task<IReadOnlyList<CompletenessReport>> ReportAsync(CancellationToken cancellationToken = default)
    {
        var requestedAfter = Interlocked.Read(ref _runsStarted);
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var last = _lastRun;
            if (last != null && last.Id > requestedAfter)
            {
                return last.Reports;
            }
            var id = Interlocked.Increment(ref _runsStarted);
            var reports = await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            _lastRun = new Run(id, reports);
            return reports;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Declares the source INTENTIONALLY_IDLE. The next allocation ends it.</summary>
    public void DeclareIdle()
    {
        lock (_stateLock)
        {
            if (_activity == SourceActivityState.IntentionallyIdle)
            {
                return;
            }
            _activity = SourceActivityState.IntentionallyIdle;
            _activitySince = Math.Max(NowUnixNanoseconds(), _lastReportedAt);
            var declaration = _sequence.Declaration();
            _idleMark = (declaration.Epoch, declaration.LastEmitted);
        }
    }

    /// <summary>Declares the source ACTIVE again.</summary>
    public void DeclareActive()
    {
        lock (_stateLock)
        {
            if (_activity == SourceActivityState.Active)
            {
                return;
            }
            _activity = SourceActivityState.Active;
            _activitySince = Math.Max(NowUnixNanoseconds(), _lastReportedAt);
        }
    }

    /// <summary>Cancels the timer and silences the heartbeat. Sends nothing, for a process that must go quiet.</summary>
    public void Stop()
    {
        _stopped = true;
        CancelTimer();
    }

    /// <summary>
    /// Cancels the timer, waits for a running report, sends one final report and silences the
    /// heartbeat. The final signal is a close only if nothing is unresolved.
    /// </summary>
    public async Task<IReadOnlyList<CompletenessReport>> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        CancelTimer();
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reports = await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            _stopped = true;
            return reports;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Stops the heartbeat. Equivalent to <see cref="Stop"/>.</summary>
    public void Dispose() => Stop();

    private async Task<IReadOnlyList<CompletenessReport>> RunOnceAsync(CancellationToken cancellationToken)
    {
        var reports = new List<CompletenessReport>();
        if (_stopped)
        {
            return reports;
        }
        var declaration = _sequence.Declare();
        var ack = _filterEpoch() ?? 0;
        var moment = AdvanceClock(declaration.Epoch, declaration.LastEmitted);
        var previous = declaration.Previous;
        if (previous != null
            && _lastAccepted?.Declaration.Epoch != declaration.Epoch
            && _lastAccepted != new Accepted(previous, ack)
            && ack >= FloorOf(previous.Epoch))
        {
            await SendOneAsync(previous, ack, moment, reports, cancellationToken).ConfigureAwait(false);
        }
        var floor = FloorOf(declaration.Epoch);
        if (ack < floor)
        {
            // Never clamp upward: that would acknowledge a Filter the Probe is not applying.
            _onError(new SequenceException(
                SequenceException.FilterEpochRegressed,
                $"acknowledged Filter epoch {ack} is below {floor} already sent for epoch {declaration.Epoch}"));
            return reports;
        }
        var current = new EpochDeclaration(declaration.Epoch, declaration.LastEmitted, declaration.CompleteThrough);
        await SendOneAsync(current, ack, moment, reports, cancellationToken).ConfigureAwait(false);
        var oldest = previous?.Epoch ?? declaration.Epoch;
        foreach (var epoch in new List<ulong>(_ackFloor.Keys))
        {
            if (epoch < oldest)
            {
                _ackFloor.Remove(epoch);
            }
        }
        return reports;
    }

    // The report instant never goes before an earlier report or the activity change; an
    // allocation since an idle declaration ends it.
    private Moment AdvanceClock(ulong epoch, ulong lastEmitted)
    {
        lock (_stateLock)
        {
            var now = Math.Max(Math.Max(NowUnixNanoseconds(), _lastReportedAt), _activitySince);
            _lastReportedAt = now;
            if (_activity == SourceActivityState.IntentionallyIdle && _idleMark != (epoch, lastEmitted))
            {
                _activity = SourceActivityState.Active;
                _activitySince = now;
            }
            return new Moment(now, _activity, _activitySince);
        }
    }

    private async Task SendOneAsync(
        EpochDeclaration declaration,
        ulong ack,
        Moment moment,
        List<CompletenessReport> reports,
        CancellationToken cancellationToken)
    {
        if (_stopped)
        {
            return;
        }
        var signal = CompletenessSignals.Build(declaration, ack, moment.Activity, moment.ReportedAt, moment.ActivitySince);
        _ackFloor[declaration.Epoch] = Math.Max(FloorOf(declaration.Epoch), ack);
        bool accepted;
        try
        {
            await _send(signal, cancellationToken).ConfigureAwait(false);
            accepted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // Any refusal from the send function (HTTP, transport, a 409) is a signal Sentinel did
            // not accept. It changes nothing but the report.
            _onError(error);
            accepted = false;
        }
        if (accepted)
        {
            _lastAccepted = new Accepted(declaration, ack);
        }
        reports.Add(new CompletenessReport(declaration.Epoch, declaration.LastEmitted, declaration.CompleteThrough, ack, accepted));
    }

    private ulong FloorOf(ulong epoch) => _ackFloor.TryGetValue(epoch, out var floor) ? floor : 0;

    private async Task TickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    // CancellationToken.None: a shutdown waits for a running report rather than
                    // abandoning its send.
                    await ReportAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    _onError(error);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop or shutdown cancelled the wait.
        }
    }

    private void CancelTimer()
    {
        lock (_scheduleLock)
        {
            _loopCancellation?.Cancel();
            _loopCancellation?.Dispose();
            _loopCancellation = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private long NowUnixNanoseconds() =>
        checked((_timeProvider.GetUtcNow().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * NanosecondsPerTick);
}
