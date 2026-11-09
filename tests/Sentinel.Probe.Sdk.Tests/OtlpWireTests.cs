using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Google.Protobuf;
using Sentinel.Probe.Sdk.Emission;
using Sentinel.Probe.Sdk.Enforcement;
using Sentinel.Probe.Sdk.Int128;
using Sentinel.Probe.Sdk.Otlp;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

public class OtlpWireTests
{
    private static readonly DateTime EndTime = ActivityBuilder.StartTime.AddMilliseconds(5);

    [Fact]
    public void GoldenChargeAndShipCarryTheContractAttributes()
    {
        var spans = OtlpTraces.Decode(Golden("charge-ship.bin"));
        Assert.Equal(2, spans.Count);

        var charge = spans[0].ToProducerEvent();
        Assert.Equal("sagashop.payment.charge.completed", charge.Kind);
        Assert.Equal("01f0c0ffee-charge", charge.Id);
        Assert.Equal(7UL, charge.Sequence!.Epoch);
        Assert.Equal(1UL, charge.Sequence.Sequence);
        Assert.Equal("sagashop-contract-v1", charge.SchemaVersion);
        Assert.Equal(4UL, charge.AcknowledgedFilterEpoch);
        Assert.Equal(Sensitivity.SensitivityInternal, charge.ClaimedSensitivity);
        Assert.Empty(charge.CausalPredecessorIds);
        Assert.False(spans[0].HasParentSpanId);
        Assert.Equal("success", StringAttr(charge, "sagashop.payment.status"));
        Assert.Equal("order-1001", StringAttr(charge, "sagashop.order.id"));

        var ship = spans[1].ToProducerEvent();
        Assert.Equal("sagashop.order.ship.requested", ship.Kind);
        Assert.Equal("01f0c0ffee-ship", ship.Id);
        Assert.Equal(2UL, ship.Sequence!.Sequence);
        Assert.Equal(new[] { "01f0c0ffee-charge" }, ship.CausalPredecessorIds);
        Assert.True(spans[1].HasParentSpanId);
        Assert.Equal("order-1001", StringAttr(ship, "sagashop.order.id"));
        Assert.DoesNotContain(ship.CausalPredecessorIds, id => id.Contains("2222222222222222", StringComparison.Ordinal));
    }

    [Fact]
    public void GoldenCompletenessIsTheProducerSignal()
    {
        var records = OtlpLogs.Decode(Golden("completeness-order.bin"));
        var record = Assert.Single(records);
        Assert.Equal(OtlpLogs.CompletenessType, Text(record, OtlpLogs.PayloadTypeKey));
        Assert.Equal("sagashop-order", Text(record, OtlpLogs.SourceHandleKey));
        Assert.Null(Text(record, OtlpLogs.RequestIdKey));
        Assert.True(record.BodyIsBytes);
        var signal = ProducerCompletenessSignal.ParseFrom(record.BodyBytes!);
        Assert.Equal(7UL, signal.SequenceEpoch);
        Assert.Equal(2UL, signal.LastEmitted);
        Assert.Equal(2UL, signal.CompleteThrough);
        Assert.Equal(4UL, signal.AcknowledgedFilterEpoch);
        Assert.Equal(SourceActivityState.SourceActivityStateActive, signal.Activity);
        Assert.Equal("unix", signal.ProducerReportedAt!.ClockDomainId);
        Assert.Equal(0UL, signal.ProducerReportedAt.UncertaintyNanoseconds);
        Assert.Equal(new System.Numerics.BigInteger(1_700_000_000_000_000_000), Int128Codec.ToBigInteger(signal.ProducerReportedAt.Nanoseconds));
    }

    [Fact]
    public async Task ReceiverDecodesVerdictFilterAndDecisionGoldens()
    {
        using var receiver = OtlpLogReceiver.Start();
        Assert.Equal(200, receiver.Ingest(Golden("verdict.bin")));
        var verdict = Assert.Single(receiver.Verdicts);
        Assert.False(string.IsNullOrEmpty(verdict.Id));
        Assert.NotEqual(ReceivedVerdictOutcome.Unspecified, verdict.Outcome);

        Assert.Equal(200, receiver.Ingest(Golden("event-filter.bin")));
        Assert.Single(receiver.Filters);

        Assert.Equal(200, receiver.Ingest(Golden("decide-response.bin")));
        var decision = await receiver.WaitForDecisionAsync("req-charge-1", CancellationToken.None);
        Assert.Equal("req-charge-1", decision.RequestId);
        Assert.Equal(DecisionAction.DecisionActionDeny, decision.Action);
        Assert.Equal(DecisionAction.DecisionActionPermit, Assert.Single(decision.Specifications).Action);
    }

    [Fact]
    public void UnspecifiedDecisionActionIsAnError()
    {
        using var receiver = OtlpLogReceiver.Start();
        var response = new DecideResponse
        {
            RequestId = "req-1",
            Action = DecisionAction.DecisionActionUnspecified,
        };
        var bytes = OtlpLogs.EncodeRecord(OtlpLogs.DecideResponseType, response.ToByteArray(), "req-1", null);
        Assert.Equal(400, receiver.Ingest(bytes));
        Assert.Empty(receiver.Verdicts);

        response.Action = DecisionAction.DecisionActionPermit;
        response.Specifications.Add(new SpecificationDecision { Action = DecisionAction.DecisionActionUnspecified });
        bytes = OtlpLogs.EncodeRecord(OtlpLogs.DecideResponseType, response.ToByteArray(), "req-1", null);
        Assert.Equal(400, receiver.Ingest(bytes));
    }

    [Fact]
    public async Task ReceiverAcceptsGzipIdentityOnLoopback()
    {
        using var receiver = OtlpLogReceiver.Start();
        var golden = Golden("verdict.bin");
        using var gzipped = new MemoryStream();
        using (var gzip = new GZipStream(gzipped, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(golden, 0, golden.Length);
        }
        using var http = new HttpClient();
        using var content = new ByteArrayContent(gzipped.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        content.Headers.ContentEncoding.Add("gzip");
        var response = await http.PostAsync(receiver.LogsUrl, content);
        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.Single(receiver.Verdicts);
    }

    [Fact]
    public void TwoEndedSpansAreSeparateRepeatedFields()
    {
        var charge = ActivityBuilder.Build(
            name: "sagashop.payment.charge.completed",
            tags: new[] { new KeyValuePair<string, object?>("sagashop.order.id", "order-1001") },
            startTime: ActivityBuilder.StartTime,
            endTime: EndTime);
        var ship = ActivityBuilder.Build(
            name: "sagashop.order.ship.requested",
            tags: new[]
            {
                new KeyValuePair<string, object?>("sagashop.order.id", "order-1001"),
                new KeyValuePair<string, object?>(ReservedAttributes.AttributeParentEventID, "01f0c0ffee-charge"),
            },
            startTime: ActivityBuilder.StartTime,
            endTime: EndTime);
        var bytes = OtlpTraces.Encode(new[]
        {
            SampleExport(charge, "01f0c0ffee-charge"),
            SampleExport(ship, "01f0c0ffee-ship"),
        });
        var spans = OtlpTraces.Decode(bytes);
        Assert.Equal(2, spans.Count);
        Assert.Equal("01f0c0ffee-charge", spans[0].ToProducerEvent().Id);
        Assert.Equal("sagashop.payment.charge.completed", spans[0].Name);
        Assert.Equal("01f0c0ffee-ship", spans[1].ToProducerEvent().Id);
        Assert.Equal("sagashop.order.ship.requested", spans[1].Name);
        Assert.Equal(new[] { "01f0c0ffee-charge" }, spans[1].ToProducerEvent().CausalPredecessorIds);
    }

    [Fact]
    public void SpanToEventMissingPredecessorFailsTheExport()
    {
        var activity = ActivityBuilder.Build(
            name: "sagashop.order.ship.requested",
            parentSpanHex: "1112131415161718",
            links: new[] { ("2122232425262728", (IEnumerable<KeyValuePair<string, object?>>?)null) },
            startTime: ActivityBuilder.StartTime,
            endTime: EndTime);
        var conversion = new SpanConversion { Activity = activity, EventId = "01f0c0ffee-ship" };
        var converted = Assert.Throws<OtlpExportException>(() => SpanToEventConverter.SpanToEvent(conversion));
        var exported = Assert.Throws<OtlpExportException>(() => OtlpTraces.Encode(new[] { SampleExport(activity, "01f0c0ffee-ship") }));
        Assert.DoesNotContain("1112131415161718", converted.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2122232425262728", converted.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1112131415161718", exported.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2122232425262728", exported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPredecessorIdFailsTheExport()
    {
        var activity = ActivityBuilder.Build(
            name: "sagashop.order.ship.requested",
            parentSpanHex: "1112131415161718",
            links: new[] { ("2122232425262728", (IEnumerable<KeyValuePair<string, object?>>?)null) },
            startTime: ActivityBuilder.StartTime,
            endTime: EndTime);
        var export = SampleExport(activity, "01f0c0ffee-ship");
        var error = Assert.Throws<OtlpExportException>(() => OtlpTraces.Encode(new[] { export }));
        Assert.DoesNotContain("1112131415161718", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2122232425262728", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EndedSpanAgreesWithSpanToEvent()
    {
        var activity = ActivityBuilder.Build(
            name: "sagashop.order.ship.requested",
            tags: new[]
            {
                new KeyValuePair<string, object?>("sagashop.order.id", "order-1001"),
                new KeyValuePair<string, object?>(ReservedAttributes.AttributeParentEventID, "01f0c0ffee-charge"),
            },
            parentSpanHex: "1112131415161718",
            startTime: ActivityBuilder.StartTime,
            endTime: EndTime);
        var export = SampleExport(activity, "01f0c0ffee-ship");
        var expected = SpanToEventConverter.SpanToEvent(new SpanConversion
        {
            Activity = activity,
            EventId = export.EventId,
            Sequence = new SequenceCoordinate { Epoch = export.SequenceEpoch, Sequence = export.SequenceNumber },
            SchemaVersion = export.SchemaVersion,
            AcknowledgedEpoch = export.AcknowledgedFilterEpoch,
            ClaimedSensitivity = export.ClaimedSensitivity,
        });
        var decoded = Assert.Single(OtlpTraces.Decode(OtlpTraces.Encode(new[] { export }))).ToProducerEvent();
        AssertSame(expected, decoded);
        Assert.Equal(new[] { "01f0c0ffee-charge" }, decoded.CausalPredecessorIds);
    }

    [Fact]
    public void RandomAttributesMatchSpanToEventOrAreRejected()
    {
        var random = new Random(407);
        for (var i = 0; i < 48; i++)
        {
            var tags = new List<KeyValuePair<string, object?>>();
            var count = random.Next(0, 6);
            for (var k = 0; k < count; k++)
            {
                tags.Add(new KeyValuePair<string, object?>("field-" + k.ToString(System.Globalization.CultureInfo.InvariantCulture), RandomValue(random)));
            }
            string? parentHex = random.Next(5) == 0 ? "1112131415161718" : null;
            string? parentEvent = null;
            if (random.Next(3) == 0)
            {
                parentEvent = "pred-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                tags.Add(new KeyValuePair<string, object?>(ReservedAttributes.AttributeParentEventID, parentEvent));
            }
            var links = new List<(string SpanHex, IEnumerable<KeyValuePair<string, object?>>? Tags)>();
            var linkMissing = false;
            if (random.Next(4) == 0)
            {
                links.Add(("2122232425262728", new[]
                {
                    new KeyValuePair<string, object?>(ReservedAttributes.AttributeEventId, "link-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                }));
            }
            else if (random.Next(6) == 0)
            {
                linkMissing = true;
                links.Add(("2122232425262728", new[] { new KeyValuePair<string, object?>("other", "x") }));
            }
            var unknownSentinel = random.Next(10) == 0;
            if (unknownSentinel)
            {
                tags.Add(new KeyValuePair<string, object?>("sentinel.extra", "nope"));
            }
            var nonFinite = random.Next(10) == 0;
            if (nonFinite)
            {
                tags.Add(new KeyValuePair<string, object?>("ratio", double.NaN));
            }

            var activity = ActivityBuilder.Build(
                name: "kind-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                tags: tags,
                parentSpanHex: parentHex,
                links: links.Count == 0 ? null : links,
                startTime: ActivityBuilder.StartTime,
                endTime: EndTime);
            var export = new SpanExport
            {
                Activity = activity,
                EventId = "evt-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SequenceEpoch = 7,
                SequenceNumber = (ulong)(i + 1),
                SchemaVersion = "sagashop-contract-v1",
                AcknowledgedFilterEpoch = 4,
                ClaimedSensitivity = Sensitivity.SensitivityInternal,
            };
            var reject = (parentHex != null && parentEvent == null) || linkMissing || unknownSentinel || nonFinite;
            if (reject)
            {
                var error = Assert.Throws<OtlpExportException>(() => OtlpTraces.Encode(new[] { export }));
                Assert.DoesNotContain("1112131415161718", error.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("2122232425262728", error.Message, StringComparison.Ordinal);
                continue;
            }

            var expected = SpanToEventConverter.SpanToEvent(new SpanConversion
            {
                Activity = activity,
                EventId = export.EventId,
                Sequence = new SequenceCoordinate { Epoch = 7, Sequence = (ulong)(i + 1) },
                SchemaVersion = export.SchemaVersion,
                AcknowledgedEpoch = 4,
                ClaimedSensitivity = Sensitivity.SensitivityInternal,
            });
            var decoded = Assert.Single(OtlpTraces.Decode(OtlpTraces.Encode(new[] { export }))).ToProducerEvent();
            AssertSame(expected, decoded);
        }
    }

    [Fact]
    public async Task ExporterPostsTracesAndCompletenessWithTheSourceHandle()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var seen = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = Task.Run(() => CaptureTwo(listener, seen));
        using var exporter = new OtlpExporter("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var activity = ActivityBuilder.Build(
            name: "sagashop.payment.charge.completed",
            tags: new[] { new KeyValuePair<string, object?>("sagashop.payment.status", "success") },
            startTime: ActivityBuilder.StartTime,
            endTime: EndTime);
        await exporter.ExportSpansAsync("sagashop-payment", new[] { SampleExport(activity, "01f0c0ffee-charge") }, CancellationToken.None);
        await exporter.ExportCompletenessAsync("sagashop-payment", SampleSignal(1), CancellationToken.None);
        var headers = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(headers, line => line.Contains("POST /v1/traces ", StringComparison.Ordinal));
        Assert.Contains(headers, line => line.Contains("POST /v1/logs ", StringComparison.Ordinal));
        Assert.Equal(2, headers.FindAll(line => line.Contains("x-sentinel-source-handle: sagashop-payment", StringComparison.OrdinalIgnoreCase)).Count);
        Assert.Contains(headers, line => line.Contains("application/x-protobuf", StringComparison.OrdinalIgnoreCase));
        listener.Stop();
        await serve;
    }

    [Fact]
    public async Task GateUsesTheLogAndDoesNotCallConnect()
    {
        using var receiver = OtlpLogReceiver.Start();
        var response = new DecideResponse
        {
            RequestId = "req-1",
            Action = DecisionAction.DecisionActionPermit,
        };
        Assert.Equal(200, receiver.Ingest(OtlpLogs.EncodeRecord(OtlpLogs.DecideResponseType, response.ToByteArray(), "req-1", null)));
        var outcome = await Gate.GateAsync(
            TestSupport.MakeEvent(TestSupport.TestKind),
            TestSupport.MakeFilter(4UL, TestSupport.AskAndBlockSpec()),
            10_000L,
            new Deps
            {
                Decide = Gate.FromLogReceiver(receiver),
                NowMonotonicNs = () => 0,
                AcceptedFailModeFor = TestSupport.AlwaysOpen,
            },
            TestSupport.TestOptions,
            CancellationToken.None);
        Assert.Equal(OutcomeKind.Permit, outcome.Kind);
        Assert.Equal("permit", outcome.Reason);
    }

    private static void CaptureTwo(TcpListener listener, TaskCompletionSource<List<string>> seen)
    {
        var headers = new List<string>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();
                var text = ReadHttp(stream);
                headers.Add(text);
                var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                stream.Write(ok, 0, ok.Length);
            }
            seen.TrySetResult(headers);
        }
        catch (Exception ex)
        {
            seen.TrySetException(ex);
        }
    }

    private static string ReadHttp(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        while (buffer.Length < 1024 * 1024)
        {
            var next = stream.ReadByte();
            if (next < 0)
            {
                break;
            }
            buffer.WriteByte((byte)next);
            if (buffer.Length >= 4)
            {
                var raw = buffer.GetBuffer();
                var count = (int)buffer.Length;
                if (raw[count - 4] == (byte)'\r' && raw[count - 3] == (byte)'\n' && raw[count - 2] == (byte)'\r' && raw[count - 1] == (byte)'\n')
                {
                    var length = ContentLength(Encoding.ASCII.GetString(buffer.ToArray()));
                    var body = new byte[length];
                    var read = 0;
                    while (read < length)
                    {
                        var n = stream.Read(body, read, length - read);
                        if (n <= 0)
                        {
                            break;
                        }
                        read += n;
                    }
                    break;
                }
            }
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private static int ContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(line.Split(':', 2)[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return 0;
    }

    private static SpanExport SampleExport(Activity activity, string eventId) => new()
    {
        Activity = activity,
        EventId = eventId,
        SequenceEpoch = 7,
        SequenceNumber = 1,
        SchemaVersion = "sagashop-contract-v1",
        AcknowledgedFilterEpoch = 4,
        ClaimedSensitivity = Sensitivity.SensitivityInternal,
    };

    internal static ProducerCompletenessSignal SampleSignal(ulong last) 
    {
        var when = new OccurrenceTime
        {
            ClockDomainId = "unix",
            Nanoseconds = Int128Codec.FromInt64(1_700_000_000_000_000_000),
            UncertaintyNanoseconds = 0,
        };
        return new ProducerCompletenessSignal
        {
            SequenceEpoch = 7,
            LastEmitted = last,
            CompleteThrough = last,
            ProducerReportedAt = when,
            AcknowledgedFilterEpoch = 1,
            Activity = SourceActivityState.SourceActivityStateActive,
            ActivitySince = when,
        };
    }

    private static object RandomValue(Random random) => random.Next(4) switch
    {
        0 => "v" + random.Next(100).ToString(System.Globalization.CultureInfo.InvariantCulture),
        1 => random.Next(2) == 0,
        2 => random.Next(-20, 20),
        _ => random.NextDouble() * 10,
    };

    private static void AssertSame(ProducerEvent expected, ProducerEvent actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.AcknowledgedFilterEpoch, actual.AcknowledgedFilterEpoch);
        Assert.Equal(expected.Sequence?.Epoch, actual.Sequence?.Epoch);
        Assert.Equal(expected.Sequence?.Sequence, actual.Sequence?.Sequence);
        Assert.Equal(expected.ClaimedSensitivity, actual.ClaimedSensitivity);
        Assert.Equal(expected.ClaimedCapabilities, actual.ClaimedCapabilities);
        Assert.Equal(expected.CausalPredecessorIds, actual.CausalPredecessorIds);
        Assert.Equal(expected.Attributes.Count, actual.Attributes.Count);
        for (var i = 0; i < expected.Attributes.Count; i++)
        {
            Assert.Equal(expected.Attributes[i].Key, actual.Attributes[i].Key);
            AssertValue(expected.Attributes[i].Value, actual.Attributes[i].Value);
        }
        Assert.Equal("unix", actual.OccurrenceTime?.ClockDomainId);
        Assert.Equal(0UL, actual.OccurrenceTime?.UncertaintyNanoseconds ?? 1);
        Assert.Equal(Int128Codec.ToBigInteger(expected.OccurrenceTime?.Nanoseconds), Int128Codec.ToBigInteger(actual.OccurrenceTime?.Nanoseconds));
    }

    private static void AssertValue(AttributeValue? expected, AttributeValue? actual)
    {
        Assert.Equal(expected?.Case, actual?.Case);
        if (expected == null || actual == null)
        {
            return;
        }
        Assert.Equal(expected.StringValue, actual.StringValue);
        Assert.Equal(expected.BoolValue, actual.BoolValue);
        Assert.Equal(expected.IntegerValue, actual.IntegerValue);
        Assert.Equal(expected.DoubleValue, actual.DoubleValue);
        Assert.Equal(expected.BytesValue, actual.BytesValue);
    }

    private static string? StringAttr(ProducerEvent @event, string key)
    {
        foreach (var attribute in @event.Attributes)
        {
            if (attribute.Key == key)
            {
                return attribute.Value?.StringValue;
            }
        }
        return null;
    }

    private static string? Text(DecodedLog record, string key)
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

    internal static byte[] Golden(string name)
    {
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
        throw new FileNotFoundException(name);
    }
}
