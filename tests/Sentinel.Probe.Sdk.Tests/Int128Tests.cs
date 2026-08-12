using System;
using System.Numerics;
using Xunit;
using Emet.Sentinel.Model.V1;
using Int128Value = Emet.Sentinel.Model.V1.Int128;
using Sentinel.Probe.Sdk.Int128;

namespace Sentinel.Probe.Sdk.Tests;

public class Int128Tests
{
    private static BigInteger Big(string digits) => BigInteger.Parse(digits);

    public static IEnumerable<object[]> RoundTripCases()
    {
        yield return new object[] { "0" };
        yield return new object[] { "1" };
        yield return new object[] { "1700000000123456789" };
        yield return new object[] { "18446744073709551615" }; // max low word
        yield return new object[] { "18446744073709551616" }; // 2^64, spans both words
        yield return new object[] { "170141183460469231731687303715884105727" }; // max int128
        yield return new object[] { "-1" };
        yield return new object[] { "-18446744073709551617" };
        yield return new object[] { "-170141183460469231731687303715884105728" }; // min int128
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void RoundTrip(string value)
    {
        var want = Big(value);
        var got = Int128Codec.ToBigInteger(Int128Codec.FromBigInteger(want));
        Assert.Equal(want, got);
    }

    // low is fixed64 (unsigned), high is sfixed64 (signed): the direction that is easy to reverse.
    [Fact]
    public void EncodeWordSignedness()
    {
        var positive = Int128Codec.FromBigInteger(Big("18446744073709551617")); // 2^64 + 1
        Assert.Equal(1L, positive.High);
        Assert.Equal(1UL, positive.Low);

        // -1 -> high=-1, low=0xFFFFFFFFFFFFFFFF, and -1*2^64 + (2^64-1) = -1 with NO sign correction.
        var negative = Int128Codec.FromBigInteger(-1);
        Assert.Equal(-1L, negative.High);
        Assert.Equal(ulong.MaxValue, negative.Low);
        Assert.Equal(new BigInteger(-1), Int128Codec.ToBigInteger(negative));
    }

    [Fact]
    public void FromInt64MatchesFromBigInt()
    {
        foreach (long value in new long[] { 0, 1, -1, long.MaxValue, long.MinValue, 1700000000123456789, -1700000000123456789 })
        {
            var fast = Int128Codec.FromInt64(value);
            var slow = Int128Codec.FromBigInteger(value);
            Assert.Equal(slow.High, fast.High);
            Assert.Equal(slow.Low, fast.Low);
            Assert.Equal(new BigInteger(value), Int128Codec.ToBigInteger(fast));
        }
    }

    [Fact]
    public void ToBigIntegerIsNilSafe()
    {
        Assert.Equal(BigInteger.Zero, Int128Codec.ToBigInteger(null));
        Assert.Equal(BigInteger.Zero, Int128Codec.ToBigInteger(new Int128Value()));
    }

    [Fact]
    public void TimeToNanoseconds()
    {
        var cases = new (string name, DateTimeOffset time, string want)[]
        {
            ("epoch", DateTimeOffset.FromUnixTimeSeconds(0), "0"),
            ("whole seconds", DateTimeOffset.FromUnixTimeSeconds(1700000000), "1700000000000000000"),
            ("seconds and ticks", DateTimeOffset.FromUnixTimeSeconds(1700000000).AddTicks(1234567), "1700000000123456700"),
            ("before the epoch", DateTimeOffset.FromUnixTimeSeconds(-1).AddTicks(5_000_000), "-500000000"),
            ("year 3000", new DateTimeOffset(3000, 1, 1, 0, 0, 0, TimeSpan.Zero), "32503680000000000000"),
            ("year 1000", new DateTimeOffset(1000, 1, 1, 0, 0, 0, TimeSpan.Zero), "-30610224000000000000"),
        };
        foreach (var (name, time, want) in cases)
        {
            Assert.Equal(Big(want), Int128Codec.TimeToNanoseconds(time));
        }
    }

    [Fact]
    public void TimeToNanosecondsRoundTripsThroughInt128()
    {
        var moments = new DateTimeOffset[]
        {
            DateTimeOffset.FromUnixTimeSeconds(1700000000).AddTicks(1234567),
            new DateTimeOffset(3000, 1, 1, 0, 0, 0, 1, TimeSpan.Zero),
            new DateTimeOffset(1000, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        foreach (var moment in moments)
        {
            var want = Int128Codec.TimeToNanoseconds(moment);
            var got = Int128Codec.ToBigInteger(Int128Codec.FromBigInteger(want));
            Assert.Equal(want, got);
        }
    }

    [Fact]
    public void TimeToNanosecondsBeyondInt64Range()
    {
        // Year 3000 overflows int64 nanoseconds; the Int128 encoding must span both words.
        var moment = new DateTimeOffset(3000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var nanoseconds = Int128Codec.TimeToNanoseconds(moment);
        Assert.False(nanoseconds <= long.MaxValue && nanoseconds >= long.MinValue); // beyond int64
        var encoded = Int128Codec.FromBigInteger(nanoseconds);
        Assert.NotEqual(0L, encoded.High);
        Assert.Equal(nanoseconds, Int128Codec.ToBigInteger(encoded));
    }
}
