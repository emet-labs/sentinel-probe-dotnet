using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Otlp;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

public class PartialSuccessTests
{
    [Fact]
    public void AnEmptyBodyRejectsNothing()
    {
        Assert.Equal(0L, PartialSuccess.RejectedSpans(Array.Empty<byte>()));
    }

    [Fact]
    public void RejectedSpansAreReadFromPartialSuccess()
    {
        Assert.Equal(2L, PartialSuccess.RejectedSpans(Convert.FromHexString("0a020802")));
    }

    [Fact]
    public void ADuplicateMessageWithZeroRejectedSpansRejectsNothing()
    {
        var message = Encoding.UTF8.GetBytes("append_events classified 1 deliveries as duplicate");
        var inner = new List<byte> { 0x12, (byte)message.Length };
        inner.AddRange(message);
        var body = new List<byte> { 0x0a, (byte)inner.Count };
        body.AddRange(inner);
        Assert.Equal(0L, PartialSuccess.RejectedSpans(body.ToArray()));
    }

    [Fact]
    public void ATruncatedBodyIsUndecodable()
    {
        Assert.Throws<FormatException>(() => PartialSuccess.RejectedSpans(Convert.FromHexString("0a0508")));
    }

    [Fact]
    public void ANegativeRejectedCountIsUndecodable()
    {
        // rejected_spans = -1: a ten-byte two's-complement varint.
        Assert.Throws<FormatException>(() => PartialSuccess.RejectedSpans(Convert.FromHexString("0a0b08ffffffffffffffffff01")));
    }

    [Fact]
    public void UnknownFieldsAreSkipped()
    {
        Assert.Equal(3L, PartialSuccess.RejectedSpans(Convert.FromHexString("15000000000a0508031a0100")));
    }

    [Fact]
    public async Task ExportSpansCheckedReturnsTheRejectedCountFromA200()
    {
        Assert.Equal(1L, await ExportAgainst(200, Convert.FromHexString("0a020801")));
        Assert.Equal(0L, await ExportAgainst(200, Array.Empty<byte>()));
    }

    [Fact]
    public async Task ExportSpansCheckedFailsOnANon2xxStatusOrAnUndecodable200()
    {
        await Assert.ThrowsAsync<OtlpExportException>(() => ExportAgainst(500, Encoding.ASCII.GetBytes("boom")));
        await Assert.ThrowsAsync<OtlpExportException>(() => ExportAgainst(200, Convert.FromHexString("0a0508")));
    }

    private static async Task<long> ExportAgainst(int status, byte[] response)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(() => Answer(listener, status, response));
        using var exporter = new OtlpExporter("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var activity = ActivityBuilder.Build(
            name: "sagashop.payment.charge.completed",
            startTime: ActivityBuilder.StartTime,
            endTime: ActivityBuilder.StartTime.AddMilliseconds(5));
        var export = new SpanExport
        {
            Activity = activity,
            EventId = "01f0c0ffee-charge",
            SequenceEpoch = 7,
            SequenceNumber = 1,
            SchemaVersion = "sagashop-contract-v1",
            AcknowledgedFilterEpoch = 4,
            ClaimedSensitivity = Sensitivity.Internal,
        };
        try
        {
            return await exporter.ExportSpansCheckedAsync("sagashop-payment", new[] { export }, CancellationToken.None);
        }
        finally
        {
            await serve;
        }
    }

    internal static void Answer(TcpListener listener, int status, byte[] response)
    {
        using var client = listener.AcceptTcpClient();
        using var stream = client.GetStream();
        ReadRequest(stream);
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + " X\r\nContent-Length: "
            + response.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
        stream.Write(head, 0, head.Length);
        stream.Write(response, 0, response.Length);
    }

    private static void ReadRequest(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            var next = stream.ReadByte();
            if (next < 0)
            {
                return;
            }
            buffer.WriteByte((byte)next);
            var bytes = buffer.GetBuffer();
            var length = (int)buffer.Length;
            if (length >= 4 && bytes[length - 4] == '\r' && bytes[length - 3] == '\n' && bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
            {
                break;
            }
        }
        var headers = Encoding.ASCII.GetString(buffer.ToArray());
        var contentLength = 0;
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        var body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = stream.Read(body, read, contentLength - read);
            if (n <= 0)
            {
                break;
            }
            read += n;
        }
    }
}
