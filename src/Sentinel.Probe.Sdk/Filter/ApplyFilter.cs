using System.Collections.Generic;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Proto;

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
    /// The returned event always carries FRESHLY ALLOCATED repeated fields, including in the
    /// keep-everything branch where the reference aliases the input list directly. The
    /// AttributeEntry elements, Sequence and OccurrenceTime are still shared with the input, so a
    /// caller must not mutate them in place. A deep copy is deliberately not paid for on the emit
    /// hot path.
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

        // 5. Rebuild with every other field unchanged. Sequence and OccurrenceTime are shared
        // with the input, mirroring the reference's shallow-aliasing contract. The generated
        // repeated fields are get-only, so the attribute, capability and predecessor lists are
        // fresh RepeatedFields holding the input's elements (enum values and strings, plus the
        // shared AttributeEntry references). The input message's Clone() is deliberately not used:
        // it deep-copies every attribute only for step 4 to discard the copy, the cost the Go
        // reference also declines on the emit hot path.
        var projectedEvent = new ProducerEvent
        {
            Id = @event.Id,
            Sequence = @event.Sequence,
            SchemaVersion = @event.SchemaVersion,
            Kind = @event.Kind,
            OccurrenceTime = @event.OccurrenceTime,
            Attributes = { trimmed },
            ClaimedCapabilities = { @event.ClaimedCapabilities },
            ClaimedSensitivity = @event.ClaimedSensitivity,
            CausalPredecessorIds = { @event.CausalPredecessorIds }, // never trimmed
        };
        // proto3 `optional`: an absent acknowledged epoch stays absent; it is not copied as 0.
        projectedEvent.SetAcknowledgedFilterEpoch(@event.AcknowledgedFilterEpochOrNull());
        return projectedEvent;
    }
}
