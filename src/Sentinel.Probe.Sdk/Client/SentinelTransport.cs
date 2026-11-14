using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Probe.V1;
using Google.Protobuf;
using Sentinel.Probe.Sdk.Connect;

namespace Sentinel.Probe.Sdk.Client;

// Hand-written Connect unary transport over HttpClient. It stays compiled so the proto
// codec tests still have a client, but it is not the Probe wire. #398 carries decisions
// back as OTLP logs; Gate.FromLogReceiver waits for those logs and does not call Decide.

/// <summary>The outcome of a Decide RPC: exactly one of Response / Error is non-null.</summary>
public readonly record struct DecideResult(DecideResponse? Response, Exception? Error);

/// <summary>The decision-service client the Probe talks to.</summary>
public interface ISentinelDecisionServiceClient
{
    Task<DecideResult> DecideAsync(DecideRequest request, CancellationToken cancellationToken);
}

/// <summary>Configures the hand-written Connect transport. Analog of TransportOptions.</summary>
public sealed class SentinelTransportOptions
{
    /// <summary>Base URL of the Sentinel decision endpoint, e.g. "http://sentinel.local:7070".</summary>
    public string BaseURL { get; set; } = "";

    /// <summary>
    /// The HttpClient to issue requests with. Defaults to a shared singleton when null. Hosts
    /// override it to install timeouts, connection pools, or auth handlers.
    /// </summary>
    public HttpClient? HttpClient { get; set; }
}

/// <summary>
/// Builds a Connect client for SentinelDecisionService. HTTP/2 is deliberately NOT forced here for
/// the same reason the Go reference does not force it: the decision endpoint does not exist yet and
/// a host should not be bound to an untested transport assumption. Whoever lands the endpoint (#22)
/// decides; hosts can supply their own HttpClient.
/// </summary>
public sealed class SentinelTransport : ISentinelDecisionServiceClient
{
    // The Connect unary path: /{fully.qualified.Service}/{Rpc}.
    private const string DecidePath = "/sentinel.probe.v1.SentinelDecisionService/Decide";

    private static readonly HttpClient DefaultClient = new HttpClient();

    private readonly HttpClient _http;
    private readonly string _url;

    public SentinelTransport(SentinelTransportOptions options)
    {
        _http = options.HttpClient ?? DefaultClient;
        // Join base URL and the RPC path with exactly one slash between them.
        string base_ = (options.BaseURL ?? string.Empty).TrimEnd('/');
        _url = base_ + DecidePath;
    }

    public async Task<DecideResult> DecideAsync(DecideRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var content = new ByteArrayContent(request.ToByteArray());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");

            using var req = new HttpRequestMessage(HttpMethod.Post, _url) { Content = content };
            response = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is a context error; let the gate classify it.
        }
        catch (Exception ex)
        {
            return new DecideResult(null, ex);
        }

        using (response)
        {
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return new DecideResult(DecideResponse.Parser.ParseFrom(body), null);
                }
                catch (Exception ex)
                {
                    return new DecideResult(null, ex);
                }
            }

            return new DecideResult(null, ParseConnectError(response, body));
        }
    }

    private static ConnectException ParseConnectError(HttpResponseMessage response, byte[] body)
    {
        string code = "unknown";
        string message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

        string? contentType = null;
        if (response.Content.Headers.ContentType != null)
        {
            contentType = response.Content.Headers.ContentType.MediaType;
        }

        if (contentType != null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                {
                    code = codeEl.GetString() ?? "unknown";
                }
                if (doc.RootElement.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                {
                    string? m = msgEl.GetString();
                    if (!string.IsNullOrEmpty(m)) message = m;
                }
            }
            catch
            {
                // A malformed error envelope is still an error; fall back to the HTTP status line.
            }
        }

        return new ConnectException(ConnectException.Parse(code), message);
    }
}
