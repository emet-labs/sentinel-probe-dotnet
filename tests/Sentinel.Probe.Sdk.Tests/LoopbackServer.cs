using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Emet.Sentinel.Probe.V1;
using Sentinel.Probe.Sdk.Connect;

namespace Sentinel.Probe.Sdk.Tests;

// A scripted Sentinel decision endpoint stood up on a loopback TcpListener — the .NET analog of
// the Go connect_loopback_test's httptest server. Exercises the real proto wire encoding, the
// hand-written Connect transport, and Connect's error mapping, all with no network beyond
// loopback (ADR-0019: hermetic recorded evidence).
internal sealed class LoopbackServer : IDisposable
{
    public DecisionAction Action { get; }
    public Exception? Error { get; }

    private readonly TcpListener _listener;
    private readonly Thread _thread;
    private readonly int _port;
    private readonly List<DecideRequest> _requests = new();
    private readonly object _lock = new();
    private volatile bool _stop;

    public string BaseURL => $"http://127.0.0.1:{_port}";

    public LoopbackServer(DecisionAction action, Exception? error = null)
    {
        Action = action;
        Error = error;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _thread = new Thread(Serve) { IsBackground = true };
        _thread.Start();
    }

    public IReadOnlyList<DecideRequest> Requests => GetRequests();

    private IReadOnlyList<DecideRequest> GetRequests() { lock (_lock) return new List<DecideRequest>(_requests); }

    public int RequestCount { get { lock (_lock) return _requests.Count; } }

    private void Serve()
    {
        while (!_stop)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { break; }
            try { Handle(client); }
            catch { /* a malformed request is ignored; the test asserts on what arrived */ }
            finally { client.Dispose(); }
        }
    }

    private void Handle(TcpClient client)
    {
        using var stream = client.GetStream();
        client.ReceiveTimeout = 2000;
        byte[] headerBytes = ReadHeaders(stream);
        string headers = Encoding.ASCII.GetString(headerBytes);
        int contentLength = ParseContentLength(headers);

        byte[] body = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = stream.Read(body, read, contentLength - read);
            if (n <= 0) break;
            read += n;
        }

        var request = DecideRequest.ParseFrom(body);
        lock (_lock) { _requests.Add(request); }

        if (Error is ConnectException ce)
        {
            string json = "{\"code\":\"" + ConnectException.WireName(ce.Code) + "\",\"message\":" + JsonString(ce.Message) + "}";
            WriteResponse(stream, ConnectException.HttpStatus(ce.Code), "application/json", Encoding.UTF8.GetBytes(json));
        }
        else
        {
            var response = new DecideResponse { RequestId = request.RequestId, Action = Action };
            WriteResponse(stream, 200, "application/proto", response.ToByteArray());
        }
    }

    private static byte[] ReadHeaders(NetworkStream stream)
    {
        using var ms = new MemoryStream();
        byte[] buf = new byte[1];
        // Read until the header terminator \r\n\r\n.
        while (true)
        {
            int n = stream.Read(buf, 0, 1);
            if (n <= 0) break;
            ms.Write(buf, 0, 1);
            byte[] data = ms.ToArray();
            if (data.Length >= 4 &&
                data[^4] == (byte)'\r' && data[^3] == (byte)'\n' &&
                data[^2] == (byte)'\r' && data[^1] == (byte)'\n')
            {
                break;
            }
        }
        return ms.ToArray();
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var line in headers.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string name = line[..colon].Trim();
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(line[(colon + 1)..].Trim());
            }
        }
        return 0;
    }

    private static void WriteResponse(Stream stream, int status, string contentType, byte[] body)
    {
        string reason = status == 200 ? "OK" : "Error";
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        stream.Write(headBytes, 0, headBytes.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static string JsonString(string s)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch { }
    }
}
