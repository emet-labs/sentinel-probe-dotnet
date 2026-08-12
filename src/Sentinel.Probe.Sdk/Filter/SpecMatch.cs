using System;
using System.Collections.Generic;
using Emet.Sentinel.Model.V1;

namespace Sentinel.Probe.Sdk.Filter;

// SpecMatch holds the single definition of "does this SpecificationFilter select this
// ProducerEvent", shared by the Filter and Enforcement modules.
//
// It mirrors sdk/go/internal/specmatch/specmatch.go (which mirrors sdk/typescript's
// specSelects). C# has no assembly-internal export, so the equivalent of Go's internal/
// visibility is `internal static`. It is NOT re-exported from the public SDK surface; consumers
// reach it only through Filter and Enforcement.
internal static class SpecMatch
{
    /// <summary>
    /// Reports whether <paramref name="spec"/>'s EventMatch selects <paramref name="event"/>: its
    /// event_kinds is empty, or it includes event.kind. A spec with no EventMatch at all selects
    /// everything, defensively — the same over-approximate-upward choice ADR-0006 requires of the
    /// projection itself.
    /// </summary>
    /// <remarks>
    /// A null EventMatch or empty EventKinds is treated as "matches every kind", giving the
    /// optional-chaining semantics of the reference's nil-safe getters for free.
    /// </remarks>
    public static bool Selects(SpecificationFilter? spec, ProducerEvent? @event)
    {
        var kinds = spec?.EventMatch?.EventKinds;
        if (kinds == null || kinds.Count == 0)
        {
            return true;
        }
        return kinds.Contains(@event?.Kind ?? string.Empty, StringComparer.Ordinal);
    }
}
