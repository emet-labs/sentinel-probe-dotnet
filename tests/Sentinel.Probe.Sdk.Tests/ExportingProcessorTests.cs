using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Sentinel.Probe.Sdk.Client;
using Sentinel.Probe.Sdk.Emission;
using Sentinel.Probe.Sdk.Otlp;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

// Hardening of the exporting path: it never throws into host code, retries only transport errors,
// drops (and fails) on overflow, flushes before the final heartbeat, and exports a span whose
// remote parent is not a Probe as a causal root. Hermetic: stub HttpMessageHandler, no network.
public class ExportingProcessorTests
{
    private static readonly ActivitySource Source = new("sentinel.test.exporting");

    static ExportingProcessorTests()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = s => s.Name == "sentinel.test.exporting",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        });
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _respond;
        private int _calls;
        public List<(string Url, byte[] Body)> Requests { get; } = new();
        public int Calls => Volatile.Read(ref _calls);

        public Handler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) => _respond = respond;

        public static Handler Status(HttpStatusCode code, string body = "") =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) }));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            int n;
            lock (Requests)
            {
                Requests.Add((request.RequestUri!.AbsolutePath, body));
                n = ++_calls;
            }
            return await _respond(request, n);
        }
    }

    private sealed class Rig : IDisposable
    {
        public SequenceAllocator Sequence { get; } = new();
        public List<ExportFailure> Failures { get; } = new();
        public List<string> Logs { get; } = new();
        public OtlpExportingSpanProcessor Processor { get; }
        public Handler Http { get; }
        private readonly OtlpExporter _exporter;
        private readonly SequenceStampingProcessor _stamp;

        public Rig(
            Handler http,
            bool allowUnattributed = false,
            int capacity = 4096,
            int maxBatch = 32,
            Action<ExportFailure>? onError = null,
            Action<string>? log = null,
            FilterStore? filters = null,
            TimeProvider? time = null,
            IReadOnlyDictionary<string, string>? resource = null)
        {
            Http = http;
            _exporter = new OtlpExporter("http://sentinel.invalid", new HttpClient(http), resource);
            _stamp = new SequenceStampingProcessor(Sequence);
            Processor = new OtlpExportingSpanProcessor(new OtlpExportingSpanProcessorOptions
            {
                SourceHandle = "svc",
                Sequence = Sequence,
                Exporter = _exporter,
                SchemaVersion = "v1",
                Filters = filters,
                AllowUnattributedRemoteParent = allowUnattributed,
                QueueCapacity = capacity,
                MaxBatch = maxBatch,
                OnError = f =>
                {
                    lock (Failures) Failures.Add(f);
                    onError?.Invoke(f);
                },
                Log = m =>
                {
                    lock (Logs) Logs.Add(m);
                    log?.Invoke(m);
                },
                TimeProvider = time,
                Delay = (_, _) => Task.CompletedTask,
            });
        }

        /// <summary>Starts, stamps, ends and hands one Event activity to the processor.</summary>
        public Activity Emit(ActivityContext parent = default, string? parentEventId = null)
        {
            var a = ProbeActivities.StartEvent(Source, "svc.did", ActivityKind.Internal, parent, parentEventId)!;
            _stamp.OnStart(a);
            a.SetTag("sagashop.event", "x");
            a.Stop();
            Processor.OnEnd(a);
            return a;
        }

        public SequenceDeclaration Declaration => Sequence.Declaration();

        public void Dispose()
        {
            Processor.Dispose();
            _exporter.Dispose();
            _stamp.Dispose();
        }
    }

    private static ActivityContext Remote() =>
        new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, null, isRemote: true);

    private static ActivityContext Local() =>
        new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, null, isRemote: false);

    // ---- 1. never throw from the span-end path ---------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task a_failing_export_does_not_throw_out_of_span_end_and_fails_the_slot(HttpStatusCode status)
    {
        using var rig = new Rig(Handler.Status(status, "boom"));
        var ex = Record.Exception(() => rig.Emit());
        Assert.Null(ex);
        await rig.Processor.ShutdownAsync();
        Assert.Equal((1UL, 0UL), (rig.Declaration.LastEmitted, rig.Declaration.CompleteThrough));
        Assert.Equal(1, rig.Processor.FailedCount);
        Assert.Single(rig.Failures);
        Assert.Contains("boom", rig.Failures[0].Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task a_transport_exception_does_not_throw_and_fails_the_slot()
    {
        using var rig = new Rig(new Handler((_, _) => throw new HttpRequestException("connection refused")));
        Assert.Null(Record.Exception(() => rig.Emit()));
        await rig.Processor.ShutdownAsync();
        Assert.Equal(0UL, rig.Declaration.CompleteThrough);
        Assert.Equal(ExportFailureKind.Transport, rig.Failures.Single().Kind);
    }

    [Fact]
    public async Task a_conversion_failure_is_caught_and_fails_the_slot()
    {
        // A LOCAL parent without sentinel.parent.event.id is refused, even with the option on.
        using var rig = new Rig(Handler.Status(HttpStatusCode.OK), allowUnattributed: true);
        Assert.Null(Record.Exception(() => rig.Emit(Local())));
        await rig.Processor.ShutdownAsync();
        Assert.Equal((1UL, 0UL), (rig.Declaration.LastEmitted, rig.Declaration.CompleteThrough));
        Assert.Equal(0, rig.Http.Calls);
        Assert.Contains(rig.Failures, f => f.Kind == ExportFailureKind.Encode);
    }

    [Fact]
    public async Task a_throwing_error_callback_and_log_sink_never_reach_the_host()
    {
        using var rig = new Rig(
            Handler.Status(HttpStatusCode.Forbidden),
            onError: _ => throw new InvalidOperationException("callback"),
            log: _ => throw new InvalidOperationException("sink"));
        Assert.Null(Record.Exception(() => rig.Emit()));
        Assert.Null(Record.Exception(() => rig.Emit()));
        await rig.Processor.ShutdownAsync();
        Assert.Equal(2, rig.Processor.FailedCount);
    }

    [Fact]
    public void an_activity_without_a_slot_is_ignored_without_throwing()
    {
        using var rig = new Rig(Handler.Status(HttpStatusCode.OK));
        var a = new Activity("not-an-event");
        a.Start();
        a.Stop();
        Assert.Null(Record.Exception(() => rig.Processor.OnEnd(a)));
        Assert.Null(Record.Exception(() => rig.Processor.OnEnd(null!)));
        Assert.Equal(0, rig.Http.Calls);
    }

    [Fact]
    public async Task logging_is_rate_limited()
    {
        var time = ManualTimeProvider.AtUnixMilliseconds(1_000_000);
        using var rig = new Rig(Handler.Status(HttpStatusCode.Forbidden), time: time);
        for (var i = 0; i < 5; i++) rig.Emit(Local()); // each failure is its own report
        await rig.Processor.ShutdownAsync();
        Assert.Equal(5, rig.Processor.FailedCount);
        Assert.Equal(5, rig.Failures.Count);
        Assert.Equal(5, rig.Failures.Sum(f => f.Slots.Count));
        Assert.Single(rig.Logs); // later failures fall inside the interval
    }

    // ---- 2. unattributed remote parent ---------------------------------------------------------

    [Fact]
    public async Task an_unattributed_remote_parent_is_refused_by_default()
    {
        using var rig = new Rig(Handler.Status(HttpStatusCode.OK));
        rig.Emit(Remote());
        await rig.Processor.ShutdownAsync();
        Assert.Equal(0UL, rig.Declaration.CompleteThrough);
        Assert.Contains("parent span is missing sentinel.parent.event.id", rig.Failures.Single().Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, rig.Http.Calls);
    }

    [Fact]
    public async Task an_unattributed_remote_parent_exports_as_a_causal_root_when_allowed()
    {
        using var rig = new Rig(Handler.Status(HttpStatusCode.OK), allowUnattributed: true);
        var a = rig.Emit(Remote());
        await rig.Processor.ShutdownAsync();
        Assert.True(a.ParentSpanId != default); // the host copy keeps its parent (Tempo)
        Assert.Equal((1UL, 1UL), (rig.Declaration.LastEmitted, rig.Declaration.CompleteThrough));
        var span = Assert.Single(OtlpTraces.Decode(rig.Http.Requests.Single().Body));
        Assert.False(span.HasParentSpanId); // no parent span id on the Sentinel copy
        Assert.Empty(span.ToProducerEvent().CausalPredecessorIds);
        Assert.DoesNotContain(span.Attributes, x => x.Key == OtlpTraces.ParentEventIdKey);
    }

    [Fact]
    public async Task a_remote_parent_with_an_event_id_still_carries_the_edge()
    {
        using var rig = new Rig(Handler.Status(HttpStatusCode.OK), allowUnattributed: true);
        rig.Emit(Remote(), "caller:7:3");
        await rig.Processor.ShutdownAsync();
        var span = Assert.Single(OtlpTraces.Decode(rig.Http.Requests.Single().Body));
        Assert.Equal(new[] { "caller:7:3" }, span.ToProducerEvent().CausalPredecessorIds);
        Assert.False(span.HasParentSpanId);
    }

    [Fact]
    public void span_to_event_gates_the_unattributed_remote_parent_on_the_option()
    {
        var a = ProbeActivities.StartEvent(Source, "x", ActivityKind.Internal, Remote())!;
        a.Stop();
        var strict = new SpanConversion { Activity = a, EventId = "e" };
        Assert.Throws<OtlpExportException>(() => SpanToEventConverter.SpanToEvent(strict));
        var relaxed = new SpanConversion { Activity = a, EventId = "e", AllowUnattributedRemoteParent = true };
        Assert.Empty(SpanToEventConverter.SpanToEvent(relaxed).CausalPredecessorIds);
    }

    [Fact]
    public void start_event_honours_an_explicit_parent_over_activity_current()
    {
        using var ambient = new Activity("request");
        ambient.Start();
        var parent = Remote();
        var a = ProbeActivities.StartEvent(Source, "x", ActivityKind.Internal, parent)!;
        try
        {
            Assert.Equal(parent.TraceId, a.TraceId);
            Assert.Equal(parent.SpanId, a.ParentSpanId);
            Assert.True(a.HasRemoteParent);
            Assert.NotEqual(ambient.TraceId, a.TraceId);
            Assert.Same(a, Activity.Current);
        }
        finally
        {
            a.Stop();
            ambient.Stop();
        }
    }

    [Fact]
    public void start_event_under_an_ambient_request_span_is_a_remote_parent_without_an_event_id()
    {
        using var request = new Activity("request");
        request.Start();
        var a = ProbeActivities.StartEvent(Source, "x")!;
        try
        {
            Assert.Equal(request.TraceId, a.TraceId);
            Assert.Equal(request.SpanId, a.ParentSpanId);
            Assert.True(a.HasRemoteParent);
            Assert.Null(a.GetTagItem(OtlpTraces.ParentEventIdKey));
        }
        finally
        {
            a.Stop();
            request.Stop();
        }
    }

    [Fact]
    public void start_event_under_an_ambient_probe_event_is_a_local_parent_with_its_event_id()
    {
        var outer = ProbeActivities.StartEvent(Source, "outer")!;
        outer.SetTag(OtlpTraces.EventIdKey, "svc:1:1");
        var inner = ProbeActivities.StartEvent(Source, "inner")!;
        try
        {
            Assert.False(inner.HasRemoteParent);
            Assert.Equal("svc:1:1", inner.GetTagItem(OtlpTraces.ParentEventIdKey));
        }
        finally
        {
            inner.Stop();
            outer.Stop();
        }
    }

    // ---- 3. parent Event id propagation ----------------------------------------------------------

    [Fact]
    public void the_parent_event_id_round_trips_through_a_dictionary_carrier()
    {
        var carrier = new Dictionary<string, string>();
        ParentEventPropagation.InjectParentEventId(carrier, "ledger:9:2");
        Assert.Equal("ledger:9:2", carrier["x-sentinel-parent-event-id"]);
        Assert.Equal("ledger:9:2", ParentEventPropagation.ExtractParentEventId(carrier));
    }

    [Fact]
    public void extraction_is_case_insensitive_and_prefers_the_dedicated_header()
    {
        var carrier = new Dictionary<string, string>
        {
            ["X-Sentinel-Parent-Event-Id"] = "direct:1:1",
            ["tracestate"] = "vendor=a,sentinel=fallback:1:1",
        };
        Assert.Equal("direct:1:1", ParentEventPropagation.ExtractParentEventId(carrier));
    }

    [Theory]
    [InlineData("sentinel=svc:3:4", "svc:3:4")]
    [InlineData("vendor=a, sentinel=svc:3:4 ,other=b", "svc:3:4")]
    [InlineData("vendor=a", null)]
    [InlineData("sentinel=", null)]
    [InlineData("notsentinel=x", null)]
    public void the_tracestate_member_is_the_fallback(string tracestate, string? expected)
    {
        var carrier = new Dictionary<string, string> { ["tracestate"] = tracestate };
        Assert.Equal(expected, ParentEventPropagation.ExtractParentEventId(carrier));
    }

    [Fact]
    public void a_blank_header_counts_as_absent_and_a_caller_that_is_not_a_probe_yields_null()
    {
        Assert.Null(ParentEventPropagation.ExtractParentEventId(new Dictionary<string, string> { ["x-sentinel-parent-event-id"] = "  " }));
        Assert.Null(ParentEventPropagation.ExtractParentEventId(new Dictionary<string, string> { ["traceparent"] = "00-a-b-01" }));
    }

    [Fact]
    public void injecting_an_empty_or_malformed_id_is_refused()
    {
        var carrier = new Dictionary<string, string>();
        Assert.Throws<ArgumentException>(() => ParentEventPropagation.InjectParentEventId(carrier, ""));
        Assert.Throws<ArgumentException>(() => ParentEventPropagation.InjectParentEventId(carrier, "a\r\nb"));
        Assert.Empty(carrier);
    }

    [Fact]
    public void http_headers_round_trip_and_replace()
    {
        using var request = new HttpRequestMessage();
        ParentEventPropagation.InjectParentEventId(request.Headers, "a:1:1");
        ParentEventPropagation.InjectParentEventId(request.Headers, "a:1:2");
        Assert.Equal("a:1:2", ParentEventPropagation.ExtractParentEventId(request.Headers));
        using var other = new HttpRequestMessage();
        other.Headers.TryAddWithoutValidation("tracestate", "sentinel=b:2:2");
        Assert.Equal("b:2:2", ParentEventPropagation.ExtractParentEventId(other.Headers));
    }

    // ---- 4. bounded async exporter ----------------------------------------------------------------

    [Fact]
    public async Task accepted_events_close_the_epoch_and_are_batched()
    {
        using var rig = new Rig(Handler.Status(HttpStatusCode.OK), maxBatch: 2);
        for (var i = 0; i < 5; i++) rig.Emit();
        await rig.Processor.ShutdownAsync();
        Assert.Equal((5UL, 5UL), (rig.Declaration.LastEmitted, rig.Declaration.CompleteThrough));
        Assert.Equal(5, rig.Processor.ExportedCount);
        Assert.Equal(0, rig.Processor.FailedCount);
        Assert.All(rig.Http.Requests, r => Assert.Equal("/v1/traces", r.Url));
    }

    [Fact]
    public async Task transport_errors_are_retried_then_succeed()
    {
        var handler = new Handler((_, n) => Task.FromResult(new HttpResponseMessage(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        }));
        using var rig = new Rig(handler);
        rig.Emit();
        await rig.Processor.ShutdownAsync();
        Assert.Equal(3, handler.Calls);
        Assert.Equal((1UL, 1UL), (rig.Declaration.LastEmitted, rig.Declaration.CompleteThrough));
        Assert.Empty(rig.Failures);
    }

    [Fact]
    public async Task transport_errors_are_failed_after_the_attempt_limit()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("down"));
        using var rig = new Rig(handler);
        rig.Emit();
        await rig.Processor.ShutdownAsync();
        Assert.Equal(3, handler.Calls);
        Assert.Equal(0UL, rig.Declaration.CompleteThrough);
        Assert.Equal(ExportFailureKind.Transport, rig.Failures.Single().Kind);
    }

    [Fact]
    public async Task a_deterministic_refusal_is_not_retried()
    {
        var handler = Handler.Status(HttpStatusCode.BadRequest, "span refused");
        using var rig = new Rig(handler);
        rig.Emit();
        await rig.Processor.ShutdownAsync();
        Assert.Equal(1, handler.Calls);
        var failure = rig.Failures.Single();
        Assert.Equal(ExportFailureKind.Refused, failure.Kind);
        Assert.Contains("span refused", failure.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task rejected_spans_in_a_2xx_response_fail_the_slots()
    {
        // ExportTraceServiceResponse{partial_success{rejected_spans=1}}
        var body = new byte[] { 0x0a, 0x02, 0x08, 0x01 };
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));
        using var rig = new Rig(handler);
        rig.Emit();
        await rig.Processor.ShutdownAsync();
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0UL, rig.Declaration.CompleteThrough);
        Assert.Equal(ExportFailureKind.Rejected, rig.Failures.Single().Kind);
    }

    // ExportTraceServiceResponse{partial_success{rejected_spans=1, error_message=text}}
    private static byte[] Refusal(string text)
    {
        var message = System.Text.Encoding.UTF8.GetBytes(text);
        var inner = new List<byte> { 0x08, 0x01, 0x12, (byte)message.Length };
        inner.AddRange(message);
        var body = new List<byte> { 0x0a, (byte)inner.Count };
        body.AddRange(inner);
        return body.ToArray();
    }

    [Theory]
    [InlineData("evidence_conflict")]
    [InlineData("identity_conflict")]
    public async Task a_refused_duplicate_event_id_keeps_the_slot_failed_and_is_not_retried(string kind)
    {
        var refusal = Refusal("append_events conflicted: " + kind);
        var handler = new Handler((_, n) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(n == 2 ? refusal : Array.Empty<byte>()),
        }));
        using var rig = new Rig(handler, maxBatch: 1);
        rig.Emit();
        rig.Emit(); // slot 2: the second delivery of an Event id Sentinel holds, refused
        rig.Emit();
        await rig.Processor.ShutdownAsync();
        Assert.Equal(3, handler.Calls); // the refusal is not retried
        var failure = rig.Failures.Single();
        Assert.Equal(ExportFailureKind.DuplicateEventId, failure.Kind);
        Assert.Equal(new[] { 2UL }, failure.Slots.Select(slot => slot.Number).ToArray());
        // Slots 1 and 3 exported, slot 2 FAILED: complete_through stops at 1.
        Assert.Equal((3UL, 1UL), (rig.Declaration.LastEmitted, rig.Declaration.CompleteThrough));
    }

    [Fact]
    public async Task slot_conflict_also_stays_failed_as_a_plain_rejection()
    {
        var refusal = Refusal("append_events conflicted: slot_conflict");
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(refusal) }));
        using var rig = new Rig(handler);
        rig.Emit();
        await rig.Processor.ShutdownAsync();
        Assert.Equal(1, handler.Calls);
        Assert.Equal(ExportFailureKind.Rejected, rig.Failures.Single().Kind);
        Assert.Equal(0UL, rig.Declaration.CompleteThrough);
    }

    [Fact]
    public async Task a_rejected_batch_is_resent_one_by_one_so_a_refusal_fails_only_its_event()
    {
        var refusal = Refusal("append_events conflicted: evidence_conflict");
        // Request 1 is the batch of three (rejected as a whole); then one request per Event.
        var handler = new Handler((_, n) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(n == 1 || n == 3 ? refusal : Array.Empty<byte>()),
        }));
        using var rig = new Rig(handler, maxBatch: 8);
        for (var i = 0; i < 3; i++) rig.Emit();
        await rig.Processor.ShutdownAsync();
        Assert.Equal(4, handler.Calls);
        Assert.Equal(ExportFailureKind.DuplicateEventId, rig.Failures.Single().Kind);
        Assert.Equal((3UL, 1UL), (rig.Declaration.LastEmitted, rig.Declaration.CompleteThrough));
    }

    [Fact]
    public async Task one_invalid_span_does_not_sink_its_batch()
    {
        using var rig = new Rig(Handler.Status(HttpStatusCode.OK), maxBatch: 8);
        rig.Emit();
        rig.Emit(Local()); // refused: local parent without an Event id
        rig.Emit();
        await rig.Processor.ShutdownAsync();
        Assert.Equal(2, rig.Processor.ExportedCount);
        Assert.Equal(1, rig.Processor.FailedCount);
        Assert.Equal(3UL, rig.Declaration.LastEmitted);
        Assert.Equal(1UL, rig.Declaration.CompleteThrough); // slot 2 failed: the boundary stays below it
    }

    [Fact]
    public async Task overflow_drops_the_newest_event_and_fails_its_slot()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var handler = new Handler(async (_, _) =>
        {
            entered.TrySetResult();
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        });
        using var rig = new Rig(handler, capacity: 1, maxBatch: 1);
        rig.Emit();                       // slot 1: taken by the worker and held in the transport
        await entered.Task;
        rig.Emit();                       // slot 2: fills the queue
        rig.Emit();                       // slot 3: queue full, dropped
        Assert.Equal(1, rig.Processor.DroppedCount);
        Assert.Equal(ExportFailureKind.QueueOverflow, rig.Failures.Single().Kind);
        gate.SetResult();
        await rig.Processor.ShutdownAsync();
        Assert.Equal(3UL, rig.Declaration.LastEmitted);
        Assert.Equal(2UL, rig.Declaration.CompleteThrough); // 1 and 2 exported, 3 dropped and FAILED
        Assert.Equal(2, rig.Processor.ExportedCount);
    }

    [Fact]
    public async Task events_after_shutdown_are_dropped_and_failed_not_thrown()
    {
        using var rig = new Rig(Handler.Status(HttpStatusCode.OK));
        await rig.Processor.ShutdownAsync();
        Assert.Null(Record.Exception(() => rig.Emit()));
        Assert.Equal(1, rig.Processor.DroppedCount);
        Assert.Equal(0UL, rig.Declaration.CompleteThrough);
    }

    [Fact]
    public async Task shutdown_flushes_every_queued_event_before_the_final_heartbeat()
    {
        var order = new List<string>();
        var release = new TaskCompletionSource();
        var handler = new Handler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/traces")
            {
                await release.Task;
                lock (order) order.Add("traces");
            }
            else
            {
                lock (order) order.Add("logs");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        });
        using var rig = new Rig(handler);
        using var exporter = new OtlpExporter("http://sentinel.invalid", new HttpClient(handler));
        using var heartbeat = new CompletenessHeartbeat(
            rig.Sequence,
            (signal, ct) => exporter.ExportCompletenessAsync("svc", signal, ct),
            interval: TimeSpan.Zero);
        rig.Emit();
        rig.Emit();
        var shutdown = rig.Processor.ShutdownAsync(heartbeat);
        await Task.Delay(50);
        Assert.False(shutdown.IsCompleted); // still waiting on the in-flight export
        release.SetResult();
        await shutdown;
        Assert.Equal("traces", order.First());
        Assert.Equal("logs", order.Last());
        Assert.DoesNotContain("logs", order.Take(order.Count - 1));
        Assert.Equal(2UL, rig.Declaration.CompleteThrough);
    }

    [Fact]
    public async Task the_event_filter_marks_a_dropped_event_filtered_and_sends_nothing()
    {
        var filters = new FilterStore(new Emet.Sentinel.Model.V1.EventFilter { Epoch = 3 });
        using var rig = new Rig(Handler.Status(HttpStatusCode.OK), filters: filters);
        rig.Emit();
        await rig.Processor.ShutdownAsync();
        Assert.Equal(1UL, rig.Declaration.LastEmitted);
        Assert.Equal(0UL, rig.Declaration.CompleteThrough); // filtered slots do not advance completeness
        Assert.Equal(0, rig.Http.Calls);
        Assert.Empty(rig.Failures);
    }

    // ---- 5. log receiver bind address ---------------------------------------------------------------

    [Fact]
    public async Task the_log_receiver_defaults_to_loopback()
    {
        using var receiver = OtlpLogReceiver.Start();
        Assert.Equal(IPAddress.Loopback, receiver.BoundAddress);
        Assert.StartsWith("http://127.0.0.1:", receiver.LogsUrl, StringComparison.Ordinal);
        using var http = new HttpClient();
        using var response = await http.PostAsync(receiver.LogsUrl, Garbage());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task the_log_receiver_binds_a_wildcard_address_and_a_fixed_port()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        using var receiver = OtlpLogReceiver.Start("0.0.0.0", port);
        Assert.Equal(IPAddress.Any, receiver.BoundAddress);
        Assert.Equal(port, receiver.Port);
        using var http = new HttpClient();
        using var response = await http.PostAsync("http://127.0.0.1:" + port + "/v1/logs", Garbage());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void the_log_receiver_refuses_a_bad_bind()
    {
        Assert.Throws<ArgumentException>(() => OtlpLogReceiver.Start(" ", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OtlpLogReceiver.Start(IPAddress.Loopback, 70000));
    }

    // ---- 6. resource attributes and error bodies -----------------------------------------------------

    [Fact]
    public async Task resource_attributes_ride_on_the_resource_and_never_on_the_span()
    {
        var resource = new Dictionary<string, string>
        {
            ["service.name"] = "sagashop-ledger",
            ["sentinel.source.id"] = "ledger",
            ["sentinel.source.tier"] = "anchor",
            ["sentinel.tenant"] = "sagashop",
            ["sentinel.data_class"] = "monitored-tenant",
        };
        using var rig = new Rig(Handler.Status(HttpStatusCode.OK), resource: resource);
        rig.Emit();
        await rig.Processor.ShutdownAsync();
        var body = rig.Http.Requests.Single().Body;
        var decoded = OtlpTraces.DecodeResourceAttributes(body).ToDictionary(a => a.Key, a => a.Value.String);
        Assert.Equal(resource, decoded);
        var span = Assert.Single(OtlpTraces.Decode(body));
        var allowed = new HashSet<string>
        {
            "sentinel.event.id", "sentinel.parent.event.id", "sentinel.sequence.epoch", "sentinel.sequence.number",
            "sentinel.schema.version", "sentinel.acknowledged_filter_epoch", "sentinel.claimed_sensitivity", "sentinel.claimed_capabilities",
        };
        Assert.All(span.Attributes.Where(a => a.Key.StartsWith("sentinel.", StringComparison.Ordinal)), a => Assert.Contains(a.Key, allowed));
        Assert.DoesNotContain(span.Attributes, a => a.Key == "service.name");
        Assert.Equal(1, SpanCount(body));
    }

    [Fact]
    public void an_export_without_resource_attributes_has_no_resource()
    {
        var a = ProbeActivities.StartEvent(Source, "x")!;
        a.Stop();
        var bytes = OtlpTraces.Encode(new[] { Export(a) });
        Assert.Empty(OtlpTraces.DecodeResourceAttributes(bytes));
    }

    [Fact]
    public void invalid_resource_attributes_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new OtlpExporter("http://x", null, new Dictionary<string, string> { [""] = "v" }));
    }

    [Fact]
    public async Task the_http_response_body_is_in_the_export_error()
    {
        using var exporter = new OtlpExporter("http://sentinel.invalid", new HttpClient(Handler.Status(HttpStatusCode.BadRequest, "unknown sentinel attribute sentinel.tenant")));
        var a = ProbeActivities.StartEvent(Source, "x")!;
        a.Stop();
        var e = await Assert.ThrowsAsync<OtlpExportException>(() =>
            exporter.ExportSpansCheckedAsync("svc", new[] { Export(a) }, CancellationToken.None));
        Assert.Equal(400, e.StatusCode);
        Assert.Equal("unknown sentinel attribute sentinel.tenant", e.ResponseBody);
        Assert.Contains("unknown sentinel attribute sentinel.tenant", e.Message, StringComparison.Ordinal);
        Assert.False(e.IsTransient);
    }

    [Fact]
    public async Task a_very_long_response_body_is_truncated()
    {
        using var exporter = new OtlpExporter("http://sentinel.invalid", new HttpClient(Handler.Status(HttpStatusCode.BadGateway, new string('x', 10_000))));
        var a = ProbeActivities.StartEvent(Source, "x")!;
        a.Stop();
        var e = await Assert.ThrowsAsync<OtlpExportException>(() =>
            exporter.ExportSpansCheckedAsync("svc", new[] { Export(a) }, CancellationToken.None));
        Assert.True(e.IsTransient);
        Assert.True(e.ResponseBody!.Length <= OtlpExporter.MaxErrorBodyChars + 3);
    }

    private static ByteArrayContent Garbage()
    {
        var content = new ByteArrayContent(new byte[] { 0xff });
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        return content;
    }

    private static SpanExport Export(Activity a) => new()
    {
        Activity = a,
        EventId = "svc:1:1",
        SequenceEpoch = 1,
        SequenceNumber = 1,
        SchemaVersion = "v1",
        AcknowledgedFilterEpoch = 0,
        ClaimedSensitivity = Emet.Sentinel.Model.V1.Sensitivity.Internal,
    };

    internal static int SpanCount(byte[] body) => OtlpTraces.Decode(body).Count;
}
