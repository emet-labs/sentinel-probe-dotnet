using System;
using System.Diagnostics;
using OpenTelemetry;
using Sentinel.Probe.Sdk.Otlp;

namespace Sentinel.Probe.Sdk.Emission;

// SequenceStampingProcessor stamps each Activity with a Sequence slot when it starts (#461).
//
// Allocation happens at start, before any Event Filter, so a filtered Event still holds its slot.
// An Activity that already carries both sentinel.sequence.* tags (a slot the host reserved ahead
// of its Event) is left alone. This SDK has no exporting processor: the host reads the slot back
// with TryGetSlot, builds its SpanExport from it, and marks the slot from
// OtlpExporter.ExportSpansCheckedAsync — exported only on HTTP 2xx with zero rejected spans,
// failed otherwise.

/// <summary>A span processor that allocates a Sequence slot for each Activity at start.</summary>
public sealed class SequenceStampingProcessor : BaseProcessor<Activity>
{
    private readonly SequenceAllocator _sequence;

    public SequenceStampingProcessor(SequenceAllocator sequence)
    {
        _sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
    }

    /// <summary>
    /// Stamps <c>sentinel.sequence.epoch</c> and <c>sentinel.sequence.number</c> as int64 tags,
    /// unless the Activity already carries both. An allocation past 2^63 - 1 leaves the Activity
    /// unstamped, so its export is refused as missing a slot rather than failing the host's call.
    /// </summary>
    public override void OnStart(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (TryGetSlot(data, out _))
        {
            return;
        }
        Slot slot;
        try
        {
            slot = _sequence.Allocate();
        }
        catch (SequenceException)
        {
            return;
        }
        data.SetTag(OtlpTraces.SequenceEpochKey, (long)slot.Epoch);
        data.SetTag(OtlpTraces.SequenceNumberKey, (long)slot.Number);
    }

    /// <summary>Reads the slot stamped on an Activity. False when either tag is absent, negative or not an integer.</summary>
    public static bool TryGetSlot(Activity activity, out Slot slot)
    {
        ArgumentNullException.ThrowIfNull(activity);
        slot = default;
        if (!TryUint(activity.GetTagItem(OtlpTraces.SequenceEpochKey), out var epoch)
            || !TryUint(activity.GetTagItem(OtlpTraces.SequenceNumberKey), out var number))
        {
            return false;
        }
        slot = new Slot(epoch, number);
        return true;
    }

    private static bool TryUint(object? value, out ulong result)
    {
        switch (value)
        {
            case long l when l >= 0:
                result = (ulong)l;
                return true;
            case int i when i >= 0:
                result = (ulong)i;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
