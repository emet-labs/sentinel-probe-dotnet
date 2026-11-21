using System;
using System.Collections.Generic;

namespace Sentinel.Probe.Sdk.Emission;

// SequenceAllocator numbers a source's Events and tracks which numbers Sentinel holds (#461).
//
// One allocator serves one source. Allocate() hands out (Sequence Epoch, number) slots, and the
// host marks each slot exported, failed or filtered. The completeness heartbeat reads the
// declaration: last_emitted is the highest number allocated, and complete_through is the
// contiguous prefix whose export succeeded. complete_through never passes a slot that is in
// flight, failed or filtered, so missing evidence is never declared complete.
//
// A declared close (complete_through == last_emitted and, since the C2 amendment of
// 2026-11-15, attempted == last_emitted; returned by Declare()) seals the epoch:
// Sentinel refuses any later slot in it. The next hook entry therefore opens a new epoch,
// max(epoch + 1, clock in Unix milliseconds). The roll is lazy, at NoteAttempt() or Allocate()
// and never at the heartbeat, so a quiet source keeps its epoch.
//
// Epoch chain (ADR-0024 amendment, formal/wire-spec.md section 5):
//   - C1 NoteAttempt() is the first call on every hook entry. It rolls a sealed epoch exactly as
//     Allocate() would, then counts the attempt, so a hook entry that fails before Allocate() is
//     still visible as attempted > last_emitted. Allocate() never counts.
//   - C2 the first declaration with complete_through == last_emitted records sealed_at from the
//     producer clock inside the same critical section, and nothing moves it afterwards.
//   - C3 a roll sets the new epoch's predecessor_epoch and keeps the closing declaration for
//     re-send; the re-send is never edited.
//   - C4 attempted is not persisted. The host-persisted previousEpoch, when given, is the first
//     epoch's predecessor_epoch; without it the field is absent (unknown, not "first").
//   - C6 one allocator per source identity at a time; the deployment must prevent two.
//
// Epochs and numbers are at most 2^63 - 1 (they travel as OTLP int64). Allocation past the
// maximum is a SequenceException, never a wrap. One lock guards all state.

/// <summary>One allocated sequence position: the Sequence Epoch and the number within it.</summary>
public readonly record struct Slot(ulong Epoch, ulong Number);

/// <summary>
/// A run of slots the Event Filter dropped under one Filter epoch. It is not on the wire until
/// #542; the declaration carries it so that change stays small.
/// </summary>
public sealed record FilteredRange(ulong First, ulong Last, ulong FilterEpoch);

/// <summary>
/// The declaration of one Sequence Epoch: its counters and the epoch-chain fields of the wire
/// spec. A null <c>Attempted</c> is a legacy declaration, which the heartbeat sends with none of
/// the four chain fields: absence is unknown, never zero.
/// </summary>
/// <param name="Epoch">The Sequence Epoch.</param>
/// <param name="LastEmitted">The highest number allocated.</param>
/// <param name="CompleteThrough">The contiguous prefix whose export succeeded.</param>
/// <param name="Attempted">Hook entries of the epoch, counted before slot allocation (&gt;= LastEmitted).</param>
/// <param name="SealedAtUnixNanoseconds">The producer instant of the first closing declaration; never moves.</param>
/// <param name="PredecessorEpoch">The epoch this one followed, when the Probe knows it.</param>
public sealed record EpochDeclaration(
    ulong Epoch,
    ulong LastEmitted,
    ulong CompleteThrough,
    ulong? Attempted = null,
    long? SealedAtUnixNanoseconds = null,
    ulong? PredecessorEpoch = null);

/// <summary>
/// What the heartbeat reports: the current epoch's counters, its filtered ranges (sorted, merged
/// within one Filter epoch), and the final declaration of the epoch closed before it, if any.
/// </summary>
public sealed record SequenceDeclaration(
    ulong Epoch,
    ulong LastEmitted,
    ulong CompleteThrough,
    IReadOnlyList<FilteredRange> Filtered,
    EpochDeclaration? Previous,
    ulong? Attempted = null,
    long? SealedAtUnixNanoseconds = null,
    ulong? PredecessorEpoch = null)
{
    /// <summary>The current epoch's declaration, chain fields included.</summary>
    public EpochDeclaration CurrentEpoch() => new(Epoch, LastEmitted, CompleteThrough, Attempted, SealedAtUnixNanoseconds, PredecessorEpoch);
}

/// <summary>A refusal with a stable code shared with every Probe SDK and the sequence_completeness vectors.</summary>
public sealed class SequenceException : Exception
{
    /// <summary>An explicit epoch that is not strictly greater than the previous run's epoch.</summary>
    public const string EpochNotGreater = "epoch-not-greater";

    /// <summary>An epoch or a number past 2^63 - 1.</summary>
    public const string SequenceOverflow = "sequence-overflow";

    /// <summary>The acknowledged Filter epoch went below one already sent for the epoch.</summary>
    public const string FilterEpochRegressed = "filter-epoch-regressed";

    public SequenceException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>The stable error code.</summary>
    public string Code { get; }
}

/// <summary>Allocates Sequence slots for one source and declares which of them Sentinel holds.</summary>
public sealed class SequenceAllocator
{
    /// <summary>The largest epoch or number: 2^63 - 1.</summary>
    public const ulong MaxValue = long.MaxValue;

    private enum SlotState
    {
        InFlight,
        Exported,
        Failed,
        Filtered,
    }

    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<ulong, SlotState> _states = new();
    private ulong _epoch;
    private ulong _last;
    private ulong _completeThrough;
    private bool _sealed;
    private ulong _attempted;
    private long? _sealedAt;
    private ulong? _predecessor;
    private long _lastClock = long.MinValue;
    private List<FilteredRange> _filtered = new();
    private EpochDeclaration? _previous;

    /// <param name="timeProvider">The clock; the epoch is its Unix milliseconds. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="epoch">Overrides the clock-derived first epoch.</param>
    /// <param name="previousEpoch">
    /// The last epoch of the previous run, when the host persisted it. Without it a backward clock
    /// step across a restart goes undetected, and Sentinel refuses the regressed epoch.
    /// </param>
    public SequenceAllocator(TimeProvider? timeProvider = null, ulong? epoch = null, ulong? previousEpoch = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (epoch > MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(epoch), "epoch must be at most 2^63 - 1");
        }
        if (previousEpoch > MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(previousEpoch), "previous epoch must be at most 2^63 - 1");
        }
        if (epoch.HasValue && previousEpoch.HasValue && epoch.Value <= previousEpoch.Value)
        {
            throw new SequenceException(
                SequenceException.EpochNotGreater,
                $"epoch {epoch.Value} is not greater than the previous epoch {previousEpoch.Value}");
        }
        _epoch = epoch ?? (previousEpoch.HasValue ? Math.Max(ClockMilliseconds(), Successor(previousEpoch.Value)) : ClockMilliseconds());
        _predecessor = previousEpoch;
    }

    /// <summary>
    /// Counts one hook entry (C1). Call it first on every hook entry, before any filtering,
    /// serialization or <see cref="Allocate"/>. After a declared close it opens the next epoch,
    /// exactly as Allocate would. It throws SequenceException only on overflow, like Allocate.
    /// </summary>
    public void NoteAttempt()
    {
        lock (_lock)
        {
            RollIfSealed();
            _attempted = Successor(_attempted);
        }
    }

    /// <summary>Returns the next slot. After a declared close it first opens a new epoch. It never counts an attempt.</summary>
    public Slot Allocate()
    {
        lock (_lock)
        {
            RollIfSealed();
            _last = Successor(_last);
            // A host that skipped NoteAttempt still cannot declare attempted < last_emitted.
            if (_attempted < _last)
            {
                _attempted = _last;
            }
            _states[_last] = SlotState.InFlight;
            return new Slot(_epoch, _last);
        }
    }

    // Opens the next epoch after a declared close (C3). The caller holds the lock.
    private void RollIfSealed()
    {
        if (!_sealed)
        {
            return;
        }
        var opened = Math.Max(Successor(_epoch), ClockMilliseconds());
        _previous = new EpochDeclaration(_epoch, _last, _completeThrough, _attempted, _sealedAt, _predecessor);
        _predecessor = _epoch;
        _epoch = opened;
        _last = 0;
        _completeThrough = 0;
        _attempted = 0;
        _sealedAt = null;
        _states.Clear();
        _filtered = new List<FilteredRange>();
        _sealed = false;
    }

    /// <summary>
    /// Records that Sentinel accepted the slot's Event (HTTP 2xx, zero rejected spans). A failed
    /// slot the host re-exported may be marked exported.
    /// </summary>
    public void MarkExported(Slot slot)
    {
        lock (_lock)
        {
            if (!TryState(slot, out var state) || (state != SlotState.InFlight && state != SlotState.Failed))
            {
                return;
            }
            _states[slot.Number] = SlotState.Exported;
            while (_states.TryGetValue(_completeThrough + 1, out var next) && next == SlotState.Exported)
            {
                _states.Remove(_completeThrough + 1);
                _completeThrough++;
            }
        }
    }

    /// <summary>
    /// Records that the slot's export failed. The helper does not retry it; the epoch stays open
    /// below the slot until the host re-exports the identical Event.
    /// </summary>
    public void MarkFailed(Slot slot)
    {
        lock (_lock)
        {
            if (TryState(slot, out var state) && state == SlotState.InFlight)
            {
                _states[slot.Number] = SlotState.Failed;
            }
        }
    }

    /// <summary>
    /// Records that the Event Filter dropped the slot's Event under <paramref name="filterEpoch"/>.
    /// Until #542 a filtered slot does not advance complete_through.
    /// </summary>
    public void MarkFiltered(Slot slot, ulong filterEpoch)
    {
        lock (_lock)
        {
            if (!TryState(slot, out var state) || state != SlotState.InFlight)
            {
                return;
            }
            _states[slot.Number] = SlotState.Filtered;
            _filtered = Merge(_filtered, new FilteredRange(slot.Number, slot.Number, filterEpoch));
        }
    }

    /// <summary>The current Sequence Epoch.</summary>
    public ulong Epoch()
    {
        lock (_lock) return _epoch;
    }

    /// <summary>The highest number allocated in the current epoch, 0 before any.</summary>
    public ulong HighestAllocated()
    {
        lock (_lock) return _last;
    }

    /// <summary>Reads the current declaration without sealing anything.</summary>
    public SequenceDeclaration Declaration()
    {
        lock (_lock) return Snapshot();
    }

    /// <summary>
    /// Returns the declaration the heartbeat sends. A close (complete_through == last_emitted)
    /// seals the epoch, so no slot is ever allocated in it afterwards.
    /// </summary>
    public SequenceDeclaration Declare()
    {
        lock (_lock)
        {
            // C2 (amended): seal only when attempted == last_emitted, so a heartbeat between
            // NoteAttempt and Allocate of one hook entry leaves the epoch open.
            if (_completeThrough == _last && _attempted == _last)
            {
                if (!_sealed)
                {
                    _sealedAt = ProducerNow();
                }
                _sealed = true;
            }
            return Snapshot();
        }
    }

    private SequenceDeclaration Snapshot() => new(_epoch, _last, _completeThrough, _filtered, _previous, _attempted, _sealedAt, _predecessor);

    // False for a slot of another epoch, a number never allocated, or a number at or below
    // complete_through (EXPORTED, already discarded).
    private bool TryState(Slot slot, out SlotState state)
    {
        state = default;
        return slot.Epoch == _epoch && slot.Number >= 1 && slot.Number <= _last && _states.TryGetValue(slot.Number, out state);
    }

    // The producer clock for sealed_at (C2, C7), in Unix nanoseconds. It never steps backwards
    // within this allocator.
    private long ProducerNow()
    {
        var ticks = _timeProvider.GetUtcNow().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        _lastClock = Math.Max(_lastClock, checked(ticks * 100));
        return _lastClock;
    }

    private ulong ClockMilliseconds()
    {
        var milliseconds = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (milliseconds < 0)
        {
            throw new InvalidOperationException("clock is before the Unix epoch");
        }
        return (ulong)milliseconds;
    }

    private static ulong Successor(ulong value)
    {
        if (value >= MaxValue)
        {
            throw new SequenceException(SequenceException.SequenceOverflow, $"sequence value {value} has no successor");
        }
        return value + 1;
    }

    // Sorted by first slot; adjacent ranges merge only within one Filter epoch. A slot is filtered
    // at most once, so ranges never overlap. A fresh list each time: a returned declaration never
    // changes under its holder.
    private static List<FilteredRange> Merge(List<FilteredRange> ranges, FilteredRange added)
    {
        var sorted = new List<FilteredRange>(ranges) { added };
        sorted.Sort((a, b) => a.First.CompareTo(b.First));
        var merged = new List<FilteredRange>(sorted.Count);
        foreach (var range in sorted)
        {
            if (merged.Count > 0)
            {
                var tail = merged[^1];
                if (tail.FilterEpoch == range.FilterEpoch && tail.Last + 1 == range.First)
                {
                    merged[^1] = tail with { Last = range.Last };
                    continue;
                }
            }
            merged.Add(range);
        }
        return merged;
    }
}
