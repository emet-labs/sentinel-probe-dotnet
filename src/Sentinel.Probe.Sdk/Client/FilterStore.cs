using System.Threading;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Proto;

namespace Sentinel.Probe.Sdk.Client;

// FilterStore — the .NET analog of sdk/go/client/filterstore.go (mirrors filter-store.ts).
//
// Holds the current EventFilter for the Probe's source and tracks the acknowledged epoch.
// Reference-swap semantics: Set replaces the held reference atomically and Get returns that
// stable reference. The store never mutates a held EventFilter in place, so a caller that
// snapshots the filter once for an emit-and-enforce flow holds a consistent view even if a Set
// lands mid-flow.
//
// In the TypeScript reference that property was free (single-threaded). In Go it is a real
// concurrency requirement satisfied with atomic.Pointer and a compare-and-swap loop. In .NET the
// analog is a reference field read with Volatile.Read and replaced with Interlocked.CompareExchange,
// so two concurrent Sets can never let the older epoch win. The default value is a usable, empty
// store. All members are safe for concurrent use.
//
// Epoch order (ADR-0006, versioned Filter push). Epochs are unsigned 64-bit and only move forward.
// With held the held Filter's epoch and offered the offered Filter's epoch, either of which may be
// absent:
//
// - Nothing held: Set applies any Filter, including epoch 0 and an epoch-less one.
// - Held is absent: a Filter with an epoch (including 0) replaces it; an epoch-less one is a no-op.
// - Held is present: only a strictly greater epoch replaces it. An equal epoch is a no-op, the
//   ordinary at-least-once re-delivery. A lower or absent epoch is refused: a reordered or
//   replayed push never downgrades the Filter or the acknowledged epoch.
//
// A Set that returns false keeps the held reference and changes no later comparison. It raises no
// error and logs nothing.
//
// An equal epoch is never applied, even when its content differs. The server rewrites the current
// epoch in place when its Readiness flips Warming to Active, so whether a Probe should accept that
// flip at an equal epoch is an open question (#534); the store keeps its earlier behaviour until it
// is decided.
//
// After a server store reset, epochs restart from 1 and this store refuses them until the Probe
// restarts. Restarting the Probe is the recovery.
public sealed class FilterStore
{
    // Read with Volatile.Read on the emit hot path and replaced only by the CompareExchange loop in
    // Set, the analog of Go's atomic.Pointer<EventFilter>.
    private EventFilter? _current;

    /// <summary>
    /// Builds a store optionally seeded with an initial filter (for example one restored from a
    /// local cache before the first push). A null initial filter means no filter is held.
    /// </summary>
    public FilterStore(EventFilter? initial = null)
    {
        _current = initial;
    }

    /// <summary>
    /// Returns the held EventFilter, or null before the first Set. The returned reference is
    /// stable until the next Set: it is safe to hold it across an entire emit-and-enforce flow.
    /// Callers must not mutate the pointed-to filter.
    /// </summary>
    public EventFilter? Get() => Volatile.Read(ref _current);

    /// <summary>
    /// Returns the held filter's epoch, or null when no filter is held or the held filter declares
    /// no epoch. The result is a fresh value copy, so a caller cannot reach the held filter
    /// through it.
    /// <para>
    /// null means "absent". It does NOT mean zero: epoch 0 is a legitimate epoch and is returned
    /// as a non-null value of 0.
    /// </para>
    /// </summary>
    public ulong? Epoch()
    {
        return EpochOf(Get());
    }

    /// <summary>
    /// Swaps in a new filter under the epoch order in the type comment. Returns true when the
    /// reference was replaced (the first Set, or a strictly newer epoch), and false when the held
    /// reference was kept (an equal, older or absent epoch).
    /// </summary>
    public bool Set(EventFilter? filter)
    {
        ulong? offered = EpochOf(filter);
        EventFilter? held = Volatile.Read(ref _current);
        while (true)
        {
            if (held != null && !Replaces(EpochOf(held), offered))
            {
                return false; // equal, older, or epoch-less: keep the held reference
            }
            EventFilter? witnessed = Interlocked.CompareExchange(ref _current, filter, held);
            if (ReferenceEquals(witnessed, held))
            {
                return true;
            }
            held = witnessed; // another Set landed first: re-compare against it
        }
    }

    /// <summary>
    /// Reports whether a Filter at <paramref name="newEpoch"/> would be newer than the held one:
    /// no epoch is held, or <paramref name="newEpoch"/> is strictly greater. An absent
    /// <paramref name="newEpoch"/> never refreshes a held epoch.
    /// </summary>
    public bool ShouldRefresh(ulong? newEpoch)
    {
        ulong? held = Epoch();
        if (held == null)
        {
            return true; // no epoch held -> refresh
        }
        if (newEpoch == null)
        {
            return false; // no new epoch to compare
        }
        return newEpoch.GetValueOrDefault() > held.GetValueOrDefault();
    }

    /// <summary>
    /// Whether a Filter at <paramref name="offered"/> replaces one held at
    /// <paramref name="held"/>, per the epoch order in the type comment. Compares by value as
    /// unsigned 64-bit integers.
    /// </summary>
    internal static bool Replaces(ulong? held, ulong? offered)
    {
        if (!offered.HasValue) return false;
        if (!held.HasValue) return true;
        return offered.GetValueOrDefault() > held.GetValueOrDefault();
    }

    /// <summary>
    /// Nil-safe on the message itself, and presence-aware: the generated <c>Epoch</c> getter
    /// flattens an absent epoch to 0, and <c>filter?.Epoch</c> would widen that 0 into a non-null
    /// <c>ulong?</c> without a compiler diagnostic. Only <c>HasEpoch</c> tells the two apart.
    /// </summary>
    private static ulong? EpochOf(EventFilter? filter)
    {
        return filter?.EpochOrNull();
    }
}
