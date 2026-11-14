using System;

namespace Sentinel.Probe.Sdk.Otlp;

/// <summary>
/// Reads the ExportTraceServiceResponse Sentinel returns for a trace export (#461).
/// <para>
/// An export succeeded only on HTTP 2xx with <c>partial_success.rejected_spans == 0</c>; an absent
/// partial_success counts as 0. Sentinel counts conflicts, admission refusals and Events above a
/// closed boundary as rejected, so a rejected span is an Event Sentinel does not hold. A duplicate
/// is reported with zero rejected spans and a message. A body that does not decode is a failure,
/// never a success.
/// </para>
/// <para>
/// The two fields are decoded by hand (<c>ExportTraceServiceResponse.partial_success = 1</c>,
/// <c>ExportTracePartialSuccess.rejected_spans = 1</c>, an int64), with no dependency on the
/// generated types or on OtlpCodec; every other field is skipped by wire type.
/// </para>
/// </summary>
public static class PartialSuccess
{
    /// <summary>Returns the rejected span count of a 2xx trace export body.</summary>
    /// <exception cref="FormatException">The body is not a valid ExportTraceServiceResponse.</exception>
    public static long RejectedSpans(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ulong rejected = 0;
        var position = 0;
        while (position < body.Length)
        {
            var (field, wireType) = Tag(body, ref position);
            if (field == 1 && wireType == 2)
            {
                var (start, end) = LengthDelimited(body, ref position);
                rejected = Partial(body, start, end, rejected);
            }
            else
            {
                Skip(body, ref position, body.Length, wireType);
            }
        }
        if (rejected > long.MaxValue)
        {
            throw new FormatException("rejected_spans is negative");
        }
        return (long)rejected;
    }

    // ExportTracePartialSuccess in body[start, end). Repeated occurrences merge: the last
    // rejected_spans seen wins.
    private static ulong Partial(byte[] body, int start, int end, ulong rejected)
    {
        var position = start;
        while (position < end)
        {
            var (field, wireType) = Tag(body, ref position, end);
            if (field == 1 && wireType == 0)
            {
                rejected = Varint(body, ref position, end);
            }
            else
            {
                Skip(body, ref position, end, wireType);
            }
        }
        return rejected;
    }

    private static (ulong Field, int WireType) Tag(byte[] body, ref int position, int? end = null)
    {
        var key = Varint(body, ref position, end ?? body.Length);
        if (key >> 3 == 0)
        {
            throw new FormatException("field number 0");
        }
        return (key >> 3, (int)(key & 7));
    }

    private static void Skip(byte[] body, ref int position, int end, int wireType)
    {
        switch (wireType)
        {
            case 0:
                Varint(body, ref position, end);
                break;
            case 1:
                Advance(ref position, end, 8);
                break;
            case 2:
                var (_, stop) = LengthDelimited(body, ref position, end);
                position = stop;
                break;
            case 5:
                Advance(ref position, end, 4);
                break;
            default:
                throw new FormatException("unsupported wire type " + wireType.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static (int Start, int End) LengthDelimited(byte[] body, ref int position, int? end = null)
    {
        var limit = end ?? body.Length;
        var size = Varint(body, ref position, limit);
        if (size > (ulong)(limit - position))
        {
            throw new FormatException("truncated length-delimited field");
        }
        var start = position;
        position += (int)size;
        return (start, position);
    }

    private static void Advance(ref int position, int end, int size)
    {
        if (end - position < size)
        {
            throw new FormatException("truncated fixed-width field");
        }
        position += size;
    }

    // A base-128 varint of at most ten bytes, as an unsigned 64-bit value.
    private static ulong Varint(byte[] body, ref int position, int end)
    {
        ulong value = 0;
        for (var index = 0; index < 10; index++)
        {
            if (position >= end)
            {
                throw new FormatException("truncated varint");
            }
            var current = body[position++];
            if (index == 9 && current > 1)
            {
                throw new FormatException("varint overflows 64 bits");
            }
            value |= (ulong)(current & 0x7F) << (7 * index);
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }
        throw new FormatException("varint longer than ten bytes");
    }
}
