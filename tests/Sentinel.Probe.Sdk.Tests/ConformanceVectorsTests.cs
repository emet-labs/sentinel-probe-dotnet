using System.Numerics;
using System.Text.Json;
using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Sentinel.Probe.Sdk.Enforcement;
using Sentinel.Probe.Sdk.Filter;
using Sentinel.Probe.Sdk.Int128;
using Xunit;
using Int128Value = Emet.Sentinel.Model.V1.Int128;

namespace Sentinel.Probe.Sdk.Tests;

public class ConformanceVectorsTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "probe-sdk-conformance");

    private static JsonDocument Load(string name)
    {
        var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name)));
        RejectDuplicateKeys(document.RootElement);
        Assert.Equal("1.0.0", document.RootElement.GetProperty("format_version").GetString());
        return document;
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Assert.True(names.Add(property.Name), $"duplicate JSON key {property.Name}");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                RejectDuplicateKeys(child);
            }
        }
    }

    [Fact]
    public void ManifestSuiteRegistryFailsClosed()
    {
        using var manifest = Load("manifest-v1.json");
        var kinds = manifest.RootElement.GetProperty("suites").EnumerateArray()
            .Select(entry => entry.GetProperty("kind").GetString()).ToArray();
        Assert.Equal(new[] { "spec_match", "int128", "enforcement_gate" }, kinds);
    }

    [Fact]
    public void SharedMalformedCorpusIsRejectedByCategory()
    {
        using var manifest = Load("manifest-v1.json");
        foreach (var fixture in manifest.RootElement.GetProperty("malformed").EnumerateArray())
        {
            var path = fixture.GetProperty("path").GetString()!;
            Assert.Equal(fixture.GetProperty("rejection_category").GetString(), MalformedCategory(path));
        }
    }

    private static string MalformedCategory(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name)));
        try
        {
            RejectDuplicateKeys(document.RootElement);
        }
        catch (Exception error) when (error.Message.Contains("duplicate JSON key", StringComparison.Ordinal))
        {
            return "duplicate-key";
        }
        var root = document.RootElement;
        if (!root.TryGetProperty("format_version", out var version) || version.GetString() != "1.0.0")
            return "version";
        if (!root.TryGetProperty("cases", out var cases))
            return "missing-field";
        var allowed = new HashSet<string>(new[] { "format_version", "kind", "cases" }, StringComparer.Ordinal);
        if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            return "unknown-field";
        var kind = root.GetProperty("kind").GetString();
        if (kind is not ("spec_match" or "int128" or "enforcement_gate"))
            return "unknown-token";
        if (kind == "int128")
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in cases.EnumerateArray())
            {
                if (!ids.Add(item.GetProperty("id").GetString()!))
                    return "duplicate-id";
                var words = new[] { "value", "high", "low" }
                    .Select(field => item.GetProperty(field).GetString()!).ToArray();
                if (words.Any(word => !System.Text.RegularExpressions.Regex.IsMatch(word, "^(0|-?[1-9][0-9]*)$")))
                    return "integer-lexeme";
                var value = BigInteger.Parse(words[0]);
                if (value < -(BigInteger.One << 127) || value >= (BigInteger.One << 127))
                    return "integer-range";
            }
        }
        return "accepted";
    }

    [Fact]
    public void Int128VectorsAssertWordsAndIndependentDecode()
    {
        using var suite = Load("int128-v1.json");
        Assert.Equal("int128", suite.RootElement.GetProperty("kind").GetString());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in suite.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = vector.GetProperty("id").GetString()!;
            Assert.True(ids.Add(id));
            var value = BigInteger.Parse(vector.GetProperty("value").GetString()!);
            var high = long.Parse(vector.GetProperty("high").GetString()!);
            var low = ulong.Parse(vector.GetProperty("low").GetString()!);
            var encoded = Int128Codec.FromBigInteger(value);
            Assert.Equal(high, encoded.High);
            Assert.Equal(low, encoded.Low);
            Assert.Equal(value, Int128Codec.ToBigInteger(new Int128Value { High = high, Low = low }));
        }
    }

    [Fact]
    public void SpecMatchVectorsInvokeProductionPredicate()
    {
        using var suite = Load("spec-match-v1.json");
        Assert.Equal("spec_match", suite.RootElement.GetProperty("kind").GetString());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in suite.RootElement.GetProperty("cases").EnumerateArray())
        {
            Assert.True(ids.Add(vector.GetProperty("id").GetString()!));
            var fixtureFilter = vector.GetProperty("specification_filter");
            var filter = new SpecificationFilter();
            var fixtureMatch = fixtureFilter.GetProperty("event_match");
            if (fixtureMatch.ValueKind != JsonValueKind.Null)
            {
                filter.EventMatch = new EventMatch();
                foreach (var kind in fixtureMatch.GetProperty("event_kinds").EnumerateArray())
                {
                    filter.EventMatch.EventKinds.Add(kind.GetString()!);
                }
                foreach (var key in fixtureMatch.GetProperty("projected_attribute_keys").EnumerateArray())
                {
                    filter.EventMatch.ProjectedAttributeKeys.Add(key.GetString()!);
                }
            }
            var producerEvent = new ProducerEvent
            {
                Kind = vector.GetProperty("producer_event").GetProperty("kind").GetString()!,
            };
            Assert.Equal(vector.GetProperty("expected").GetBoolean(), SpecMatch.Selects(filter, producerEvent));
        }
    }

    [Fact]
    public async Task EnforcementVectorsInvokeProductionGate()
    {
        using var suite = Load("enforcement-gate-v1.json");
        foreach (var vector in suite.RootElement.GetProperty("cases").EnumerateArray())
        {
            var accepted = new Dictionary<string, FailMode>(StringComparer.Ordinal);
            EventFilter? filter = null;
            var fixtureFilter = vector.GetProperty("filter");
            if (fixtureFilter.ValueKind != JsonValueKind.Null)
            {
                filter = new EventFilter { Epoch = ulong.Parse(fixtureFilter.GetProperty("epoch").GetString()!) };
                foreach (var fixture in fixtureFilter.GetProperty("specifications").EnumerateArray())
                {
                    var id = fixture.GetProperty("id").GetString()!;
                    accepted[id] = fixture.GetProperty("accepted_fail_mode").GetString() == "closed" ? FailMode.FailModeClosed : FailMode.FailModeOpen;
                    var match = new EventMatch
                    {
                        DeliveryMode = fixture.GetProperty("delivery_mode").GetString() == "ask_and_block" ? DeliveryMode.DeliveryModeAskAndBlock : DeliveryMode.DeliveryModeShipAsync,
                    };
                    match.EventKinds.AddRange(fixture.GetProperty("event_kinds").EnumerateArray().Select(value => value.GetString()!));
                    filter.Specifications.Add(new SpecificationFilter
                    {
                        SpecificationId = id,
                        EventMatch = match,
                        FailMode = fixture.GetProperty("fail_mode").GetString() == "closed" ? FailMode.FailModeClosed : FailMode.FailModeOpen,
                        EvaluationMode = EvaluationMode.EvaluationModeEnforce,
                        Readiness = Readiness.ReadinessActive,
                        LatencyBudgetNanoseconds = ulong.Parse(fixture.GetProperty("latency_budget_ns").GetString()!),
                    });
                }
            }
            var reads = vector.GetProperty("clock_reads_ns").EnumerateArray().Select(value => long.Parse(value.GetString()!)).ToArray();
            var readIndex = 0;
            var requests = new List<DecideRequest>();
            var decider = vector.GetProperty("decider").GetProperty("result").GetString()!;
            var actions = new Dictionary<string, DecisionAction>
            {
                ["permit"] = DecisionAction.DecisionActionPermit,
                ["deny"] = DecisionAction.DecisionActionDeny,
                ["defer"] = DecisionAction.DecisionActionDefer,
                ["unspecified"] = DecisionAction.DecisionActionUnspecified,
            };
            var outcome = await Gate.GateAsync(
                new ProducerEvent { Id = "fixture-event", Kind = vector.GetProperty("event").GetProperty("kind").GetString()! },
                filter,
                vector.GetProperty("local_deadline_ns").ValueKind == JsonValueKind.Null ? null : long.Parse(vector.GetProperty("local_deadline_ns").GetString()!),
                new Deps
                {
                    NowMonotonicNs = () => readIndex < reads.Length ? reads[readIndex++] : throw new InvalidOperationException("clock script exhausted"),
                    AcceptedFailModeFor = spec => accepted[spec.SpecificationId],
                    Decide = (request, _) =>
                    {
                        requests.Add(request);
                        return Task.FromResult(decider == "transport_error"
                            ? new DecideOutcome(null, new Exception("fixture-transport-error"))
                            : new DecideOutcome(new DecideResponse { Action = actions[decider] }, null));
                    },
                },
                new Options { SourceHandle = "fixture-source", RequestId = "fixture-request", IdempotencyKey = "fixture-idempotency" },
                CancellationToken.None);
            var expected = vector.GetProperty("expected");
            Assert.Equal(expected.GetProperty("kind").GetString(), outcome.Kind.Name());
            Assert.Equal(expected.GetProperty("decide_calls").GetInt32(), requests.Count);
            Assert.Equal(reads.Length, readIndex);
        }
    }
}
