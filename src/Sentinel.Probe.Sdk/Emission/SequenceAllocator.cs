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
// A declared close (complete_through == last_emitted, returned by Declare()) seals the epoch:
// Sentinel refuses any later slot in it. The next allocation therefore opens a new epoch,
// max(epoch + 1, clock in Unix milliseconds). The roll is lazy, at allocation and never at the
// heartbeat, so a quiet source keeps its epoch.
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

/// <summary>The final declaration of one Sequence Epoch.</summary>
public sealed record EpochDeclaration(ulong Epoch, ulong LastEmitted, ulong CompleteThrough);

/// <summary>
/// What the heartbeat reports: the current epoch's counters, its filtered ranges (sorted, merged
/// within one Filter epoch), and the final declaration of the epoch closed before it, if any.
/// </summary>
public sealed record SequenceDeclaration(
    ulong Epoch,
    ulong LastEmitted,
    ulong CompleteThrough,
    IReadOnlyList<FilteredRange> Filtered,
    EpochDeclaration? Previous);

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
    }

    /// <summary>Returns the next slot. After a declared close it first opens a new epoch.</summary>
    public Slot Allocate()
    {
        lock (_lock)
        {
            if (_sealed)
            {
                var opened = Math.Max(Successor(_epoch), ClockMilliseconds());
                _previous = new EpochDeclaration(_epoch, _last, _completeThrough);
                _epoch = opened;
                _last = 0;
                _completeThrough = 0;
                _states.Clear();
                _filtered = new List<FilteredRange>();
                _sealed = false;
            }
            _last = Successor(_last);
            _states[_last] = SlotState.InFlight;
            return new Slot(_epoch, _last);
        }
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
            if (_completeThrough == _last)
            {
                _sealed = true;
            }
            return Snapshot();
        }
    }

    private SequenceDeclaration Snapshot() => new(_epoch, _last, _completeThrough, _filtered, _previous);

    // False for a slot of another epoch, a number never allocated, or a number at or below
    // complete_through (EXPORTED, already discarded).
    private bool TryState(Slot slot, out SlotState state)
    {
        state = default;
        return slot.Epoch == _epoch && slot.Number >= 1 && slot.Number <= _last && _states.TryGetValue(slot.Number, out state);
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
