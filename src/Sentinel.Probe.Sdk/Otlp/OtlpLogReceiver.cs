using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Google.Protobuf;

namespace Sentinel.Probe.Sdk.Otlp;

/// <summary>
/// Serves <c>POST /v1/logs</c> on a configurable address (default <c>127.0.0.1</c>, random port;
/// bind <c>0.0.0.0</c> and a fixed port so a containerised Probe can receive Event Filters). Accepts identity and gzip
/// <c>application/x-protobuf</c> and decodes Verdict, EventFilter, and DecideResponse.
/// An unspecified decision action is an error and is not delivered to the gate.
/// </summary>
public sealed class OtlpLogReceiver : IDisposable
{
    private const int GzipLimit = 8 * 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly Thread _thread;
    private readonly int _port;
    private readonly object _gate = new();
    private readonly List<Verdict> _verdicts = new();
    private readonly List<EventFilter> _filters = new();
    private readonly Dictionary<string, DecideResponse> _decisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DecisionWaiter> _waiters = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private volatile bool _stop;
    private bool _closed; // guarded by _gate

    /// <summary>
    /// A URL that reaches this receiver from the same host: the bound address, or loopback when the
    /// receiver listens on a wildcard address. A peer in another container needs its own name for it.
    /// </summary>
    public string LogsUrl
    {
        get
        {
            var host = BoundAddress;
            if (host.Equals(IPAddress.Any) || host.Equals(IPAddress.IPv6Any))
            {
                host = host.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            }
            var text = host.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + host + "]" : host.ToString();
            return "http://" + text + ":" + _port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/v1/logs";
        }
    }

    /// <summary>The address the receiver is bound to.</summary>
    public IPAddress BoundAddress { get; }

    public int Port => _port;

    private OtlpLogReceiver(TcpListener listener, IPAddress address, int port, TimeProvider timeProvider)
    {
        _listener = listener;
        BoundAddress = address;
        _port = port;
        _timeProvider = timeProvider;
        _thread = new Thread(Serve) { IsBackground = true, Name = "sentinel-otlp-logs" };
        _thread.Start();
    }

    /// <summary>Starts a receiver on loopback and a random port, timing decision waits on the system clock.</summary>
    public static OtlpLogReceiver Start() => Start(IPAddress.Loopback, 0, TimeProvider.System);

    /// <summary>
    /// Starts a receiver on loopback and a random port whose decision waits are timed by
    /// <paramref name="timeProvider"/>. A test passes a manual provider to expire a wait without a
    /// wall clock.
    /// </summary>
    public static OtlpLogReceiver Start(TimeProvider timeProvider) => Start(IPAddress.Loopback, 0, timeProvider);

    /// <summary>
    /// Starts a receiver on <paramref name="host"/> (an IP literal such as <c>0.0.0.0</c> or
    /// <c>::</c>, or a name that resolves to one) and <paramref name="port"/> (0 picks a free port).
    /// </summary>
    public static OtlpLogReceiver Start(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Bind host is required.", nameof(host));
        }
        if (IPAddress.TryParse(host, out var address))
        {
            return Start(address, port);
        }
        var resolved = Dns.GetHostAddresses(host);
        if (resolved.Length == 0)
        {
            throw new ArgumentException("Bind host " + host + " did not resolve.", nameof(host));
        }
        return Start(resolved[0], port);
    }

    /// <summary>Starts a receiver bound to <paramref name="address"/> and <paramref name="port"/>.</summary>
    public static OtlpLogReceiver Start(IPAddress address, int port) => Start(address, port, TimeProvider.System);

    /// <summary>
    /// Starts a receiver bound to <paramref name="address"/> and <paramref name="port"/> (0 picks a
    /// free port) whose decision waits are timed by <paramref name="timeProvider"/>. Every other
    /// <c>Start</c> overload calls this one.
    /// </summary>
    public static OtlpLogReceiver Start(IPAddress address, int port, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (port < 0 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be 0..65535.");
        }
        var listener = new TcpListener(address, port);
        listener.Start();
        var bound = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new OtlpLogReceiver(listener, address, bound, timeProvider);
    }

    /// <summary>
    /// Every Verdict received so far, decoded in full by the generated parser (#466). Each has a
    /// non-empty id, an outcome of Satisfy, Violate or Inconclusive, and an authority of
    /// Provisional or Authoritative; anything else was refused.
    /// </summary>
    public IReadOnlyList<Verdict> Verdicts
    {
        get
        {
            lock (_gate)
            {
                return new List<Verdict>(_verdicts);
            }
        }
    }

    public IReadOnlyList<EventFilter> Filters
    {
        get
        {
            lock (_gate)
            {
                return new List<EventFilter>(_filters);
            }
        }
    }

    /// <summary>
    /// Waits for the DecideResponse whose request id is <paramref name="requestId"/>, for at most
    /// <paramref name="budgetNs"/>, the request's remaining Transport Budget.
    /// <list type="bullet">
    /// <item>An answer that already arrived is returned at once and consumed, whatever the budget.</item>
    /// <item>Otherwise a null budget, or one that floors to zero 100 ns ticks, cannot be waited: the
    /// task fails at once and no timer is armed. Missing evidence is never a pass.</item>
    /// <item>Otherwise the waiter is registered first and a timer is armed for the budget, floored to
    /// whole 100 ns ticks so the wait never outlives the budget. On expiry the task fails with a
    /// <see cref="TimeoutException"/>. A budget beyond <c>uint.MaxValue - 1</c> milliseconds (about
    /// 49.7 days, the <see cref="Timer"/> limit) is shortened to that limit.</item>
    /// </list>
    /// The timer and the cancellation registration are released on every exit: answer, expiry,
    /// cancellation and <see cref="Dispose"/>. An answer that arrives after its wait expired is
    /// kept, and a later wait for the same request id returns it. A closed receiver fails a new
    /// wait at once. One waiter per request id: a second concurrent wait throws (#535).
    /// </summary>
    public Task<DecideResponse> WaitForDecisionAsync(string requestId, ulong? budgetNs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new OtlpExportException("DecideResponse request_id is empty");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new DecisionWaiter();
        TimeSpan due;
        lock (_gate)
        {
            if (_decisions.Remove(requestId, out var ready))
            {
                return Task.FromResult(ready);
            }
            if (_closed)
            {
                return Task.FromException<DecideResponse>(new OtlpExportException("log receiver stopped"));
            }
            due = budgetNs.HasValue ? Due(budgetNs.GetValueOrDefault()) : TimeSpan.Zero;
            if (due == TimeSpan.Zero)
            {
                return Task.FromException<DecideResponse>(new OtlpExportException(
                    "no DecideResponse for " + requestId + " and no Transport Budget left to wait for one"));
            }
            if (_waiters.ContainsKey(requestId))
            {
                throw new OtlpExportException("a decision wait is already registered for " + requestId);
            }
            // Registered before the timer is armed, so an answer or an expiry that lands while
            // arming finds the waiter.
            _waiters.Add(requestId, waiter);
        }
        ITimer timer;
        try
        {
            timer = _timeProvider.CreateTimer(
                _ => Release(requestId, waiter, w => w.Fail(new TimeoutException("timeout"))),
                null,
                due,
                Timeout.InfiniteTimeSpan);
        }
        catch (Exception error)
        {
            // A wait that cannot be armed must not stay registered: it would never expire, and
            // it would refuse every later wait for this request id.
            Release(requestId, waiter, w => w.Fail(error));
            return waiter.Task;
        }
        waiter.Attach(timer);
        if (cancellationToken.CanBeCanceled)
        {
            waiter.Attach(cancellationToken.Register(
                () => Release(requestId, waiter, w => w.Cancel(cancellationToken))));
        }
        return waiter.Task;
    }

    /// <summary>
    /// The timer due time for a remaining budget: floored to whole 100 ns ticks, so the wait never
    /// outlives the budget, and capped at <c>uint.MaxValue - 1</c> milliseconds, the most a
    /// <see cref="Timer"/> accepts.
    /// </summary>
    internal static TimeSpan Due(ulong budgetNs)
    {
        const ulong maxTicks = (uint.MaxValue - 1UL) * TimeSpan.TicksPerMillisecond;
        ulong ticks = budgetNs / (1_000_000_000UL / TimeSpan.TicksPerSecond);
        return TimeSpan.FromTicks((long)Math.Min(ticks, maxTicks));
    }

    // Removes the waiter when it is still the one registered for the request id, then completes it.
    // A waiter already released by an answer, an expiry, a cancellation or Dispose is left alone.
    private void Release(string requestId, DecisionWaiter waiter, Action<DecisionWaiter> complete)
    {
        lock (_gate)
        {
            if (!_waiters.TryGetValue(requestId, out var current) || !ReferenceEquals(current, waiter))
            {
                return;
            }
            _waiters.Remove(requestId);
        }
        complete(waiter);
    }

    /// <summary>Decodes one uncompressed export. Returns the HTTP status the socket would send.</summary>
    public int Ingest(byte[] body)
    {
        try
        {
            Apply(OtlpLogs.Decode(body));
            return 200;
        }
        catch (OtlpExportException)
        {
            return 400;
        }
        catch (InvalidProtocolBufferException)
        {
            return 400;
        }
        catch (InvalidDataException)
        {
            return 400;
        }
    }

    /// <summary>
    /// Stops the receiver. Every pending decision wait fails, its timer is released, and a later
    /// wait fails at once unless its answer already arrived.
    /// </summary>
    public void Dispose()
    {
        _stop = true;
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // The accept loop is already gone.
        }
        List<DecisionWaiter> pending;
        lock (_gate)
        {
            _closed = true;
            pending = new List<DecisionWaiter>(_waiters.Values);
            _waiters.Clear();
        }
        foreach (var waiter in pending)
        {
            waiter.Fail(new OtlpExportException("log receiver stopped"));
        }
    }

    private void Apply(IReadOnlyList<DecodedLog> records)
    {
        var verdicts = new List<Verdict>();
        var filters = new List<EventFilter>();
        var decisions = new List<DecideResponse>();
        foreach (var record in records)
        {
            if (record.DroppedAttributesCount != 0)
            {
                throw new OtlpExportException("log record dropped attributes");
            }
            var payload = Attribute(record, OtlpLogs.PayloadTypeKey) ?? throw new OtlpExportException("log record is missing sentinel.payload.type");
            RejectForeignAttributes(record, payload);
            if (!record.BodyIsBytes || record.BodyBytes == null)
            {
                throw new OtlpExportException("log body is not protobuf bytes");
            }
            switch (payload)
            {
                case OtlpLogs.VerdictType:
                    verdicts.Add(ReadVerdict(record.BodyBytes));
                    break;
                case OtlpLogs.EventFilterType:
                    filters.Add(EventFilter.Parser.ParseFrom(record.BodyBytes));
                    break;
                case OtlpLogs.DecideResponseType:
                    decisions.Add(ReadDecision(record));
                    break;
                default:
                    throw new OtlpExportException("unsupported sentinel.payload.type " + payload);
            }
        }
        var answered = new List<(DecisionWaiter Waiter, DecideResponse Decision)>();
        lock (_gate)
        {
            foreach (var decision in decisions)
            {
                if (!_waiters.ContainsKey(decision.RequestId) && _decisions.ContainsKey(decision.RequestId))
                {
                    throw new OtlpExportException("duplicate DecideResponse " + decision.RequestId);
                }
            }
            _verdicts.AddRange(verdicts);
            _filters.AddRange(filters);
            foreach (var decision in decisions)
            {
                if (_waiters.Remove(decision.RequestId, out var waiter))
                {
                    answered.Add((waiter, decision));
                }
                else
                {
                    _decisions.Add(decision.RequestId, decision);
                }
            }
        }
        foreach (var (waiter, decision) in answered)
        {
            waiter.Answer(decision);
        }
    }

    // One pending decision wait: its task, the expiry timer and the cancellation registration.
    // Every completion releases both before completing the task. A timer or registration attached
    // after the wait already completed (an answer delivered while arming) is released at once.
    private sealed class DecisionWaiter
    {
        private readonly object _lock = new();
        private readonly TaskCompletionSource<DecideResponse> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ITimer? _timer;
        private CancellationTokenRegistration _registration;
        private bool _released;

        public Task<DecideResponse> Task => _source.Task;

        public void Attach(ITimer timer)
        {
            lock (_lock)
            {
                if (!_released)
                {
                    _timer = timer;
                    return;
                }
            }
            timer.Dispose();
        }

        public void Attach(CancellationTokenRegistration registration)
        {
            lock (_lock)
            {
                if (!_released)
                {
                    _registration = registration;
                    return;
                }
            }
            registration.Unregister();
        }

        public void Answer(DecideResponse decision)
        {
            ReleaseResources();
            _source.TrySetResult(decision);
        }

        public void Fail(Exception error)
        {
            ReleaseResources();
            _source.TrySetException(error);
        }

        public void Cancel(CancellationToken cancellationToken)
        {
            ReleaseResources();
            _source.TrySetCanceled(cancellationToken);
        }

        private void ReleaseResources()
        {
            ITimer? timer;
            CancellationTokenRegistration registration;
            lock (_lock)
            {
                _released = true;
                timer = _timer;
                registration = _registration;
                _timer = null;
                _registration = default;
            }
            timer?.Dispose();
            // Unregister, not Dispose: Dispose blocks until a running cancellation callback ends,
            // and that callback may be the caller.
            registration.Unregister();
        }
    }

    private static void RejectForeignAttributes(DecodedLog record, string payload)
    {
        foreach (var attribute in record.Attributes)
        {
            if (!attribute.Key.StartsWith("sentinel.", StringComparison.Ordinal))
            {
                if (attribute.Value.Case == OtlpValue.Shape.Double && !double.IsFinite(attribute.Value.Double))
                {
                    throw new OtlpExportException("non-finite double");
                }
                continue;
            }
            if (attribute.Key == OtlpLogs.PayloadTypeKey)
            {
                continue;
            }
            if (payload == OtlpLogs.DecideResponseType && attribute.Key == OtlpLogs.RequestIdKey)
            {
                continue;
            }
            throw new OtlpExportException("unexpected sentinel attribute " + attribute.Key);
        }
    }

    private static DecideResponse ReadDecision(DecodedLog record)
    {
        var response = DecideResponse.Parser.ParseFrom(record.BodyBytes ?? Array.Empty<byte>());
        var echoed = Attribute(record, OtlpLogs.RequestIdKey);
        if (string.IsNullOrWhiteSpace(response.RequestId) || echoed != response.RequestId)
        {
            throw new OtlpExportException("sentinel.request_id does not match DecideResponse.request_id");
        }
        RequireAction(response.Action, "DecideResponse action");
        foreach (var specification in response.Specifications)
        {
            RequireAction(specification.Action, "specification decision action");
        }
        return response;
    }

    private static void RequireAction(DecisionAction action, string what)
    {
        if (action is not (DecisionAction.Permit or DecisionAction.Deny or DecisionAction.Defer))
        {
            throw new OtlpExportException(what + " is unspecified");
        }
    }

    // The full Verdict, not just its id and outcome. Malformed bytes throw
    // InvalidProtocolBufferException, which Ingest and the socket path turn into a 400.
    private static Verdict ReadVerdict(byte[] body)
    {
        var verdict = Verdict.Parser.ParseFrom(body);
        if (verdict.Id.Length == 0)
        {
            throw new OtlpExportException("Verdict id is empty");
        }
        // Unspecified, or an outcome this SDK does not know, is never a pass.
        if (verdict.Outcome is not (VerdictOutcome.Satisfy or VerdictOutcome.Violate or VerdictOutcome.Inconclusive))
        {
            throw new OtlpExportException("Verdict outcome is unspecified");
        }
        // Fail closed on authority too, as the Verdict surface does (#371): an Unspecified or
        // unknown authority cannot say whether this Verdict is settled.
        if (verdict.Authority is not (VerdictAuthority.Provisional or VerdictAuthority.Authoritative))
        {
            throw new OtlpExportException("Verdict authority is unspecified");
        }
        return verdict;
    }

    private static string? Attribute(DecodedLog record, string key)
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

    private void Serve()
    {
        while (!_stop)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
            try
            {
                Handle(client);
            }
            catch (IOException)
            {
                // The peer closed the socket.
            }
            finally
            {
                client.Dispose();
            }
        }
    }

    private void Handle(TcpClient client)
    {
        using var stream = client.GetStream();
        client.ReceiveTimeout = 5000;
        var headerBytes = ReadHeaders(stream);
        var headers = Encoding.ASCII.GetString(headerBytes);
        var status = Route(headers, stream);
        var reason = status switch
        {
            200 => "OK",
            404 => "Not Found",
            411 => "Length Required",
            415 => "Unsupported Media Type",
            _ => "Bad Request",
        };
        var response = "HTTP/1.1 " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + reason
            + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);
        stream.Write(bytes, 0, bytes.Length);
    }

    private int Route(string headers, NetworkStream stream)
    {
        var first = headers.Split("\r\n", 2)[0];
        var parts = first.Split(' ');
        if (parts.Length < 2 || !string.Equals(parts[0], "POST", StringComparison.Ordinal) || parts[1] != "/v1/logs")
        {
            return 404;
        }
        if (!MediaTypeIsProtobuf(headers))
        {
            return 415;
        }
        var encoding = Header(headers, "content-encoding");
        if (encoding != null && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase) && !encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
        {
            return 415;
        }
        if (!int.TryParse(Header(headers, "content-length"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length) || length < 0)
        {
            return 411;
        }
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = stream.Read(body, read, length - read);
            if (n <= 0)
            {
                return 400;
            }
            read += n;
        }
        if (encoding != null && encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                body = Inflate(body);
            }
            catch (InvalidDataException)
            {
                return 400;
            }
            catch (OtlpExportException)
            {
                return 400;
            }
        }
        return Ingest(body);
    }

    private static byte[] Inflate(byte[] body)
    {
        using var input = new MemoryStream(body);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var n = gzip.Read(buffer, 0, buffer.Length);
            if (n == 0)
            {
                break;
            }
            if (output.Length + n > GzipLimit)
            {
                throw new OtlpExportException("gzip inflate exceeded the uncompressed limit");
            }
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }

    private static bool MediaTypeIsProtobuf(string headers)
    {
        var value = Header(headers, "content-type");
        if (value == null)
        {
            return false;
        }
        var essence = value.Split(';', 2)[0].Trim();
        return essence.Equals(OtlpExporter.ProtobufMediaType, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Header(string headers, string name)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            var split = line.Split(':', 2);
            if (split.Length == 2 && split[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return split[1].Trim();
            }
        }
        return null;
    }

    private static byte[] ReadHeaders(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        while (buffer.Length < 64 * 1024)
        {
            var next = stream.ReadByte();
            if (next < 0)
            {
                break;
            }
            buffer.WriteByte((byte)next);
            if (buffer.Length < 4)
            {
                continue;
            }
            var raw = buffer.GetBuffer();
            var count = (int)buffer.Length;
            if (raw[count - 4] == (byte)'\r' && raw[count - 3] == (byte)'\n' && raw[count - 2] == (byte)'\r' && raw[count - 1] == (byte)'\n')
            {
                break;
            }
        }
        return buffer.ToArray();
    }
}
