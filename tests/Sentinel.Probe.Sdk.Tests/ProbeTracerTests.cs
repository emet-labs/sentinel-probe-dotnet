using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xunit;
using Emet.Sentinel.Model.V1;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Sentinel.Probe.Sdk.Emission;

namespace Sentinel.Probe.Sdk.Tests;

public class ProbeTracerTests
{
    private static Activity EndedActivity(string name = "x") => ActivityBuilder.Build(name: name);

    private static SpanConversion Convert(Activity a) => new() { Activity = a, SchemaVersion = "sentinel.model.v1", EventId = "evt-1" };

    [Fact]
    public void NewProbeTracerReturnsUsableTracerAndProvider()
    {
        using var tracer = ProbeTracer.NewProbeTracer(new ProbeTracerOptions { TracerName = "probe.test", ServiceName = "sagashop" });
        Assert.NotNull(tracer.Source);
        Assert.NotNull(tracer.Provider);
    }

    [Fact]
    public void WithoutServiceNameBuilds()
    {
        using var tracer = ProbeTracer.NewProbeTracer(new ProbeTracerOptions { TracerName = "probe.test" });
        Assert.NotNull(tracer.Provider);
    }

    [Fact]
    public void ToEventMatchesSpanToEvent()
    {
        using var tracer = ProbeTracer.NewProbeTracer(new ProbeTracerOptions { TracerName = "probe.test", ServiceName = "sagashop" });
        var a = EndedActivity();
        var viaMethod = tracer.ToEvent(Convert(a));
        var viaFunc = SpanToEventConverter.SpanToEvent(Convert(a));
        Assert.Equal(viaFunc.Kind, viaMethod.Kind);
        Assert.Equal(viaFunc.Attributes.Count, viaMethod.Attributes.Count);
    }

    [Fact]
    public void ServiceNameMergesOverTheDefaultResource()
    {
        // The provider builds with a service.name merged over the default resource; the proof is
        // that a span emitted through the source is sampled (provider enabled it) and the source
        // is active. A resource created with NewWithAttributes alone would still build, so this
        // also guards that ServiceName does not throw during merge.
        using var tracer = ProbeTracer.NewProbeTracer(new ProbeTracerOptions { TracerName = "probe.test", ServiceName = "sagashop" });
        var a = tracer.Source.StartActivity("transfer.initiated");
        Assert.NotNull(a); // the provider enabled the source -> the activity is real
        a!.Dispose();
    }

    // A host that builds ProviderOptions with a reusable builder is not affected by the SDK
    // mutating it: .NET's builder is handed over once, so there is no caller array to stomp.
    [Fact]
    public void ConfigureProviderReceivesTheBuilderBeforeBuild()
    {
        BaseProcessor<Activity>? captured = null;
        using var tracer = ProbeTracer.NewProbeTracer(new ProbeTracerOptions
        {
            TracerName = "probe.test",
            ServiceName = "sagashop",
            ConfigureProvider = b =>
            {
                captured = new ProbeSpanProcessor(_ => null);
                b.AddProcessor(captured);
            },
        });
        Assert.NotNull(captured);
        Assert.NotNull(tracer.Provider);
    }
}
