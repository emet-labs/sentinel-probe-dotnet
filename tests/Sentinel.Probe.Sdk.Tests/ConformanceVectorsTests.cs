using System.Numerics;
using System.Text.Json;
using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Sentinel.Probe.Sdk.Enforcement;
using Sentinel.Probe.Sdk.Filter;
using Sentinel.Probe.Sdk.Int128;
using Sentinel.Probe.Sdk.Otlp;
using Xunit;
using Int128Value = Emet.Sentinel.Model.V1.Int128;

namespace Sentinel.Probe.Sdk.Tests;

public class ConformanceVectorsTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "probe-sdk-conformance");

    private static JsonDocument Load(string name, string version = "1.0.0")
    {
        var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name)));
        RejectDuplicateKeys(document.RootElement);
        Assert.Equal(version, document.RootElement.GetProperty("format_version").GetString());
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
        using var manifest = Load("manifest-v1.json", "1.4.0");
        var kinds = manifest.RootElement.GetProperty("suites").EnumerateArray()
            .Select(entry => entry.GetProperty("kind").GetString()).ToArray();
        Assert.Equal(new[] { "spec_match", "int128", "enforcement_gate", "otlp_signal", "sequence_completeness", "decision_wait", "filter_store" }, kinds);
    }

    [Fact]
    public void SharedMalformedCorpusIsRejectedByCategory()
    {
        using var manifest = Load("manifest-v1.json", "1.4.0");
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
                    accepted[id] = fixture.GetProperty("accepted_fail_mode").GetString() == "closed" ? FailMode.Closed : FailMode.Open;
                    var match = new EventMatch
                    {
                        DeliveryMode = fixture.GetProperty("delivery_mode").GetString() == "ask_and_block" ? DeliveryMode.AskAndBlock : DeliveryMode.ShipAsync,
                    };
                    match.EventKinds.AddRange(fixture.GetProperty("event_kinds").EnumerateArray().Select(value => value.GetString()!));
                    filter.Specifications.Add(new SpecificationFilter
                    {
                        SpecificationId = id,
                        EventMatch = match,
                        FailMode = fixture.GetProperty("fail_mode").GetString() == "closed" ? FailMode.Closed : FailMode.Open,
                        EvaluationMode = EvaluationMode.Enforce,
                        Readiness = Readiness.Active,
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
                ["permit"] = DecisionAction.Permit,
                ["deny"] = DecisionAction.Deny,
                ["defer"] = DecisionAction.Defer,
                ["unspecified"] = DecisionAction.Unspecified,
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

    [Fact]
    public void OtlpSignalVectorsDecodeEventsAndMessages()
    {
        using var suite = Load("otlp-signal-v1.json", "1.1.0");
        Assert.Equal("otlp_signal", suite.RootElement.GetProperty("kind").GetString());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in suite.RootElement.GetProperty("cases").EnumerateArray())
        {
            Assert.True(ids.Add(vector.GetProperty("id").GetString()!));
            var bytes = OtlpBytes(vector.GetProperty("path").GetString()!);
            switch (vector.GetProperty("signal").GetString())
            {
                case "trace":
                    AssertTrace(vector, bytes);
                    break;
                case "completeness":
                    AssertCompleteness(vector, bytes);
                    break;
                case "verdict":
                    AssertVerdict(vector, bytes);
                    break;
                case "event_filter":
                    AssertFilter(vector, bytes);
                    break;
                case "decide_response":
                    AssertDecision(vector, bytes);
                    break;
                default:
                    throw new InvalidOperationException(vector.GetProperty("signal").GetString());
            }
        }
    }

    private static void AssertTrace(JsonElement vector, byte[] bytes)
    {
        var reject = vector.TryGetProperty("reject", out var flag) && flag.GetBoolean();
        if (reject)
        {
            Assert.Throws<OtlpExportException>(() =>
            {
                foreach (var span in OtlpTraces.Decode(bytes))
                {
                    span.ToProducerEvent();
                }
            });
            return;
        }
        var events = OtlpTraces.Decode(bytes).Select(span => span.ToProducerEvent()).ToArray();
        var expected = vector.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, events.Length);
        for (var index = 0; index < events.Length; index++)
        {
            var want = expected[index];
            Assert.Equal(want.GetProperty("id").GetString(), events[index].Id);
            Assert.Equal(want.GetProperty("kind").GetString(), events[index].Kind);
            var sequence = events[index].Sequence;
            Assert.NotNull(sequence);
            Assert.Equal(ulong.Parse(want.GetProperty("sequence_epoch").GetString()!), sequence.Epoch);
            Assert.Equal(ulong.Parse(want.GetProperty("sequence_number").GetString()!), sequence.Sequence);
            Assert.Equal(
                want.GetProperty("predecessors").EnumerateArray().Select(item => item.GetString()).ToArray(),
                events[index].CausalPredecessorIds);
        }
    }

    private static void AssertCompleteness(JsonElement vector, byte[] bytes)
    {
        var want = vector.GetProperty("completeness");
        var record = Assert.Single(OtlpLogs.Decode(bytes));
        Assert.Equal(OtlpLogs.CompletenessType, LogText(record, OtlpLogs.PayloadTypeKey));
        Assert.Equal(want.GetProperty("source_handle").GetString(), LogText(record, OtlpLogs.SourceHandleKey));
        Assert.NotNull(record.BodyBytes);
        var signal = ProducerCompletenessSignal.Parser.ParseFrom(record.BodyBytes);
        Assert.Equal(ulong.Parse(want.GetProperty("sequence_epoch").GetString()!), signal.SequenceEpoch);
        Assert.True(signal.HasLastEmitted);
        Assert.Equal(ulong.Parse(want.GetProperty("last_emitted").GetString()!), signal.LastEmitted);
        Assert.True(signal.HasCompleteThrough);
        Assert.Equal(ulong.Parse(want.GetProperty("complete_through").GetString()!), signal.CompleteThrough);
        Assert.True(signal.HasAcknowledgedFilterEpoch);
        Assert.Equal(ulong.Parse(want.GetProperty("acknowledged_filter_epoch").GetString()!), signal.AcknowledgedFilterEpoch);
        Assert.NotNull(signal.ProducerReportedAt);
        Assert.NotNull(signal.ProducerReportedAt.Nanoseconds);
        Assert.Equal(want.GetProperty("clock_domain_id").GetString(), signal.ProducerReportedAt.ClockDomainId);
        Assert.Equal(BigInteger.Parse(want.GetProperty("occurrence_ns").GetString()!), Int128Codec.ToBigInteger(signal.ProducerReportedAt.Nanoseconds));
        Assert.Equal(ulong.Parse(want.GetProperty("uncertainty_nanoseconds").GetString()!), signal.ProducerReportedAt.UncertaintyNanoseconds);
        Assert.Equal(Activity(want.GetProperty("activity").GetString()!), signal.Activity);
    }

    private static void AssertVerdict(JsonElement vector, byte[] bytes)
    {
        var want = vector.GetProperty("verdict");
        using var receiver = OtlpLogReceiver.Start();
        Assert.Equal(200, receiver.Ingest(bytes));
        var verdict = Assert.Single(receiver.Verdicts);
        Assert.Equal(want.GetProperty("id").GetString(), verdict.Id);
        Assert.Equal(Outcome(want.GetProperty("outcome").GetString()!), verdict.Outcome);
    }

    private static void AssertFilter(JsonElement vector, byte[] bytes)
    {
        var want = vector.GetProperty("event_filter");
        var record = Assert.Single(OtlpLogs.Decode(bytes));
        Assert.Equal(OtlpLogs.EventFilterType, LogText(record, OtlpLogs.PayloadTypeKey));
        Assert.NotNull(record.BodyBytes);
        var filter = EventFilter.Parser.ParseFrom(record.BodyBytes);
        Assert.True(filter.HasEpoch);
        Assert.Equal(ulong.Parse(want.GetProperty("epoch").GetString()!), filter.Epoch);
        Assert.Equal(
            want.GetProperty("specification_ids").EnumerateArray().Select(item => item.GetString()).ToArray(),
            filter.Specifications.Select(spec => spec.SpecificationId).ToArray());
    }

    private static void AssertDecision(JsonElement vector, byte[] bytes)
    {
        var want = vector.GetProperty("decide_response");
        var record = Assert.Single(OtlpLogs.Decode(bytes));
        Assert.Equal(OtlpLogs.DecideResponseType, LogText(record, OtlpLogs.PayloadTypeKey));
        Assert.Equal(want.GetProperty("request_id").GetString(), LogText(record, OtlpLogs.RequestIdKey));
        Assert.NotNull(record.BodyBytes);
        var decision = DecideResponse.Parser.ParseFrom(record.BodyBytes);
        Assert.Equal(want.GetProperty("request_id").GetString(), decision.RequestId);
        Assert.Equal(Action(want.GetProperty("action").GetString()!), decision.Action);
        var specs = want.GetProperty("specifications").EnumerateArray().ToArray();
        Assert.Equal(specs.Length, decision.Specifications.Count);
        for (var index = 0; index < specs.Length; index++)
        {
            Assert.Equal(specs[index].GetProperty("id").GetString(), decision.Specifications[index].SpecificationId);
            Assert.Equal(Action(specs[index].GetProperty("action").GetString()!), decision.Specifications[index].Action);
        }
    }

    private static byte[] OtlpBytes(string relative)
    {
        var name = Path.GetFileName(relative.Replace('/', Path.DirectorySeparatorChar));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "testdata", "otlp", name);
            if (File.Exists(path))
            {
                return File.ReadAllBytes(path);
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative);
    }

    private static string? LogText(DecodedLog record, string key)
    {
        foreach (var attribute in record.Attributes)
        {
            if (attribute.Key == key)
            {
                return OtlpTraces.StringOf(attribute.Value);
            }
        }
        return null;
    }

    private static SourceActivityState Activity(string token) => token switch
    {
        "active" => SourceActivityState.Active,
        "idle" => SourceActivityState.IntentionallyIdle,
        _ => throw new InvalidOperationException(token),
    };

    private static VerdictOutcome Outcome(string token) => token switch
    {
        "satisfy" => VerdictOutcome.Satisfy,
        "violate" => VerdictOutcome.Violate,
        "inconclusive" => VerdictOutcome.Inconclusive,
        _ => throw new InvalidOperationException(token),
    };

    private static DecisionAction Action(string token) => token switch
    {
        "permit" => DecisionAction.Permit,
        "deny" => DecisionAction.Deny,
        "defer" => DecisionAction.Defer,
        _ => throw new InvalidOperationException(token),
    };
}
