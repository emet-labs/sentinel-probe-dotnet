using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using OpenTelemetry;
using Sentinel.Probe.Sdk.Client;
using Sentinel.Probe.Sdk.Filter;
using Sentinel.Probe.Sdk.Otlp;

namespace Sentinel.Probe.Sdk.Emission;

// OtlpExportingSpanProcessor: the SDK's exporting processor. Attach it after
// SequenceStampingProcessor on the host's TracerProvider.
//
// Contract:
//   * It NEVER throws out of OnEnd. Any conversion, validation, encode or transport error is caught,
//     the Event's slot is marked FAILED (complete_through cannot pass it), the error goes to OnError
//     and to a rate-limited Log, and nothing propagates into host code.
//   * A bounded channel feeds one worker that exports batches. OVERFLOW POLICY: when the queue is
//     full the NEWEST Event is dropped (never a queued one), its slot is marked FAILED and counted in
//     DroppedCount. A dropped Event is therefore visible as missing evidence, never as a pass.
//   * RETRY POLICY (the same in every Probe SDK, sdk/README.md): transport errors (no response,
//     timeout) and HTTP 408, 425, 429, 500, 502, 503 and 504 are retried with exponential backoff and
//     jitter up to MaxAttempts, then FAILED. A Retry-After on a 429 or 503 raises the wait, never past
//     RetryMaxDelay. A deterministic refusal (validation, any other status, rejected spans) is FAILED
//     at once: resending the same Events cannot change it. A slot is marked EXPORTED only after a 2xx
//     with zero rejected spans, so complete_through never passes a failed or in-flight export.
//   * DUPLICATE EVENT ID POLICY: when Sentinel refuses a second delivery of an Event id it holds
//     (evidence_conflict / identity_conflict), OnError receives ExportFailureKind.DuplicateEventId
//     (reason "duplicate-event-id"), the slot stays FAILED, complete_through never passes it (missing
//     evidence never looks complete) and nothing is retried. Any other rejection (slot_conflict) also
//     stays FAILED. A rejected batch of several Events is resent one by one so a refusal fails only
//     the Event it names.
//   * ShutdownAsync stops intake, waits until every queued Event is exported or FAILED, and only then
//     sends the final completeness declaration.

/// <summary>Why a batch of Events was not exported.</summary>
public enum ExportFailureKind
{
    /// <summary>The span could not be converted to an Event (for example a local parent without an Event id).</summary>
    Convert,

    /// <summary>The span failed validation or encoding.</summary>
    Encode,

    /// <summary>The bounded queue was full or closed; the Event was dropped.</summary>
    QueueOverflow,

    /// <summary>The transport kept failing for MaxAttempts attempts.</summary>
    Transport,

    /// <summary>Sentinel answered with a non-retryable HTTP status.</summary>
    Refused,

    /// <summary>Sentinel's 2xx response rejected one or more spans.</summary>
    Rejected,

    /// <summary>
    /// Sentinel already holds an Event under this id and refused the redelivery (<c>evidence_conflict</c> or
    /// <c>identity_conflict</c>). The slot stays FAILED, <c>complete_through</c> does not pass it and the
    /// refusal is not retried. The wire reason is <c>duplicate-event-id</c>.
    /// </summary>
    DuplicateEventId,

    /// <summary>Any other unexpected error.</summary>
    Unexpected,
}

/// <summary>What <see cref="OtlpExportingSpanProcessorOptions.OnError"/> receives.</summary>
/// <param name="Kind">The failure class.</param>
/// <param name="Error">The underlying error.</param>
/// <param name="Slots">The slots marked failed.</param>
public sealed record ExportFailure(ExportFailureKind Kind, Exception Error, IReadOnlyList<Slot> Slots);

/// <summary>Configuration of <see cref="OtlpExportingSpanProcessor"/>.</summary>
public sealed class OtlpExportingSpanProcessorOptions
{
    /// <summary>The source handle sent as <c>x-sentinel-source-handle</c>.</summary>
    public required string SourceHandle { get; init; }

    /// <summary>The allocator that owns the slots this processor settles.</summary>
    public required SequenceAllocator Sequence { get; init; }

    /// <summary>The OTLP exporter (build it with resource attributes for service.name and provenance).</summary>
    public required OtlpExporter Exporter { get; init; }

    /// <summary>The <c>sentinel.schema.version</c> of each Event.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>The held Event Filter; an Event it drops is marked filtered. Null exports everything.</summary>
    public FilterStore? Filters { get; init; }

    /// <summary>Claimed sensitivity of each Event. Default Internal.</summary>
    public Sensitivity ClaimedSensitivity { get; init; } = Sensitivity.Internal;

    /// <summary>Claimed capabilities of each Event.</summary>
    public IReadOnlyList<SourceCapability> ClaimedCapabilities { get; init; } = Array.Empty<SourceCapability>();

    /// <summary>
    /// The Event id of a slot when the activity does not already carry <c>sentinel.event.id</c>.
    /// Default <c>{handle}:{epoch}:{number}</c>.
    /// </summary>
    public Func<Slot, string>? EventId { get; init; }

    /// <summary>
    /// Export a span whose remote parent carries no <c>sentinel.parent.event.id</c> as a causal root
    /// (no predecessor, no parent span id on the wire). Default false: such a span is refused and its
    /// slot FAILED. A local parent without the id is always refused.
    /// </summary>
    public bool AllowUnattributedRemoteParent { get; init; }

    /// <summary>Queue capacity. Default 4096.</summary>
    public int QueueCapacity { get; init; } = 4096;

    /// <summary>Most Events per request. Default 32.</summary>
    public int MaxBatch { get; init; } = 32;

    /// <summary>
    /// Attempts per batch, first try included. Retried on a transport error and on HTTP 408, 425,
    /// 429, 500, 502, 503 and 504; any other status is final. Default 5.
    /// </summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// Backoff before the second attempt; it doubles each attempt, with jitter (50-100% of the
    /// step). Default 100 ms.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Longest wait before a retry, a server <c>Retry-After</c> included. Default 5 s.</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Source of jitter in [0, 1); tests replace it. Default <see cref="Random.Shared"/>.</summary>
    public Func<double>? Jitter { get; init; }

    /// <summary>Called for every failure. An exception it throws is swallowed.</summary>
    public Action<ExportFailure>? OnError { get; init; }

    /// <summary>Receives a message for failures, at most once per <see cref="LogInterval"/> (suppressed ones are counted in the next message).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Minimum time between <see cref="Log"/> messages. Default 10 s.</summary>
    public TimeSpan LogInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Clock for <see cref="LogInterval"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Waits between attempts; tests replace it. Default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}

/// <summary>Exports each ended Event activity to Sentinel through a bounded asynchronous queue.</summary>
public sealed class OtlpExportingSpanProcessor : BaseProcessor<Activity>
{
    private readonly OtlpExportingSpanProcessorOptions _options;
    private readonly string _handle;
    private readonly SequenceAllocator _sequence;
    private readonly FilterStore? _filters;
    private readonly OtlpExporter _exporter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;
    private readonly Channel<Item> _queue;
    private readonly Task _worker;
    private readonly object _logLock = new();
    private DateTimeOffset _lastLog = DateTimeOffset.MinValue;
    private long _suppressed;
    private long _pending;
    private long _exported;
    private long _failed;
    private long _dropped;

    private readonly record struct Item(Activity Activity, Slot Slot, string EventId);

    public OtlpExportingSpanProcessor(OtlpExportingSpanProcessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Sequence);
        ArgumentNullException.ThrowIfNull(options.Exporter);
        if (string.IsNullOrWhiteSpace(options.SourceHandle))
        {
            throw new ArgumentException("Source handle is required.", nameof(options));
        }
        if (options.QueueCapacity < 1 || options.MaxBatch < 1 || options.MaxAttempts < 1)
        {
            throw new ArgumentException("QueueCapacity, MaxBatch and MaxAttempts must be at least 1.", nameof(options));
        }
        _options = options;
        _handle = options.SourceHandle;
        _sequence = options.Sequence;
        _filters = options.Filters;
        _exporter = options.Exporter;
        _time = options.TimeProvider ?? TimeProvider.System;
        _delay = options.Delay ?? ((d, ct) => Task.Delay(d, ct));
        _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait, // TryWrite fails when full; OnEnd then drops the newest.
        });
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Events whose export Sentinel accepted.</summary>
    public long ExportedCount => Interlocked.Read(ref _exported);

    /// <summary>Events whose slot was marked FAILED (including dropped ones).</summary>
    public long FailedCount => Interlocked.Read(ref _failed);

    /// <summary>Events dropped because the queue was full or closed (a subset of <see cref="FailedCount"/>).</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Never throws.</summary>
    public override void OnEnd(Activity data)
    {
        Slot slot = default;
        var haveSlot = false;
        try
        {
            if (data == null || !SequenceStampingProcessor.TryGetSlot(data, out slot))
            {
                // Not an Event of this source (or allocation overflowed): nothing to settle.
                return;
            }
            haveSlot = true;
            var eventId = EventIdFor(data, slot);
            if (_filters?.Get() is { } filter)
            {
                if (ApplyFilter.Apply(ToEvent(data, slot, eventId), filter) is null)
                {
                    _sequence.MarkFiltered(slot, _filters.Epoch() ?? 0);
                    return;
                }
            }
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(new Item(data, slot, eventId)))
            {
                Interlocked.Decrement(ref _pending);
                Interlocked.Increment(ref _dropped);
                Fail(new[] { slot }, ExportFailureKind.QueueOverflow, new InvalidOperationException(
                    "export queue is full or closed; the Event was dropped"));
            }
        }
        catch (Exception e) when (!IsFatal(e))
        {
            if (haveSlot)
            {
                Fail(new[] { slot }, ExportFailureKind.Convert, e);
            }
            else
            {
                Report(ExportFailureKind.Unexpected, e, Array.Empty<Slot>());
            }
        }
    }

    /// <summary>
    /// Stops intake and waits until every queued Event is exported or marked FAILED. When a
    /// <paramref name="heartbeat"/> is given its final completeness declaration is sent only after
    /// that, so the declaration sees the settled slots.
    /// </summary>
    public async Task ShutdownAsync(CompletenessHeartbeat? heartbeat = null, CancellationToken cancellationToken = default)
    {
        _queue.Writer.TryComplete();
        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (heartbeat != null)
        {
            try
            {
                await heartbeat.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (!IsFatal(e) && e is not OperationCanceledException)
            {
                Report(ExportFailureKind.Transport, e, Array.Empty<Slot>());
            }
        }
    }

    /// <inheritdoc />
    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        _queue.Writer.TryComplete();
        return _worker.Wait(timeoutMilliseconds < 0 ? Timeout.Infinite : timeoutMilliseconds);
    }

    /// <inheritdoc />
    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        var deadline = timeoutMilliseconds < 0 ? long.MaxValue : Environment.TickCount64 + timeoutMilliseconds;
        while (Interlocked.Read(ref _pending) > 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }
            Thread.Sleep(5);
        }
        return true;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Stop intake; ShutdownAsync (or OnShutdown) waits for the queue to settle.
            _queue.Writer.TryComplete();
        }
        base.Dispose(disposing);
    }

    private string EventIdFor(Activity activity, Slot slot)
    {
        if (activity.GetTagItem(OtlpTraces.EventIdKey) is string tagged && tagged.Length > 0)
        {
            return tagged;
        }
        return _options.EventId?.Invoke(slot)
            ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_handle}:{slot.Epoch}:{slot.Number}");
    }

    private ProducerEvent ToEvent(Activity activity, Slot slot, string eventId) =>
        SpanToEventConverter.SpanToEvent(new SpanConversion
        {
            Activity = activity,
            EventId = eventId,
            Sequence = new SequenceCoordinate { Epoch = slot.Epoch, Sequence = slot.Number },
            SchemaVersion = _options.SchemaVersion,
            AcknowledgedEpoch = _filters?.Epoch() ?? 0,
            ClaimedCapabilities = new List<SourceCapability>(_options.ClaimedCapabilities),
            ClaimedSensitivity = _options.ClaimedSensitivity,
            AllowUnattributedRemoteParent = _options.AllowUnattributedRemoteParent,
        });

    private SpanExport ToExport(Item item) => new()
    {
        Activity = item.Activity,
        EventId = item.EventId,
        SequenceEpoch = item.Slot.Epoch,
        SequenceNumber = item.Slot.Number,
        SchemaVersion = _options.SchemaVersion,
        AcknowledgedFilterEpoch = _filters?.Epoch() ?? 0,
        ClaimedSensitivity = _options.ClaimedSensitivity,
        ClaimedCapabilities = _options.ClaimedCapabilities,
        AllowUnattributedRemoteParent = _options.AllowUnattributedRemoteParent,
    };

    private async Task RunAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new List<Item>(_options.MaxBatch);
                while (batch.Count < _options.MaxBatch && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }
                if (batch.Count == 0)
                {
                    continue;
                }
                try
                {
                    await ExportBatchAsync(batch).ConfigureAwait(false);
                }
                catch (Exception e) when (!IsFatal(e))
                {
                    Fail(Slots(batch), ExportFailureKind.Unexpected, e);
                }
                finally
                {
                    Interlocked.Add(ref _pending, -batch.Count);
                }
            }
        }
        catch (Exception e) when (!IsFatal(e))
        {
            // The loop itself failed; settle whatever is still queued as FAILED so nothing looks complete.
            while (reader.TryRead(out var item))
            {
                Interlocked.Decrement(ref _pending);
                Fail(new[] { item.Slot }, ExportFailureKind.Unexpected, e);
            }
        }
    }

    private async Task ExportBatchAsync(List<Item> batch)
    {
        var good = new List<Item>(batch.Count);
        var spans = new List<SpanExport>(batch.Count);
        foreach (var item in batch)
        {
            try
            {
                var span = ToExport(item);
                _ = OtlpTraces.Encode(new[] { span }); // validate this Event alone so one bad span does not sink the batch
                good.Add(item);
                spans.Add(span);
            }
            catch (Exception e) when (!IsFatal(e))
            {
                Fail(new[] { item.Slot }, ExportFailureKind.Encode, e);
            }
        }
        if (good.Count == 0)
        {
            return;
        }

        Exception? last = null;
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
                var outcome = await _exporter.ExportSpansOutcomeAsync(_handle, spans, CancellationToken.None).ConfigureAwait(false);
                var rejected = outcome.RejectedSpans;
                if (rejected == 0)
                {
                    foreach (var item in good)
                    {
                        _sequence.MarkExported(item.Slot);
                    }
                    Interlocked.Add(ref _exported, good.Count);
                    return;
                }
                if (good.Count > 1)
                {
                    // One refused Event must not fail its neighbours: resend one by one.
                    foreach (var item in good)
                    {
                        await ExportBatchAsync(new List<Item> { item }).ConfigureAwait(false);
                    }
                    return;
                }
                if (outcome.IsDuplicateEventId)
                {
                    Fail(Slots(good), ExportFailureKind.DuplicateEventId, new OtlpExportException(
                        "Sentinel already holds an Event under this id and refused the redelivery ("
                        + outcome.ErrorMessage + "); the slot stays FAILED (duplicate-event-id)"));
                    return;
                }
                Fail(Slots(good), ExportFailureKind.Rejected, new OtlpExportException(
                    "Sentinel rejected " + rejected.ToString(System.Globalization.CultureInfo.InvariantCulture) + " span(s)"));
                return;
            }
            catch (OtlpExportException e) when (!e.IsTransient)
            {
                Fail(Slots(good), e.StatusCode != null ? ExportFailureKind.Refused : ExportFailureKind.Encode, e);
                return;
            }
            catch (Exception e) when (e is OtlpExportException or OperationCanceledException or System.Net.Http.HttpRequestException)
            {
                // A cancellation here is an HTTP timeout: this call passes no token of its own.
                last = e;
                if (attempt < _options.MaxAttempts)
                {
                    await _delay(Backoff(attempt, (e as OtlpExportException)?.RetryAfter), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        Fail(Slots(good), ExportFailureKind.Transport, last ?? new OtlpExportException("export failed"));
    }

    private TimeSpan Backoff(int attempt, TimeSpan? retryAfter) =>
        RetryDelay(attempt, _options.RetryBaseDelay, _options.RetryMaxDelay, retryAfter, (_options.Jitter ?? Random.Shared.NextDouble)());

    /// <summary>
    /// The wait after failed attempt <paramref name="attempt"/> (1-based): exponential with jitter
    /// (50-100% of the step; <paramref name="jitter"/> is in [0, 1)), raised to the server's
    /// Retry-After when that asks for longer, and never longer than <paramref name="cap"/>.
    /// </summary>
    internal static TimeSpan RetryDelay(int attempt, TimeSpan baseDelay, TimeSpan cap, TimeSpan? retryAfter, double jitter)
    {
        var step = Math.Min(baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1), cap.TotalMilliseconds);
        var jittered = step * (0.5 + (jitter / 2));
        var wait = Math.Max(jittered, retryAfter?.TotalMilliseconds ?? 0);
        return TimeSpan.FromMilliseconds(Math.Min(wait, cap.TotalMilliseconds));
    }

    private static Slot[] Slots(List<Item> items)
    {
        var slots = new Slot[items.Count];
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = items[i].Slot;
        }
        return slots;
    }

    private void Fail(IReadOnlyList<Slot> slots, ExportFailureKind kind, Exception error)
    {
        foreach (var slot in slots)
        {
            try
            {
                _sequence.MarkFailed(slot);
            }
            catch (Exception e) when (!IsFatal(e))
            {
                // MarkFailed only takes a lock; nothing to do if it somehow fails.
            }
        }
        Interlocked.Add(ref _failed, slots.Count);
        Report(kind, error, slots);
    }

    private void Report(ExportFailureKind kind, Exception error, IReadOnlyList<Slot> slots)
    {
        try
        {
            _options.OnError?.Invoke(new ExportFailure(kind, error, slots));
        }
        catch (Exception e) when (!IsFatal(e))
        {
            // A faulty callback must not reach the host or stop the worker.
        }
        LogRateLimited(kind, error, slots.Count);
    }

    private void LogRateLimited(ExportFailureKind kind, Exception error, int events)
    {
        if (_options.Log == null)
        {
            return;
        }
        string message;
        lock (_logLock)
        {
            var now = _time.GetUtcNow();
            if (now - _lastLog < _options.LogInterval)
            {
                _suppressed++;
                return;
            }
            _lastLog = now;
            var suppressed = _suppressed;
            _suppressed = 0;
            message = $"sentinel probe {_handle}: export of {events} Event(s) failed ({kind}); completeness stays open: {error.Message}"
                + (suppressed > 0 ? $" ({suppressed} similar failure(s) suppressed)" : "");
        }
        try
        {
            _options.Log(message);
        }
        catch (Exception e) when (!IsFatal(e))
        {
            // The log sink is the host's; its failure is not ours to propagate.
        }
    }

    private static bool IsFatal(Exception e) => e is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
