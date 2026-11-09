using System;
using System.Diagnostics;
using Emet.Sentinel.Model.V1;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Sentinel.Probe.Sdk.Emission;

// ProbeTracer — the .NET analog of sdk/go/emission/tracer.go (mirrors otel-bridge.ts).
//
// .NET OTel is built on System.Diagnostics.Activity: the "tracer" is an ActivitySource and the
// provider enables sources and attaches span processors. The Probe wire is OTLP/HTTP
// (the ADR-0002 exception in #398): OtlpExporter posts ended spans to Sentinel and does not
// leave that choice to the host. Connect is not the wire. The caller owns the returned
// tracer's lifecycle and must Dispose it. Resource merge behaviour is unchanged.

/// <summary>Configures the Probe's tracer. Analog of EmissionOptions.</summary>
public sealed class ProbeTracerOptions
{
    /// <summary>Identifies the instrumentation scope, e.g. the Probe's package name.</summary>
    public string TracerName { get; set; } = "";

    /// <summary>
    /// Populates the resource's service.name, MERGED over ResourceBuilder.CreateDefault() so
    /// telemetry.sdk.* and anything the operator set through OTEL_RESOURCE_ATTRIBUTES survive.
    /// Empty means no resource option is added, leaving the default in place.
    /// </summary>
    public string ServiceName { get; set; } = "";

    /// <summary>
    /// Applied to the provider builder before it is built, letting a host attach span processors,
    /// samplers, or its own resource. (The Go reference clones a caller-owned options slice; .NET's
    /// builder is already a value the host hands over, so no clone is needed.)
    /// </summary>
    public Action<TracerProviderBuilder>? ConfigureProvider { get; set; }
}

/// <summary>Bundles the ActivitySource a Probe emits with, its provider, and the conversion.</summary>
public sealed class ProbeTracer : IDisposable
{
    public ActivitySource Source { get; }
    public TracerProvider? Provider { get; }

    private bool _disposed;

    internal ProbeTracer(ActivitySource source, TracerProvider? provider)
    {
        Source = source;
        Provider = provider;
    }

    /// <summary>Builds a ProbeTracer and a tracer for a Probe.</summary>
    public static ProbeTracer NewProbeTracer(ProbeTracerOptions options)
    {
        TracerProviderBuilder builder = OpenTelemetry.Sdk.CreateTracerProviderBuilder();

        if (!string.IsNullOrEmpty(options.ServiceName))
        {
            // MERGE over the default rather than replacing it: AddService merges service.name onto
            // CreateDefault(), preserving telemetry.sdk.* and operator-set attributes.
            builder = builder.SetResourceBuilder(
                ResourceBuilder.CreateDefault().AddService(options.ServiceName));
        }

        options.ConfigureProvider?.Invoke(builder);

        builder.AddSource(options.TracerName);
        TracerProvider provider = builder.Build();
        var source = new ActivitySource(options.TracerName);
        return new ProbeTracer(source, provider);
    }

    /// <summary>Converts an ended span into a ProducerEvent. Convenience wrapper over SpanToEvent.</summary>
    public ProducerEvent ToEvent(SpanConversion conversion) => SpanToEventConverter.SpanToEvent(conversion);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Provider?.Dispose();
        Source.Dispose();
    }
}
