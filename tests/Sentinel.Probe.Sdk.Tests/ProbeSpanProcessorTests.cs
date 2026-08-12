using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xunit;
using Emet.Sentinel.Model.V1;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Sentinel.Probe.Sdk.Emission;

namespace Sentinel.Probe.Sdk.Tests;

public class ProbeSpanProcessorTests
{
    private static SpanConversion Convert(Activity a, string eventId = "evt-1") =>
        new() { Activity = a, SchemaVersion = "sentinel.model.v1", EventId = eventId };

    [Fact]
    public void OnEndConvertsAndForwardsTheEvent()
    {
        var activity = ActivityBuilder.Build(name: "transfer.initiated", tags: new[] { KVP("k", "v") });
        ProducerEvent? received = null;
        var proc = new ProbeSpanProcessor(
            a => SpanToEventConverter.SpanToEvent(Convert(a)),
            e => received = e);
        proc.OnEnd(activity);
        Assert.NotNull(received);
        Assert.Equal("transfer.initiated", received!.Kind);
        Assert.Single(received.Attributes);
    }

    [Fact]
    public void NullConversionIsDropped()
    {
        bool called = false;
        var proc = new ProbeSpanProcessor(_ => null, _ => called = true);
        proc.OnEnd(ActivityBuilder.Build());
        Assert.False(called);
    }

    [Fact]
    public void NullOnEventCallbackIsSafe()
    {
        var proc = new ProbeSpanProcessor(a => SpanToEventConverter.SpanToEvent(Convert(a)));
        proc.OnEnd(ActivityBuilder.Build()); // must not throw
    }

    // The processor fires when an activity ends through the real SDK pipeline.
    [Fact]
    public void WiredIntoProviderFiresOnActivityEnd()
    {
        var received = new List<ProducerEvent>();
        using var tracer = ProbeTracer.NewProbeTracer(new ProbeTracerOptions
        {
            TracerName = "probe.test",
            ServiceName = "sagashop",
            ConfigureProvider = b => b.AddProcessor(new ProbeSpanProcessor(
                a => SpanToEventConverter.SpanToEvent(Convert(a)),
                e => received.Add(e))),
        });

        var activity = tracer.Source.StartActivity("transfer.initiated");
        Assert.NotNull(activity);
        activity!.SetTag("sagashop.order.id", "ord-9");
        activity.Dispose(); // OnEnd fires on dispose/end

        Assert.NotEmpty(received);
        Assert.Equal("transfer.initiated", received[0].Kind);
        Assert.Equal("ord-9", received[0].Attributes.Single(a => a.Key == "sagashop.order.id").Value!.StringValue);
    }

    private static KeyValuePair<string, object?> KVP(string key, object? value) => new(key, value);
}
