using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Sentinel.Probe.Sdk.Enforcement;

namespace Sentinel.Probe.Sdk.Tests;

// Shared test helpers — the .NET analog of the Go *_test.go helper funcs (makeEvent, makeSpec,
// makeDeps, mockDecider, …). All tests live in one namespace so these are shared without ceremony.

internal static class TestSupport
{
    public const string TestKind = "transfer.initiated";
    public const ulong TestEpoch = 5;

    public static readonly Options TestOptions = new()
    {
        SourceHandle = "gateway.tool-calls",
        RequestId = "req-1",
        IdempotencyKey = "idem-1",
    };

    public static ProducerEvent MakeEvent(string kind) =>
        new() { Id = "evt-1", Kind = kind, SchemaVersion = "sentinel.model.v1" };

    public static SpecificationFilter MakeSpec(string specificationId, List<string>? kinds, FailMode failMode, DeliveryMode deliveryMode) =>
        new()
        {
            SpecificationId = specificationId,
            SpecificationVersion = "1.0.0",
            EventMatch = new EventMatch { EventKinds = { kinds ?? new List<string>() }, DeliveryMode = deliveryMode },
            FailMode = failMode,
            EvaluationMode = EvaluationMode.Enforce,
            Readiness = Readiness.Active,
            LatencyBudgetNanoseconds = 10_000,
        };

    public static SpecificationFilter AskAndBlockSpec() =>
        MakeSpec("spec-1", new() { TestKind }, FailMode.Open, DeliveryMode.AskAndBlock);

    public static SpecificationFilter ClosedAskAndBlockSpec() =>
        MakeSpec("spec-1", new() { TestKind }, FailMode.Closed, DeliveryMode.AskAndBlock);

    public static SpecificationFilter ShipAsyncSpec() =>
        MakeSpec("spec-1", new() { TestKind }, FailMode.Open, DeliveryMode.ShipAsync);

    public static EventFilter MakeFilter(ulong? epoch, params SpecificationFilter[] specs)
    {
        var filter = new EventFilter { Specifications = { specs } };
        // proto3 `optional`: null leaves the epoch absent (HasEpoch false), never 0.
        if (epoch.HasValue)
        {
            filter.Epoch = epoch.Value;
        }
        return filter;
    }

    public static Deps MakeDeps(MockDecider mock, long nowNs, Func<SpecificationFilter, FailMode>? accepted = null) =>
        new()
        {
            Decide = mock.DecideAsync,
            NowMonotonicNs = () => nowNs,
            AcceptedFailModeFor = accepted ?? AlwaysOpen,
        };

    public static FailMode AlwaysClosed(SpecificationFilter _) => FailMode.Closed;
    public static FailMode AlwaysOpen(SpecificationFilter _) => FailMode.Open;

    public static DecideResponse MakeResponse(DecisionAction action, params SpecificationDecision[] decisions) =>
        new() { RequestId = "mock", Action = action, Specifications = { decisions } };

    public static SpecificationDecision MakeDecision(string specificationId, DecisionAction action, UnresolvedReason? unresolved = null)
    {
        var decision = new SpecificationDecision
        {
            SpecificationId = specificationId,
            SpecificationVersion = "1.0.0",
            Action = action,
        };
        if (unresolved.HasValue)
        {
            decision.UnresolvedReason = unresolved.Value;
        }
        return decision;
    }
}

// MockDecider — the in-process Decide stub, mirroring the Go mockDecider: a canned response, a
// scripted sequence whose last element repeats, or a forced error, while recording every request.
internal sealed class MockDecider
{
    public DecideResponse? Response { get; set; }
    public List<DecideResponse>? Sequence { get; set; }
    public Exception? Error { get; set; }

    private readonly List<DecideRequest> _requests = new();
    private int _next;
    private readonly object _lock = new();

    public Task<DecideOutcome> DecideAsync(DecideRequest request, CancellationToken cancellationToken)
    {
        lock (_lock) { _requests.Add(request); }

        if (Error != null) return Task.FromResult(new DecideOutcome(null, Error));

        DecideResponse resp;
        if (Sequence is { Count: > 0 })
        {
            resp = Sequence[_next];
            if (_next < Sequence.Count - 1) _next++;
        }
        else if (Response != null)
        {
            resp = Response;
        }
        else
        {
            resp = new DecideResponse { RequestId = request.RequestId, Action = DecisionAction.Permit };
        }
        return Task.FromResult(new DecideOutcome(resp, null));
    }

    public int CallCount { get { lock (_lock) return _requests.Count; } }

    public DecideRequest? LastRequest
    {
        get { lock (_lock) return _requests.Count == 0 ? null : _requests[^1]; }
    }
}
