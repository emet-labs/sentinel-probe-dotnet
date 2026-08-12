using System.Numerics;
using Emet.Sentinel.Model.V1;
// The module namespace's last segment ('Int128') would shadow the model type of the same name,
// so alias the proto message to keep the code self-documenting without fighting the binder.
using Int128Value = Emet.Sentinel.Model.V1.Int128;

namespace Sentinel.Probe.Sdk.Int128;

// Int128 codec — the .NET analog of sdk/go/int128/int128.go.
//
// .NET has no native int128, so System.Numerics.BigInteger is the canonical representation — the
// idiomatic equivalent of Go's math/big.Int and TypeScript's bigint. FromInt64 covers the common
// nanosecond case without allocating a BigInteger.
//
// Encode direction is the trap (common.proto:7-10): `low` is fixed64 (UNSIGNED) and `high` is
// sfixed64 (SIGNED), and the value is high*2^64 + low:
//   - encode: low = value & (2^64-1) (as unsigned), high = value >> 64 (arithmetic shift);
//   - decode: value = high*2^64 + low, with NO sign correction — high is already signed.
//
// Worked check for value = -1: high = -1, low = 0xFFFFFFFFFFFFFFFF, and
// -1*2^64 + (2^64 - 1) = -1. Applying a second sign correction on decode is a real bug; it is not
// applied here.
public static class Int128Codec
{
    // 2^64, the weight of the high word.
    private static readonly BigInteger TwoPow64 = BigInteger.One << 64;

    // Mask for the low 64 bits. BigInteger bitwise operations behave as if operands were in
    // infinite two's complement, so & also yields the correct unsigned low word for negatives.
    private static readonly BigInteger LowMask = TwoPow64 - 1;

    /// <summary>Decodes an Int128 into its integer value: high*2^64 + low. A null decodes to zero.</summary>
    public static BigInteger ToBigInteger(Int128Value? value)
    {
        if (value == null) return BigInteger.Zero;
        // sfixed64 high is already signed; fixed64 low is unsigned.
        return (BigInteger)value.High * TwoPow64 + (BigInteger)value.Low;
    }

    /// <summary>
    /// Encodes an integer value into an Int128. Values outside the signed 128-bit range are
    /// truncated to their low 128 bits — the same modular behaviour the proto's fixed words have.
    /// </summary>
    public static Int128Value FromBigInteger(BigInteger value)
    {
        BigInteger low = value & LowMask;
        BigInteger high = value >> 64; // arithmetic on negatives: floor division, sign-extended
        return new Int128Value { High = (long)high, Low = (ulong)low };
    }

    /// <summary>
    /// Encodes an int64 without allocating a BigInteger. high is the sign extension (0 or -1) and
    /// low is the two's-complement bit pattern.
    /// </summary>
    public static Int128Value FromInt64(long value)
    {
        return new Int128Value { High = value >> 63, Low = (ulong)value };
    }

    /// <summary>
    /// Returns <paramref name="t"/> as nanoseconds since the Unix epoch. Analog of Go's
    /// TimeToNanoseconds. DateTimeOffset has 100ns (tick) precision — the only lossy step on this
    /// platform — so the big form is exact up to tick granularity, and spans the full Int128 range
    /// where a (non-existent) UnixNano helper would wrap outside 1678-2262.
    /// </summary>
    public static BigInteger TimeToNanoseconds(DateTimeOffset t)
    {
        long seconds = t.ToUnixTimeSeconds();
        // 1 tick = 100ns; sub-second nanoseconds = (ticks within the second) * 100.
        long subNanos = (t.UtcTicks % System.TimeSpan.TicksPerSecond) * 100;
        return (BigInteger)seconds * 1_000_000_000 + subNanos;
    }
}
