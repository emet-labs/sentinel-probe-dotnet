using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Sentinel.Probe.Sdk.Enforcement;
using Sentinel.Probe.Sdk.Otlp;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

// Executes the shared decision_wait suite (manifest-v1.json, 1.4.0) through the production
// OtlpLogReceiver and Gate.FromLogReceiver with CancellationToken.None. Only the TimeProvider is
// injected: `deliver` ingests the answer golden from inside CreateTimer, and `expire` fires the
// timer callback there. No test reads a wall clock.
public class DecisionWaitVectorsTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "probe-sdk-conformance");

    private static JsonElement Read(string name) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name))).RootElement.Clone();

    private static List<JsonElement> Cases()
    {
        var manifest = Read("manifest-v1.json");
        Assert.Equal("1.4.0", manifest.GetProperty("format_version").GetString());
        var entry = Assert.Single(manifest.GetProperty("suites").EnumerateArray(),
            suite => suite.GetProperty("kind").GetString() == "decision_wait");
        var document = Read(entry.GetProperty("path").GetString()!);
        Assert.Equal("1.2.0", document.GetProperty("format_version").GetString());
        Assert.Equal("decision_wait", document.GetProperty("kind").GetString());
        var cases = document.GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        return cases;
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetString();

    private static string[] Steps(JsonElement vector, string name) =>
        vector.GetProperty(name).EnumerateArray().Select(step => step.GetString()!).ToArray();

    private sealed class Harness : IDisposable
    {
        public required OtlpLogReceiver Receiver { get; init; }
        public required ManualTimeProvider Clock { get; init; }
        public required DecideFunc Decide { get; init; }

        public void Dispose() => Receiver.Dispose();
    }

    private static Harness Start(JsonElement vector)
    {
        var answerPath = vector.GetProperty("answer").GetString()!;
        Assert.StartsWith("../otlp/", answerPath, StringComparison.Ordinal);
        var answer = OtlpWireTests.Golden(Path.GetFileName(answerPath));
        var onWait = Steps(vector, "on_wait");
        var clock = ManualTimeProvider.AtUnixMilliseconds(0);
        var receiver = OtlpLogReceiver.Start(clock);
        clock.OnCreateTimer = timer =>
        {
            foreach (var step in onWait)
            {
                switch (step)
                {
                    case "deliver":
                        Assert.Equal(200, receiver.Ingest(answer));
                        break;
                    case "expire":
                        timer.Fire();
                        break;
                    default:
                        throw new InvalidOperationException("unknown on_wait step " + step);
                }
            }
        };
        foreach (var step in Steps(vector, "before_ask"))
        {
            Assert.Equal("deliver", step);
            Assert.Equal(200, receiver.Ingest(answer));
        }
        return new Harness { Receiver = receiver, Clock = clock, Decide = Gate.FromLogReceiver(receiver) };
    }

    private static void AssertArmed(JsonElement vector, ManualTimeProvider clock)
    {
        var id = vector.GetProperty("id").GetString();
        var want = OptionalString(vector.GetProperty("expected"), "armed_wait_ns");
        var timers = clock.Timers;
        if (want == null)
        {
            Assert.True(timers.Count == 0, id);
            return;
        }
        var timer = Assert.Single(timers);
        Assert.True(ulong.Parse(want) == (ulong)timer.DueTime.Ticks * 100UL, id);
        Assert.True(timer.Disposed, id);
    }

    private static string Action(DecisionAction action) => action switch
    {
        DecisionAction.Permit => "permit",
        DecisionAction.Deny => "deny",
        _ => "unexpected",
    };

    private static readonly Dictionary<OutcomeKind, string> Kinds = new()
    {
        [OutcomeKind.Permit] = "permit",
        [OutcomeKind.Deny] = "deny",
        [OutcomeKind.FailOpenPermit] = "fail-open-permit",
        [OutcomeKind.FailClosedDeny] = "fail-closed-deny",
    };

    [Fact]
    public async Task GateCasesRunTheReceiverBackedAdapter()
    {
        var cases = Cases().Where(vector => vector.GetProperty("via").GetString() == "gate").ToList();
        Assert.NotEmpty(cases);
        foreach (var vector in cases)
        {
            var id = vector.GetProperty("id").GetString()!;
            using var harness = Start(vector);
            var fixtureFilter = vector.GetProperty("filter");
            var filter = new EventFilter { Epoch = ulong.Parse(fixtureFilter.GetProperty("epoch").GetString()!) };
            var accepted = new Dictionary<string, FailMode>(StringComparer.Ordinal);
            foreach (var fixture in fixtureFilter.GetProperty("specifications").EnumerateArray())
            {
                var specId = fixture.GetProperty("id").GetString()!;
                accepted[specId] = fixture.GetProperty("accepted_fail_mode").GetString() == "closed" ? FailMode.Closed : FailMode.Open;
                var match = new EventMatch
                {
                    DeliveryMode = fixture.GetProperty("delivery_mode").GetString() == "ask_and_block" ? DeliveryMode.AskAndBlock : DeliveryMode.ShipAsync,
                };
                match.EventKinds.AddRange(fixture.GetProperty("event_kinds").EnumerateArray().Select(kind => kind.GetString()!));
                filter.Specifications.Add(new SpecificationFilter
                {
                    SpecificationId = specId,
                    FailMode = fixture.GetProperty("fail_mode").GetString() == "closed" ? FailMode.Closed : FailMode.Open,
                    EvaluationMode = EvaluationMode.Enforce,
                    Readiness = Readiness.Active,
                    LatencyBudgetNanoseconds = ulong.Parse(fixture.GetProperty("latency_budget_ns").GetString()!),
                    EventMatch = match,
                });
            }
            var reads = vector.GetProperty("clock_reads_ns").EnumerateArray().Select(read => long.Parse(read.GetString()!)).ToArray();
            var readIndex = 0;
            var requests = new List<DecideRequest>();
            var deadline = OptionalString(vector, "local_deadline_ns");
            var outcome = await Gate.GateAsync(
                new ProducerEvent { Id = "fixture-event", Kind = vector.GetProperty("event").GetProperty("kind").GetString()! },
                filter,
                deadline == null ? null : long.Parse(deadline),
                new Deps
                {
                    Decide = (request, cancellationToken) =>
                    {
                        requests.Add(request);
                        return harness.Decide(request, cancellationToken);
                    },
                    NowMonotonicNs = () =>
                    {
                        Assert.True(readIndex < reads.Length, id + ": clock script exhausted");
                        return reads[readIndex++];
                    },
                    AcceptedFailModeFor = spec => accepted[spec.SpecificationId],
                },
                new Options { SourceHandle = "fixture-source", RequestId = vector.GetProperty("request_id").GetString()!, IdempotencyKey = "fixture-idempotency" },
                CancellationToken.None);
            var expected = vector.GetProperty("expected");
            Assert.True(Kinds.TryGetValue(outcome.Kind, out var kind), id);
            Assert.True(expected.GetProperty("kind").GetString() == kind, id + ": " + kind);
            Assert.True(expected.GetProperty("decide_calls").GetInt32() == requests.Count, id);
            Assert.True(reads.Length == readIndex, id);
            Assert.True(ulong.Parse(expected.GetProperty("remaining_transport_budget_ns").GetString()!) == requests[0].RemainingTransportBudgetNanoseconds, id);
            AssertArmed(vector, harness.Clock);
        }
    }

    [Fact]
    public async Task AdapterCasesRunAgainstTheReceiver()
    {
        var cases = Cases().Where(vector => vector.GetProperty("via").GetString() == "adapter").ToList();
        Assert.NotEmpty(cases);
        foreach (var vector in cases)
        {
            var id = vector.GetProperty("id").GetString()!;
            using var harness = Start(vector);
            var request = new DecideRequest { RequestId = vector.GetProperty("request_id").GetString()! };
            var budget = OptionalString(vector, "remaining_transport_budget_ns");
            if (budget != null)
            {
                request.RemainingTransportBudgetNanoseconds = ulong.Parse(budget);
            }
            var decided = await harness.Decide(request, CancellationToken.None);
            var result = decided.Error != null || decided.Response == null ? "error" : Action(decided.Response.Action);
            Assert.True(vector.GetProperty("expected").GetProperty("result").GetString() == result, id + ": " + result);
            AssertArmed(vector, harness.Clock);
        }
    }
}
