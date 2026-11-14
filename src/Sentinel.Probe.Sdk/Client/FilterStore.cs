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
// concurrency requirement satisfied with atomic.Pointer. In .NET the analog is a volatile field
// holding a reference: reference reads/writes are atomic on the CLR, and `volatile` ensures the
// store does not reorder a stale read over a Set on another thread. The default value is a usable,
// empty store. All members are safe for concurrent use.
public sealed class FilterStore
{
    // volatile: guarantees a Get observes the most recent Set across threads without locking on
    // the emit hot path, the adequate analog of Go's atomic.Pointer<EventFilter>.
    private volatile EventFilter? _current;

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
    public EventFilter? Get() => _current;

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
        return EpochOf(_current);
    }

    /// <summary>
    /// Swaps in a new filter. Returns whether the store was actually updated: true when the epoch
    /// changed or when this is the first Set, and false when an equal epoch is re-pushed.
    /// <para>
    /// Mirrors filter-store.ts:31-38, including the subtle case where the held filter and the new
    /// filter both carry no epoch: that still counts as an update on the first Set, because the
    /// "not first set" conjunct is what makes the no-op branch reachable.
    /// </para>
    /// </summary>
    public bool Set(EventFilter? filter)
    {
        EventFilter? held = _current;
        if (held != null && EqualEpoch(EpochOf(held), EpochOf(filter)))
        {
            return false; // epoch unchanged and not the first set -> no update
        }
        _current = filter;
        return true;
    }

    /// <summary>
    /// Reports whether an announced epoch differs from the held one. No filter held means refresh;
    /// no announced epoch means there is nothing to compare against, so do not refresh.
    /// </summary>
    public bool ShouldRefresh(ulong? newEpoch)
    {
        ulong? held = Epoch();
        if (held == null)
        {
            return true; // no filter held -> refresh
        }
        if (newEpoch == null)
        {
            return false; // no new epoch to compare
        }
        return !EqualEpoch(held, newEpoch);
    }

    /// <summary>Compares two optional epochs BY VALUE (not by reference boxing identity).</summary>
    internal static bool EqualEpoch(ulong? a, ulong? b)
    {
        if (!a.HasValue) return !b.HasValue;
        if (!b.HasValue) return false;
        return a.GetValueOrDefault() == b.GetValueOrDefault();
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
