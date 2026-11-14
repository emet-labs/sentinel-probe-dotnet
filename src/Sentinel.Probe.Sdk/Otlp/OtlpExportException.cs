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
    /// (connection refused, reset, timeout) or Sentinel answered 408, 429 or 5xx. Validation,
    /// encoding and other HTTP 4xx refusals are deterministic and not transient.
    /// </summary>
    public bool IsTransient { get; init; }

    /// <summary>The HTTP status Sentinel answered, or null when no response was received.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The (truncated) HTTP response body of a non-2xx answer, or null.</summary>
    public string? ResponseBody { get; init; }
}
