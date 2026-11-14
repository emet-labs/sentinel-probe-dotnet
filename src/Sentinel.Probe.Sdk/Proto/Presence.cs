using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;

namespace Sentinel.Probe.Sdk.Proto;

// Presence — the bridge between the generated proto3 `optional` fields and the SDK's `ulong?`
// surface (#466).
//
// protoc-gen-csharp renders `optional uint64 x` as a plain `ulong X` plus `HasX` and `ClearX()`.
// Reading `X` on an absent field yields 0, and C# widens that `ulong` into a `ulong?` without a
// word, so `ulong? e = filter.Epoch` compiles and silently turns "absent" into "epoch 0". Epoch 0
// is a legitimate epoch and an absent latency budget is not an exhausted one, so every read of an
// optional field in the SDK goes through an *OrNull helper here, and every write from a nullable
// source goes through a Set* helper that clears the field on null. Missing evidence is never
// collapsed into a default.
internal static class Presence
{
    /// <summary>The filter's epoch, or null when it declares none (never 0 for absent).</summary>
    internal static ulong? EpochOrNull(this EventFilter filter) =>
        filter.HasEpoch ? filter.Epoch : null;

    /// <summary>The Specification's latency budget, or null when it declares none.</summary>
    internal static ulong? LatencyBudgetNanosecondsOrNull(this SpecificationFilter specification) =>
        specification.HasLatencyBudgetNanoseconds ? specification.LatencyBudgetNanoseconds : null;

    /// <summary>The request's remaining transport budget, or null when it declares none (never 0 for absent).</summary>
    internal static ulong? RemainingTransportBudgetNanosecondsOrNull(this DecideRequest request) =>
        request.HasRemainingTransportBudgetNanoseconds ? request.RemainingTransportBudgetNanoseconds : null;

    /// <summary>The Event's acknowledged filter epoch, or null when it declares none.</summary>
    internal static ulong? AcknowledgedFilterEpochOrNull(this ProducerEvent @event) =>
        @event.HasAcknowledgedFilterEpoch ? @event.AcknowledgedFilterEpoch : null;

    /// <summary>Sets the acknowledged filter epoch, or clears it when <paramref name="epoch"/> is null.</summary>
    internal static void SetAcknowledgedFilterEpoch(this ProducerEvent @event, ulong? epoch)
    {
        if (epoch.HasValue)
        {
            @event.AcknowledgedFilterEpoch = epoch.Value;
        }
        else
        {
            @event.ClearAcknowledgedFilterEpoch();
        }
    }

    /// <summary>Sets the request's filter epoch, or clears it when <paramref name="epoch"/> is null.</summary>
    internal static void SetFilterEpoch(this DecideRequest request, ulong? epoch)
    {
        if (epoch.HasValue)
        {
            request.FilterEpoch = epoch.Value;
        }
        else
        {
            request.ClearFilterEpoch();
        }
    }

    /// <summary>Sets the remaining transport budget, or clears it when <paramref name="budget"/> is null.</summary>
    internal static void SetRemainingTransportBudgetNanoseconds(this DecideRequest request, ulong? budget)
    {
        if (budget.HasValue)
        {
            request.RemainingTransportBudgetNanoseconds = budget.Value;
        }
        else
        {
            request.ClearRemainingTransportBudgetNanoseconds();
        }
    }
}
