using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Sentinel.Probe.Sdk.Connect;
using Sentinel.Probe.Sdk.Filter;

namespace Sentinel.Probe.Sdk.Enforcement;

// Enforcement gate — the .NET analog of sdk/go/enforcement/gate.go (mirrors enforcement-gate.ts).
//
// Enforces one ASK_AND_BLOCK event and returns the action the Probe must take. The package takes a
// plain delegate rather than the transport interface, so it stays testable without a network or a
// clock; the ProbeClient ships the adapter (DecideFunc).

/// <summary>The outcome of a Decide RPC, decoupled from the transport: one of Response/Error.</summary>
public readonly record struct DecideOutcome(DecideResponse? Response, Exception? Error);

/// <summary>The delegate the gate's Decide dependency expects.</summary>
public delegate Task<DecideOutcome> DecideFunc(DecideRequest request, CancellationToken cancellationToken);

/// <summary>Enumerates the actions a Probe can be told to take.</summary>
public enum OutcomeKind
{
    /// <summary>Never returned; exists so an accidentally constructed outcome does not read as a permit.</summary>
    Unspecified = 0,
    Permit,
    Deny,
    Defer,
    FailOpenPermit,
    FailClosedDeny,
    NoFilter,
}

public static class OutcomeKindExtensions
{
    /// <summary>Readable name for audit records.</summary>
    public static string Name(this OutcomeKind k) => k switch
    {
        OutcomeKind.Permit => "permit",
        OutcomeKind.Deny => "deny",
        OutcomeKind.Defer => "defer",
        OutcomeKind.FailOpenPermit => "fail-open-permit",
        OutcomeKind.FailClosedDeny => "fail-closed-deny",
        OutcomeKind.NoFilter => "no-filter",
        _ => "unspecified",
    };

    /// <summary>
    /// Reports whether the Probe may proceed. Both permit and fail-open-permit allow the action;
    /// deny, fail-closed-deny and defer do not. no-filter permits, matching the conservative
    /// default of not blocking on absent policy. The zero value (Unspecified) never permits.
    /// </summary>
    public static bool Permitted(this OutcomeKind k) =>
        k == OutcomeKind.Permit || k == OutcomeKind.FailOpenPermit || k == OutcomeKind.NoFilter;
}

/// <summary>What the Probe must do, plus the evidence for why. Reason is populated on every outcome.</summary>
public sealed class GateOutcome
{
    public OutcomeKind Kind { get; init; } = OutcomeKind.Unspecified;
    public string Reason { get; init; } = "";
    public ulong? FilterEpoch { get; init; }
    public IReadOnlyList<SpecificationDecision> Specifications { get; init; } = Array.Empty<SpecificationDecision>();
}

/// <summary>The effects the gate needs, all injected so the gate itself stays pure.</summary>
public sealed class Deps
{
    /// <summary>Performs the ask. In production this wraps the transport; in tests it is a stub.</summary>
    public required DecideFunc Decide { get; init; }

    /// <summary>Reads the host's monotonic clock in nanoseconds. Required whenever deadlineNs is set.</summary>
    public required Func<long> NowMonotonicNs { get; init; }

    /// <summary>
    /// Reports the fail mode the deployment has actually contracted for a spec. A spec declaring
    /// CLOSED without an accepted contract downgrades to OPEN: an operator must have agreed to be
    /// blocked.
    /// <para>
    /// REQUIRED whenever an enforcing Specification selects the event. Gate throws when it is null,
    /// exactly as it already would on a null Decide or a null NowMonotonicNs with a deadline set.
    /// Treating null as "nothing accepted" would make this the one dependency whose absence silently
    /// WEAKENS enforcement: a deployment that wires up Decide and forgets this one would get a gate
    /// that can never fail closed, with no signal at all.
    /// </para>
    /// </summary>
    public required Func<SpecificationFilter, FailMode> AcceptedFailModeFor { get; init; }
}

/// <summary>The per-call identifiers stamped into the DecideRequest.</summary>
public sealed class Options
{
    public string SourceHandle { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
}

public static class Gate
{
    /// <summary>
    /// Enforces one ASK_AND_BLOCK event and returns the action the Probe must take. Control flow
    /// mirrors the reference step for step:
    /// 1. no filter, or a filter with no epoch, returns OutcomeNoFilter without asking;
    /// 2. the enforcing set is the specs that select the event AND declare ASK_AND_BLOCK delivery;
    ///    an empty set means the event is ship-async and the gate is a no-op permit;
    /// 3. the aggregate fail mode is CLOSED iff some enforcing spec declares CLOSED and the
    ///    deployment has accepted CLOSED for it — fail-closed wins over any number of open specs;
    /// 4. if a deadline was set and the budget is already exhausted, apply the fail mode WITHOUT
    ///    asking;
    /// 5. build the DecideRequest from the event the caller passed (already projected by
    ///    ApplyFilter — Gate never projects);
    /// 6. ask; a dead cancellation token is classified as a transport error and routed into the
    ///    fail mode WITHOUT asking (never a permit, never a throw); any other error, including one
    ///    returned by the delegate, applies the aggregate fail mode;
    /// 7. PERMIT permits, DENY denies, DEFER defers while budget remains or no deadline was set and
    ///    otherwise applies the fail mode, and UNSPECIFIED applies the fail mode — never a blind
    ///    permit.
    /// </summary>
    public static async Task<GateOutcome> GateAsync(
        ProducerEvent @event,
        EventFilter? filter,
        long? deadlineNs,
        Deps deps,
        Options options,
        CancellationToken cancellationToken)
    {
        // 1. No-filter guard. Presence is `Epoch == null`: epoch 0 is a legitimate epoch, so
        //    `Epoch == 0` would misclassify it as "no policy held".
        if (filter == null || filter.Epoch == null)
        {
            return new GateOutcome { Kind = OutcomeKind.NoFilter, Reason = "no-filter" };
        }
        ulong filterEpoch = filter.Epoch.GetValueOrDefault();

        // 2. Enforcing set: selects the event AND asks-and-blocks.
        var enforcing = new List<SpecificationFilter>();
        foreach (var spec in filter.Specifications)
        {
            if (spec != null && SpecMatch.Selects(spec, @event) && IsEnforceable(spec))
            {
                enforcing.Add(spec);
            }
        }
        if (enforcing.Count == 0)
        {
            return new GateOutcome { Kind = OutcomeKind.Permit, Reason = "no-ask-and-block-spec", FilterEpoch = filterEpoch };
        }

        // 3. Aggregate fail mode: fail-closed wins.
        FailMode aggregateFailMode = ComputeAggregateFailMode(enforcing, deps);
        ulong effective = ulong.MaxValue;
        foreach (var spec in enforcing)
        {
            if (!spec.HasLatencyBudgetNanoseconds || spec.LatencyBudgetNanoseconds == 0)
                return ApplyFailMode(aggregateFailMode, "budget-exhausted", filterEpoch, null);
            effective = Math.Min(effective, spec.LatencyBudgetNanoseconds);
        }
        if (deps.NowMonotonicNs == null)
            return ApplyFailMode(aggregateFailMode, "clock-unavailable", filterEpoch, null);
        long anchor = deps.NowMonotonicNs();
        if (deadlineNs.HasValue) effective = Math.Min(effective, Budget.RemainingTransportBudgetNs(deadlineNs.Value, anchor));

        // 4. Budget. Exhausted before the call means apply the fail mode without asking, so a Probe
        //    that is already out of time does not spend more of it.
        ulong remaining = Budget.Remaining(anchor, effective, deps.NowMonotonicNs());
        if (remaining == 0) return ApplyFailMode(aggregateFailMode, "budget-exhausted", filterEpoch, null);
        ulong? remainingBudget = remaining;

        // 5. Build the request from the already-projected event.
        var request = new DecideRequest
        {
            RequestId = options.RequestId,
            IdempotencyKey = options.IdempotencyKey,
            SourceHandle = options.SourceHandle,
            FilterEpoch = filterEpoch,
            ProducerEvent = @event,
            RemainingTransportBudgetNanoseconds = remainingBudget,
        };

        // 6. A dead cancellation token is a transport error: never a permit, never a throw, and no
        //    ask is issued (divergence D17).
        if (cancellationToken.IsCancellationRequested)
        {
            return ApplyFailMode(aggregateFailMode, DescribeError(new OperationCanceledException()), filterEpoch, null);
        }

        DecideOutcome outcome;
        try
        {
            outcome = await deps.Decide(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ApplyFailMode(aggregateFailMode, DescribeError(ex), filterEpoch, null);
        }
        if (outcome.Error != null)
        {
            return ApplyFailMode(aggregateFailMode, DescribeError(outcome.Error), filterEpoch, null);
        }

        // Guard: a null response with null error is treated as unspecified-action
        // (mirrors Go's nil-safe getters returning zero values on a nil *DecideResponse).
        if (outcome.Response is null)
        {
            return ApplyFailMode(aggregateFailMode, "unspecified-action", filterEpoch, null);
        }

        // 7. Honour the answer.
        return HandleResponse(outcome.Response, aggregateFailMode, filterEpoch, anchor, effective, deps);
    }

    private static GateOutcome HandleResponse(
        DecideResponse response,
        FailMode aggregateFailMode,
        ulong filterEpoch,
        long anchor,
        ulong effectiveBudget,
        Deps deps)
    {
        var decisions = response.Specifications;

        switch (response.Action)
        {
            case DecisionAction.DecisionActionPermit:
                return new GateOutcome { Kind = OutcomeKind.Permit, Reason = "permit", FilterEpoch = filterEpoch, Specifications = decisions };

            case DecisionAction.DecisionActionDeny:
                return new GateOutcome { Kind = OutcomeKind.Deny, Reason = "deny", FilterEpoch = filterEpoch, Specifications = decisions };

            case DecisionAction.DecisionActionDefer:
                if (Budget.Remaining(anchor, effectiveBudget, deps.NowMonotonicNs()) > 0)
                {
                    return new GateOutcome { Kind = OutcomeKind.Defer, Reason = "defer", FilterEpoch = filterEpoch, Specifications = decisions };
                }
                return ApplyFailMode(aggregateFailMode, "defer-budget-exhausted", filterEpoch, decisions);

            case DecisionAction.DecisionActionUnspecified:
                return ApplyFailMode(aggregateFailMode, "unspecified-action", filterEpoch, decisions);

            default:
                // A future action this Probe does not understand is unresolved, not permitted.
                return ApplyFailMode(aggregateFailMode, "unspecified-action", filterEpoch, decisions);
        }
    }

    private static GateOutcome ApplyFailMode(FailMode failMode, string reason, ulong filterEpoch, IReadOnlyList<SpecificationDecision>? decisions)
    {
        OutcomeKind kind = failMode == FailMode.FailModeClosed ? OutcomeKind.FailClosedDeny : OutcomeKind.FailOpenPermit;
        return new GateOutcome { Kind = kind, Reason = reason, FilterEpoch = filterEpoch, Specifications = decisions ?? Array.Empty<SpecificationDecision>() };
    }

    /// <summary>
    /// Returns CLOSED iff some enforcing spec both DECLARES CLOSED and has CLOSED accepted by the
    /// deployment. Declaration alone downgrades to OPEN. FAIL_MODE_UNSPECIFIED is OPEN. Throws
    /// when <see cref="Deps.AcceptedFailModeFor"/> is null: a missing contract source is a wiring
    /// bug, and defaulting it to OPEN would silently disable fail-closed for every Specification.
    /// </summary>
    private static FailMode ComputeAggregateFailMode(List<SpecificationFilter> enforcing, Deps deps)
    {
        if (deps.AcceptedFailModeFor == null)
        {
            throw new InvalidOperationException(
                "Enforcement.Deps.AcceptedFailModeFor is required when an enforcing Specification " +
                "selects the event; a null contract source cannot be defaulted to fail-open without " +
                "silently disabling fail-closed enforcement");
        }
        foreach (var spec in enforcing)
        {
            if (spec.FailMode != FailMode.FailModeClosed) continue;
            if (deps.AcceptedFailModeFor(spec) == FailMode.FailModeClosed)
            {
                return FailMode.FailModeClosed;
            }
        }
        return FailMode.FailModeOpen;
    }

    private static bool IsEnforceable(SpecificationFilter spec) =>
        spec?.EventMatch?.DeliveryMode == DeliveryMode.DeliveryModeAskAndBlock &&
        spec.EvaluationMode == EvaluationMode.EvaluationModeEnforce &&
        spec.Readiness == Readiness.ReadinessActive;

    /// <summary>
    /// Renders a transport failure for the audit record, distinguishing a cancellation/timeout from
    /// a Connect status so an operator can tell "the host gave up" from "the endpoint said no".
    /// Every error class still routes into the fail mode.
    /// </summary>
    private static string DescribeError(Exception err)
    {
        if (err is ConnectException ce)
        {
            return "connect-" + ConnectException.WireName(ce.Code) + ": " + err.Message;
        }
        if (err is TimeoutException)
        {
            return "context-deadline-exceeded: " + err.Message;
        }
        if (err is OperationCanceledException)
        {
            return "context-canceled: " + err.Message;
        }
        return "transport-error: " + err.Message;
    }
}
