using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTelemetry.Trace;
using Sentinel.Probe.Sdk.Emission;
using Sentinel.Probe.Sdk.Otlp;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

public class SequenceStampingProcessorTests
{
    private const ulong StartMs = 1_794_000_000_000UL;

    private readonly SequenceAllocator _sequence = new(ManualTimeProvider.AtUnixMilliseconds((long)StartMs));

    [Fact]
    public void OnStartStampsTheSlotAndTryGetSlotReadsItBack()
    {
        using var activity = new Activity("sagashop.payment.charge.completed");
        new SequenceStampingProcessor(_sequence).OnStart(activity);
        Assert.Equal((long)StartMs, activity.GetTagItem(OtlpTraces.SequenceEpochKey));
        Assert.Equal(1L, activity.GetTagItem(OtlpTraces.SequenceNumberKey));
        Assert.True(SequenceStampingProcessor.TryGetSlot(activity, out var slot));
        Assert.Equal(new Slot(StartMs, 1), slot);
        Assert.Equal(0UL, _sequence.Declaration().CompleteThrough);
        Assert.Equal(1UL, _sequence.Declaration().Attempted); // the hook entry was counted (C1)
    }

    [Fact]
    public void AHostStampedActivityKeepsItsSlot()
    {
        var reserved = _sequence.Allocate();
        using var activity = new Activity("sagashop.payment.charge.completed");
        activity.SetTag(OtlpTraces.SequenceEpochKey, (long)reserved.Epoch);
        activity.SetTag(OtlpTraces.SequenceNumberKey, (long)reserved.Number);
        new SequenceStampingProcessor(_sequence).OnStart(activity);
        Assert.Equal(1UL, _sequence.HighestAllocated());
        Assert.Equal(1UL, _sequence.Declaration().Attempted); // a host-stamped Activity is not a second hook entry
        Assert.True(SequenceStampingProcessor.TryGetSlot(activity, out var slot));
        Assert.Equal(reserved, slot);
    }

    [Fact]
    public void AHalfStampedActivityGetsAFreshSlot()
    {
        using var activity = new Activity("sagashop.payment.charge.completed");
        activity.SetTag(OtlpTraces.SequenceNumberKey, 9L);
        new SequenceStampingProcessor(_sequence).OnStart(activity);
        Assert.True(SequenceStampingProcessor.TryGetSlot(activity, out var slot));
        Assert.Equal(new Slot(StartMs, 1), slot);
    }

    [Fact]
    public void TryGetSlotRefusesAbsentNegativeOrMistypedTags()
    {
        using var absent = new Activity("x");
        Assert.False(SequenceStampingProcessor.TryGetSlot(absent, out _));
        using var negative = new Activity("x");
        negative.SetTag(OtlpTraces.SequenceEpochKey, -1L);
        negative.SetTag(OtlpTraces.SequenceNumberKey, 1L);
        Assert.False(SequenceStampingProcessor.TryGetSlot(negative, out _));
        using var text = new Activity("x");
        text.SetTag(OtlpTraces.SequenceEpochKey, "7");
        text.SetTag(OtlpTraces.SequenceNumberKey, 1L);
        Assert.False(SequenceStampingProcessor.TryGetSlot(text, out _));
        using var small = new Activity("x");
        small.SetTag(OtlpTraces.SequenceEpochKey, 7);
        small.SetTag(OtlpTraces.SequenceNumberKey, 1);
        Assert.True(SequenceStampingProcessor.TryGetSlot(small, out var slot));
        Assert.Equal(new Slot(7, 1), slot);
    }

    [Fact]
    public void ATracerProviderStampsActivitiesWhenTheyStart()
    {
        using var tracer = ProbeTracer.NewProbeTracer(new ProbeTracerOptions
        {
            TracerName = "probe.sequence.test",
            ServiceName = "sagashop",
            ConfigureProvider = builder => builder.AddProcessor(new SequenceStampingProcessor(_sequence)),
        });
        using var first = tracer.Source.StartActivity("sagashop.payment.charge.completed");
        Assert.NotNull(first);
        Assert.True(SequenceStampingProcessor.TryGetSlot(first!, out var one));
        using var host = tracer.Source.StartActivity(
            ActivityKind.Internal,
            tags: new[]
            {
                new KeyValuePair<string, object?>(OtlpTraces.SequenceEpochKey, (long)StartMs),
                new KeyValuePair<string, object?>(OtlpTraces.SequenceNumberKey, 99L),
            },
            name: "sagashop.order.ship.requested");
        Assert.NotNull(host);
        Assert.True(SequenceStampingProcessor.TryGetSlot(host!, out var hostSlot));
        Assert.Equal(new Slot(StartMs, 1), one);
        Assert.Equal(new Slot(StartMs, 99), hostSlot);
        Assert.Equal(1UL, _sequence.HighestAllocated());
    }
}
