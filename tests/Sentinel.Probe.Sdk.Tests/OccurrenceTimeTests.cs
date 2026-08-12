using System;
using System.Numerics;
using Xunit;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Emission;
using Sentinel.Probe.Sdk.Int128;

namespace Sentinel.Probe.Sdk.Tests;

public class OccurrenceTimeTests
{
    private static readonly DateTime T = DateTimeOffset.FromUnixTimeSeconds(1700000000).AddTicks(1234567).UtcDateTime;

    [Fact]
    public void UsesUnixClockDomainAndZeroUncertainty()
    {
        var occurrence = SpanToEventConverter.BuildOccurrenceTime(ActivityBuilder.Build(startTime: T));
        Assert.Equal("unix", occurrence.ClockDomainId);
        Assert.Equal(0UL, occurrence.UncertaintyNanoseconds);
        Assert.Equal(new BigInteger(1700000000123456700), Int128Codec.ToBigInteger(occurrence.Nanoseconds));
    }

    [Fact]
    public void ComesFromStartNotEndTime()
    {
        var a = ActivityBuilder.Build(startTime: DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime);
        // Stop sets EndTimeUtc to "now" by default; ensure occurrence uses the start time.
        var occurrence = SpanToEventConverter.BuildOccurrenceTime(a);
        Assert.Equal(new BigInteger(1700000000000000000), Int128Codec.ToBigInteger(occurrence.Nanoseconds));
    }

    [Fact]
    public void SurvivesBeyondInt64Nanoseconds()
    {
        var moment = new DateTimeOffset(3000, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime;
        var occurrence = SpanToEventConverter.BuildOccurrenceTime(ActivityBuilder.Build(startTime: moment));
        Assert.NotEqual(0L, occurrence.Nanoseconds!.High); // spans both words
        var want = Int128Codec.TimeToNanoseconds(new DateTimeOffset(moment));
        Assert.Equal(want, Int128Codec.ToBigInteger(occurrence.Nanoseconds));
    }

    [Fact]
    public void BeforeTheEpoch()
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(-1).AddTicks(5_000_000).UtcDateTime; // -0.5s
        var occurrence = SpanToEventConverter.BuildOccurrenceTime(ActivityBuilder.Build(startTime: moment));
        Assert.Equal(new BigInteger(-500000000), Int128Codec.ToBigInteger(occurrence.Nanoseconds));
        Assert.Equal(-1L, occurrence.Nanoseconds!.High); // sign extension
    }

    [Fact]
    public void SpanToEventCarriesTheOccurrenceTime()
    {
        var e = SpanToEventConverter.SpanToEvent(new SpanConversion
        {
            Activity = ActivityBuilder.Build(startTime: DateTimeOffset.FromUnixTimeSeconds(1700000000).AddTicks(10).UtcDateTime),
            SchemaVersion = "sentinel.model.v1",
            EventId = "evt-1",
        });
        Assert.NotNull(e.OccurrenceTime);
        Assert.Equal(new BigInteger(1700000000000001000), Int128Codec.ToBigInteger(e.OccurrenceTime!.Nanoseconds));
    }
}
