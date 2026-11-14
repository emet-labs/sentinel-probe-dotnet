using System;

namespace Sentinel.Probe.Sdk.Otlp;

/// <summary>
/// The span or log cannot be sent on the OTLP/HTTP probe wire.
/// A missing causal id is this failure; it is not rewritten as a span id.
/// </summary>
public sealed class OtlpExportException : Exception
{
    public OtlpExportException(string message) : base(message)
    {
    }

    public OtlpExportException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>
    /// The failure is a transport problem that a resend can cure: the request never completed
    /// (connection refused, reset, timeout) or Sentinel answered 408, 425, 429, 500, 502, 503 or
    /// 504 (the Probe SDK retry policy, <c>sdk/README.md</c>). Validation, encoding and every other
    /// HTTP status are deterministic and not transient.
    /// </summary>
    public bool IsTransient { get; init; }

    /// <summary>
    /// The server's <c>Retry-After</c> on a 429 or 503, or null when absent or on any other status.
    /// The exporting processor waits at least this long before the next attempt, never longer than
    /// <c>RetryMaxDelay</c>.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Whether the retry policy retries an HTTP answer with this status.</summary>
    public static bool IsRetryableStatus(int status) =>
        status is 408 or 425 or 429 or 500 or 502 or 503 or 504;

    /// <summary>The HTTP status Sentinel answered, or null when no response was received.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The (truncated) HTTP response body of a non-2xx answer, or null.</summary>
    public string? ResponseBody { get; init; }
}
