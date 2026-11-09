using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Otlp;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

public class OtlpRoundTripTests
{
    private static readonly DateTime Start = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).UtcDateTime;
    private static readonly DateTime End = Start.AddMilliseconds(5);

    [Fact]
    public async Task ChargeThenLinkedShipObservesAVerdict()
    {
        var bin = PeripheryBin();
        if (bin == null)
        {
            return;
        }
        await using var session = await Periphery.Start(bin);
        await session.CoverAsync();
        await session.ExportAsync(
            "sagashop-payment",
            "sagashop.payment.charge.completed",
            "01f0c0ffee-charge",
            parent: null,
            new KeyValuePair<string, object?>("sagashop.payment.status", "success"),
            new KeyValuePair<string, object?>("sagashop.order.id", "order-1001"));
        Assert.Empty(session.Receiver.Verdicts);
        await session.ExportAsync(
            "sagashop-order",
            "sagashop.order.ship.requested",
            "01f0c0ffee-ship",
            parent: "01f0c0ffee-charge",
            new KeyValuePair<string, object?>("sagashop.order.id", "order-1001"));
        var verdicts = await session.WaitForVerdictsAsync();
        Assert.Contains(verdicts, verdict => verdict.Id.Contains("sagashop.p4.ship-requires-prior-charge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShipWithoutChargeIsNotSatisfy()
    {
        var bin = PeripheryBin();
        if (bin == null)
        {
            return;
        }
        await using var session = await Periphery.Start(bin);
        await session.CoverAsync();
        await session.ExportAsync(
            "sagashop-payment",
            "sagashop.payment.charge.completed",
            "01f0c0ffee-declin",
            parent: null,
            new KeyValuePair<string, object?>("sagashop.payment.status", "declined"),
            new KeyValuePair<string, object?>("sagashop.order.id", "order-2002"));
        await session.ExportAsync(
            "sagashop-order",
            "sagashop.order.ship.requested",
            "01f0c0ffee-noship",
            parent: null,
            new KeyValuePair<string, object?>("sagashop.order.id", "order-2002"));
        var verdicts = await session.WaitForVerdictsAsync();
        Assert.DoesNotContain(verdicts, verdict => verdict.Outcome == ReceivedVerdictOutcome.Satisfy);
    }

    private static string? PeripheryBin()
    {
        var bin = Environment.GetEnvironmentVariable("SENTINEL_PERIPHERY_BIN");
        if (string.IsNullOrEmpty(bin))
        {
            return null;
        }
        Assert.True(File.Exists(bin), bin);
        return bin;
    }

    private sealed class Periphery : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _baseUrl;
        private readonly StringBuilder _log = new();
        public OtlpLogReceiver Receiver { get; }
        public OtlpExporter Exporter { get; }

        private Periphery(Process process, OtlpLogReceiver receiver, OtlpExporter exporter, string baseUrl)
        {
            _process = process;
            Receiver = receiver;
            Exporter = exporter;
            _baseUrl = baseUrl;
        }

        public static async Task<Periphery> Start(string bin)
        {
            var receiver = OtlpLogReceiver.Start();
            var port = FreePort();
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(bin)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.StartInfo.Environment["SENTINEL_STORE"] = "memory";
            process.StartInfo.Environment["SENTINEL_LISTEN"] = "127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            process.StartInfo.Environment["SENTINEL_PROBE_LOG_URL"] = receiver.LogsUrl;
            var baseUrl = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var session = new Periphery(process, receiver, new OtlpExporter(baseUrl), baseUrl);
            process.OutputDataReceived += (_, e) => session.Append(e.Data);
            process.ErrorDataReceived += (_, e) => session.Append(e.Data);
            if (!process.Start())
            {
                throw new InvalidOperationException("sentinel-periphery did not start");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await session.WaitUntilAsync(() => session.Log().Contains("listening", StringComparison.Ordinal), "periphery did not listen");
            return session;
        }

        public async Task CoverAsync()
        {
            await Exporter.ExportCompletenessAsync("sagashop-order", OtlpWireTests.SampleSignal(1), CancellationToken.None);
            await Exporter.ExportCompletenessAsync("sagashop-payment", OtlpWireTests.SampleSignal(1), CancellationToken.None);
            using var http = new HttpClient();
            var body = new StringContent(
                "{\"observed_at\":{\"sentinel_clock_id\":\"sentinel\",\"nanoseconds\":1700000000000000000}}",
                Encoding.UTF8,
                "application/json");
            var response = await http.PostAsync(_baseUrl + "/v1/filters/bootstrap-cat1", body);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text + "\n" + Log());
        }

        public Task ExportAsync(string handle, string name, string eventId, string? parent, params KeyValuePair<string, object?>[] tags)
        {
            var list = new List<KeyValuePair<string, object?>>(tags);
            if (parent != null)
            {
                list.Add(new KeyValuePair<string, object?>("sentinel.parent.event.id", parent));
            }
            var activity = ActivityBuilder.Build(name, list, startTime: OtlpRoundTripTests.Start, endTime: OtlpRoundTripTests.End);
            return Exporter.ExportSpansAsync(handle, new[]
            {
                new SpanExport
                {
                    Activity = activity,
                    EventId = eventId,
                    SequenceEpoch = 7,
                    SequenceNumber = 1,
                    SchemaVersion = "sagashop-contract-v1",
                    AcknowledgedFilterEpoch = 1,
                    ClaimedSensitivity = Sensitivity.SensitivityInternal,
                },
            }, CancellationToken.None);
        }

        public async Task<IReadOnlyList<ReceivedVerdict>> WaitForVerdictsAsync()
        {
            await WaitUntilAsync(() => Receiver.Verdicts.Count > 0, "no Verdict log arrived");
            return Receiver.Verdicts;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
            catch (InvalidOperationException)
            {
                // The process was already gone.
            }
            Exporter.Dispose();
            Receiver.Dispose();
            _process.Dispose();
        }

        private async Task WaitUntilAsync(Func<bool> ready, string failure)
        {
            for (var i = 0; i < 200; i++)
            {
                if (ready())
                {
                    return;
                }
                if (_process.HasExited)
                {
                    break;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException(failure + "\n" + Log());
        }

        private void Append(string? line)
        {
            if (line == null)
            {
                return;
            }
            lock (_log)
            {
                _log.AppendLine(line);
            }
        }

        private string Log()
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
