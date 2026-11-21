using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using Google.Protobuf;
using Sentinel.Probe.Sdk.Emission;
using Sentinel.Probe.Sdk.Int128;
using Sentinel.Probe.Sdk.Otlp;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

// Executes the shared sequence_completeness suite (#461) through the real SequenceAllocator,
// CompletenessHeartbeat and PartialSuccess decoder. The suite is found through the manifest.
public class SequenceCompletenessVectorsTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "probe-sdk-conformance");

    private static JsonElement Read(string name) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name))).RootElement.Clone();

    private static List<JsonElement> Cases()
    {
        var manifest = Read("manifest-v1.json");
        Assert.Equal("1.4.0", manifest.GetProperty("format_version").GetString());
        var entry = Assert.Single(manifest.GetProperty("suites").EnumerateArray(),
            suite => suite.GetProperty("kind").GetString() == "sequence_completeness");
        var document = Read(entry.GetProperty("path").GetString()!);
        Assert.Equal("1.3.0", document.GetProperty("format_version").GetString());
        Assert.Equal("sequence_completeness", document.GetProperty("kind").GetString());
        var cases = document.GetProperty("cases").EnumerateArray().ToList();
        Assert.Equal(cases.Count, cases.Select(c => c.GetProperty("id").GetString()).Distinct().Count());
        foreach (var vector in cases)
        {
            Assert.True(vector.TryGetProperty("steps", out _) != vector.TryGetProperty("response_hex", out _));
        }
        return cases;
    }

    [Fact]
    public async Task ScenariosDriveTheAllocatorAndTheHeartbeat()
    {
        var scenarios = Cases().Where(c => c.TryGetProperty("steps", out _)).ToList();
        Assert.NotEmpty(scenarios);
        foreach (var vector in scenarios)
        {
            var scenario = new Scenario(vector.GetProperty("id").GetString()!, vector.GetProperty("start"));
            var index = 0;
            foreach (var step in vector.GetProperty("steps").EnumerateArray())
            {
                await scenario.StepAsync(index++, step);
            }
        }
    }

    [Fact]
    public void PartialSuccessCasesDecodeRejectedSpans()
    {
        var responses = Cases().Where(c => c.TryGetProperty("response_hex", out _)).ToList();
        Assert.NotEmpty(responses);
        foreach (var vector in responses)
        {
            var body = Convert.FromHexString(vector.GetProperty("response_hex").GetString()!);
            if (vector.TryGetProperty("undecodable", out var undecodable) && undecodable.GetBoolean())
            {
                Assert.Throws<FormatException>(() => PartialSuccess.RejectedSpans(body));
            }
            else
            {
                Assert.Equal(long.Parse(vector.GetProperty("rejected_spans").GetString()!, System.Globalization.CultureInfo.InvariantCulture), PartialSuccess.RejectedSpans(body));
            }
        }
    }

    // One allocator and one heartbeat over an injected clock, Filter-epoch reader and send
    // function. A restart replaces both unless it is refused.
    private sealed class Scenario
    {
        private readonly string _id;
        private readonly ManualTimeProvider _clock;
        private readonly Queue<string> _outcomes = new();
        private readonly List<ProducerCompletenessSignal> _sent = new();
        private readonly List<Exception> _errors = new();
        private ulong? _filterEpoch;
        private ProducerCompletenessSignal? _previous;
        private SequenceAllocator _sequence;
        private CompletenessHeartbeat _heartbeat;

        public Scenario(string id, JsonElement start)
        {
            _id = id;
            _clock = ManualTimeProvider.AtUnixMilliseconds((long)Uint(start.GetProperty("clock_unix_ms"))!.Value);
            _sequence = new SequenceAllocator(_clock, Uint(start.GetProperty("epoch")), Uint(start.GetProperty("previous_epoch")));
            _heartbeat = HeartbeatFor(_sequence);
        }

        public async Task StepAsync(int index, JsonElement step)
        {
            var op = step.GetProperty("op").GetString()!;
            var where = $"{_id} step {index} {op}";
            switch (op)
            {
                case "allocate":
                    Allocate(where, step);
                    break;
                case "allocate_pending":
                {
                    // The Allocate half of a hook entry an earlier "attempt" step already counted.
                    var expect = step.GetProperty("expect");
                    var want = new Slot(Uint(expect.GetProperty("epoch"))!.Value, Uint(expect.GetProperty("number"))!.Value);
                    Assert.True(want == _sequence.Allocate(), where);
                    break;
                }
                case "attempt":
                    // A hook entry that fails before allocation: counted (C1), no slot.
                    _sequence.NoteAttempt();
                    break;
                case "export_ok":
                    _sequence.MarkExported(SlotOf(step));
                    break;
                case "export_fail":
                    _sequence.MarkFailed(SlotOf(step));
                    break;
                case "filter_drop":
                    _sequence.MarkFiltered(SlotOf(step), Uint(step.GetProperty("filter_epoch"))!.Value);
                    break;
                case "set_filter_epoch":
                    _filterEpoch = Uint(step.GetProperty("epoch"));
                    break;
                case "clock":
                    _clock.SetUnixMilliseconds((long)Uint(step.GetProperty("unix_ms"))!.Value);
                    break;
                case "idle":
                    _heartbeat.DeclareIdle();
                    break;
                case "active":
                    _heartbeat.DeclareActive();
                    break;
                case "tick":
                    await ReportAsync(where, step, () => _heartbeat.ReportAsync());
                    break;
                case "shutdown":
                    await ReportAsync(where, step, () => _heartbeat.ShutdownAsync());
                    break;
                case "restart":
                    Restart(where, step);
                    break;
                default:
                    Assert.Fail(where + ": unknown op");
                    break;
            }
        }

        private void Allocate(string where, JsonElement step)
        {
            if (step.TryGetProperty("expect_error", out var code))
            {
                Assert.Equal(code.GetString(), Assert.Throws<SequenceException>(() => HookEntry()).Code);
                return;
            }
            var expect = step.GetProperty("expect");
            var want = new Slot(Uint(expect.GetProperty("epoch"))!.Value, Uint(expect.GetProperty("number"))!.Value);
            Assert.True(want == HookEntry(), where);
        }

        // What the span path does: NoteAttempt first (C1), then Allocate.
        private Slot HookEntry()
        {
            _sequence.NoteAttempt();
            return _sequence.Allocate();
        }

        private async Task ReportAsync(string where, JsonElement step, Func<Task<IReadOnlyList<CompletenessReport>>> call)
        {
            var sends = step.GetProperty("send").EnumerateArray().Select(s => s.GetString()!).ToList();
            _outcomes.Clear();
            sends.ForEach(_outcomes.Enqueue);
            _sent.Clear();
            _errors.Clear();
            var reports = await call();
            var expected = step.GetProperty("expect").EnumerateArray().ToList();
            Assert.True(expected.Count == _sent.Count, $"{where}: sent {_sent.Count}, expected {expected.Count}");
            Assert.Equal(sends.Select(s => s == "accepted"), reports.Select(r => r.Accepted));
            for (var i = 0; i < expected.Count; i++)
            {
                AssertSignal($"{where} signal {i}", _sent[i], expected[i]);
            }
            var codes = _errors.OfType<SequenceException>().Select(e => e.Code).ToList();
            var wantCodes = step.TryGetProperty("expect_error", out var code) ? new List<string> { code.GetString()! } : new List<string>();
            Assert.Equal(wantCodes, codes);
            Assert.Equal(sends.Count(s => s == "not_accepted"), _errors.OfType<OtlpExportException>().Count());
            var filtered = step.GetProperty("filtered").EnumerateArray()
                .Select(r => new FilteredRange(Uint(r.GetProperty("first"))!.Value, Uint(r.GetProperty("last"))!.Value, Uint(r.GetProperty("filter_epoch"))!.Value))
                .ToList();
            Assert.Equal(filtered, _sequence.Declaration().Filtered);
        }

        private void Restart(string where, JsonElement step)
        {
            _clock.SetUnixMilliseconds((long)Uint(step.GetProperty("clock_unix_ms"))!.Value);
            var previousElement = step.GetProperty("previous_epoch");
            var previous = previousElement.ValueKind == JsonValueKind.String && previousElement.GetString() == "inherit"
                ? _sequence.Epoch()
                : Uint(previousElement);
            var epoch = Uint(step.GetProperty("epoch"));
            if (step.TryGetProperty("expect_error", out var code))
            {
                var refusal = Assert.Throws<SequenceException>(() => new SequenceAllocator(_clock, epoch, previous));
                Assert.True(code.GetString() == refusal.Code, where);
                return;
            }
            _heartbeat.Stop();
            _sequence = new SequenceAllocator(_clock, epoch, previous);
            _heartbeat = HeartbeatFor(_sequence);
        }

        private void AssertSignal(string where, ProducerCompletenessSignal signal, JsonElement want)
        {
            // Parse the wire bytes, so presence is checked as Sentinel sees it.
            var decoded = ProducerCompletenessSignal.Parser.ParseFrom(signal.ToByteArray());
            Assert.True(Uint(want.GetProperty("sequence_epoch")) == decoded.SequenceEpoch, where + " sequence_epoch");
            Assert.True(decoded.HasLastEmitted && decoded.HasCompleteThrough && decoded.HasAcknowledgedFilterEpoch, where + " presence");
            Assert.True(Uint(want.GetProperty("last_emitted")) == decoded.LastEmitted, where + " last_emitted");
            Assert.True(Uint(want.GetProperty("complete_through")) == decoded.CompleteThrough, where + " complete_through");
            Assert.True(Uint(want.GetProperty("acknowledged_filter_epoch")) == decoded.AcknowledgedFilterEpoch, where + " acknowledged_filter_epoch");
            var activity = want.GetProperty("activity").GetString() == "idle" ? SourceActivityState.IntentionallyIdle : SourceActivityState.Active;
            Assert.True(activity == decoded.Activity, where + " activity");
            foreach (var (time, key) in new[] { (decoded.ProducerReportedAt, "reported_at_ns"), (decoded.ActivitySince, "activity_since_ns") })
            {
                Assert.True(time.ClockDomainId == "unix", $"{where} {key} clock domain");
                Assert.True(time.UncertaintyNanoseconds == 0, $"{where} {key} uncertainty");
                Assert.True(BigInteger.Parse(want.GetProperty(key).GetString()!, System.Globalization.CultureInfo.InvariantCulture) == Int128Codec.ToBigInteger(time.Nanoseconds), $"{where} {key}");
            }
            AssertChain(where, decoded, want);
            Assert.True(want.GetProperty("restates_previous").GetBoolean() == Restates(signal), where + " restates_previous");
            _previous = signal;
        }

        // The four epoch-chain fields (wire spec section 1). A key the vector omits is not
        // asserted; a null value asserts the field is absent from the wire.
        private static void AssertChain(string where, ProducerCompletenessSignal decoded, JsonElement want)
        {
            void Check(string key, bool present, Func<string> actual)
            {
                if (!want.TryGetProperty(key, out var expected))
                {
                    return;
                }
                Assert.True((expected.ValueKind != JsonValueKind.Null) == present, $"{where} {key} presence");
                if (expected.ValueKind != JsonValueKind.Null)
                {
                    var text = expected.ValueKind == JsonValueKind.True ? "true" : expected.GetString();
                    Assert.True(text == actual(), $"{where} {key}");
                }
            }

            var ci = System.Globalization.CultureInfo.InvariantCulture;
            Check("attempted", decoded.HasAttempted, () => decoded.Attempted.ToString(ci));
            Check("predecessor_epoch", decoded.HasPredecessorEpoch, () => decoded.PredecessorEpoch.ToString(ci));
            Check("sealed_at_ns", decoded.SealedAt != null, () => Int128Codec.ToBigInteger(decoded.SealedAt!.Nanoseconds).ToString(ci));
            // superseded: false means absent (or false) on the wire.
            if (want.TryGetProperty("superseded", out var superseded))
            {
                Assert.True((superseded.ValueKind == JsonValueKind.True) == (decoded.HasSuperseded && decoded.Superseded), $"{where} superseded");
            }
            if (decoded.SealedAt != null)
            {
                Assert.True(decoded.SealedAt.ClockDomainId == "unix", $"{where} sealed_at clock domain");
                Assert.True(
                    Int128Codec.ToBigInteger(decoded.SealedAt.Nanoseconds) <= Int128Codec.ToBigInteger(decoded.ProducerReportedAt.Nanoseconds),
                    $"{where} sealed_at is not after the row's report");
            }
            if (decoded.HasAttempted)
            {
                Assert.True(decoded.Attempted >= decoded.LastEmitted, $"{where} attempted >= last_emitted");
            }
        }

        // The signal, re-encoded with the previous signal's report nanoseconds, is byte-identical
        // to the previous signal's encoding.
        private bool Restates(ProducerCompletenessSignal signal)
        {
            if (_previous == null)
            {
                return false;
            }
            var aligned = signal.Clone();
            aligned.ProducerReportedAt = signal.ProducerReportedAt.Clone();
            aligned.ProducerReportedAt.Nanoseconds = _previous.ProducerReportedAt.Nanoseconds.Clone();
            return aligned.ToByteArray().AsSpan().SequenceEqual(_previous.ToByteArray());
        }

        private CompletenessHeartbeat HeartbeatFor(SequenceAllocator sequence) =>
            new(
                sequence,
                (signal, cancellationToken) =>
                {
                    _sent.Add(signal);
                    var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : "unscripted";
                    return outcome == "accepted"
                        ? Task.CompletedTask
                        : Task.FromException(new OtlpExportException(_id + ": send " + outcome));
                },
                () => _filterEpoch,
                _clock,
                TimeSpan.Zero,
                _errors.Add);

        private static Slot SlotOf(JsonElement step) =>
            new(Uint(step.GetProperty("epoch"))!.Value, Uint(step.GetProperty("number"))!.Value);

        private static ulong? Uint(JsonElement value) =>
            value.ValueKind == JsonValueKind.Null
                ? null
                : ulong.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }
}
