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
}
