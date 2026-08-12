using System.Collections.Generic;
using Emet.Sentinel.Model.V1;

namespace Sentinel.Probe.Sdk.Filter;

// Filter projection — the .NET analog of sdk/go/filter/apply.go (ADR-0006).
//
// This is RELEVANCE PROJECTION, not sampling: it never drops a relevant event, never drops an
// attribute any selecting Specification could need, and never invents data. Where the answer is
// uncertain it over-approximates upward — keeping more than strictly necessary is sound; keeping
// less is not.
public static class ApplyFilter
{
    /// <summary>
    /// Projects <paramref name="event"/> against <paramref name="filter"/>. Returns a possibly
    /// attribute-trimmed ProducerEvent when at least one SpecificationFilter's EventMatch selects
    /// the event, and null when none does (the event is irrelevant to every Specification and can
    /// be dropped entirely).
    ///
    /// The algorithm mirrors the reference step for step:
    /// 1. collect the SpecificationFilters whose EventMatch selects the event;
    /// 2. none selecting means drop, sound because no Specification depends on the event;
    /// 3. if ANY selecting spec has an empty projected_attribute_keys, keep every attribute
    ///    (over-approximate upward: that spec might need any of them);
    /// 4. otherwise keep the union of the selecting specs' projected keys;
    /// 5. rebuild the event with every other field unchanged. causal_predecessor_ids is NEVER
    ///    trimmed — it is the causal skeleton, not an attribute.
    ///
    /// A null filter behaves as a filter with no specifications, i.e. drop.
    /// </summary>
    /// <remarks>
    /// The returned event always carries a FRESHLY ALLOCATED attribute list, including in the
    /// keep-everything branch where the reference aliases the input list directly. The
    /// AttributeEntry elements themselves are still shared with the input, so a caller must not
    /// mutate an entry in place. A deep copy is deliberately not paid for on the emit hot path.
    /// </remarks>
    public static ProducerEvent? Apply(ProducerEvent? @event, EventFilter? filter)
    {
        if (@event == null)
        {
            return null;
        }

        // 1. Collect the SpecificationFilters whose EventMatch selects the event.
        var selecting = new List<SpecificationFilter>();
        if (filter != null)
        {
            foreach (var spec in filter.Specifications)
            {
                if (SpecMatch.Selects(spec, @event))
                {
                    selecting.Add(spec);
                }
            }
        }

        // 2. No spec selects: drop entirely.
        if (selecting.Count == 0)
        {
            return null;
        }

        // 3. Any selecting spec with an empty projection set means keep everything.
        bool projectAll = false;
        foreach (var spec in selecting)
        {
            var keys = spec?.EventMatch?.ProjectedAttributeKeys;
            if (keys == null || keys.Count == 0)
            {
                projectAll = true;
                break;
            }
        }

        // 4. Otherwise keep the union of projected keys.
        var trimmed = new List<AttributeEntry>();
        if (projectAll)
        {
            trimmed.AddRange(@event.Attributes);
        }
        else
        {
            var projected = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var spec in selecting)
            {
                var keys = spec?.EventMatch?.ProjectedAttributeKeys;
                if (keys == null) continue;
                foreach (var key in keys) projected.Add(key);
            }
            foreach (var entry in @event.Attributes)
            {
                if (entry != null && projected.Contains(entry.Key))
                {
                    trimmed.Add(entry);
                }
            }
        }

        // 5. Rebuild with every other field unchanged. Only the attribute list is freshly
        // allocated; capabilities and predecessors are shared with the input, mirroring the
        // reference's shallow-aliasing contract.
        return new ProducerEvent
        {
            Id = @event.Id,
            Sequence = @event.Sequence,
            SchemaVersion = @event.SchemaVersion,
            AcknowledgedFilterEpoch = @event.AcknowledgedFilterEpoch,
            Kind = @event.Kind,
            OccurrenceTime = @event.OccurrenceTime,
            Attributes = trimmed,
            ClaimedCapabilities = @event.ClaimedCapabilities,
            ClaimedSensitivity = @event.ClaimedSensitivity,
            CausalPredecessorIds = @event.CausalPredecessorIds, // never trimmed
        };
    }
}
