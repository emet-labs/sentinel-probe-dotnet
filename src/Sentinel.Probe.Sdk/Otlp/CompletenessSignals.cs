using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Emission;
using Sentinel.Probe.Sdk.Int128;

namespace Sentinel.Probe.Sdk.Otlp;

/// <summary>
/// Builds the ProducerCompletenessSignal the heartbeat sends (#461). Every generated-type access
/// for the heartbeat lives here. last_emitted, complete_through and acknowledged_filter_epoch are
/// always set, so presence reaches the wire for zero too; both instants are on the "unix" clock
/// with zero uncertainty.
/// </summary>
public static class CompletenessSignals
{
    /// <summary>The producer clock domain of every heartbeat signal.</summary>
    public const string ClockDomain = "unix";

    public static ProducerCompletenessSignal Build(
        EpochDeclaration declaration,
        ulong acknowledgedFilterEpoch,
        SourceActivityState activity,
        long reportedAtUnixNanoseconds,
        long activitySinceUnixNanoseconds,
        bool superseded = false)
    {
        System.ArgumentNullException.ThrowIfNull(declaration);
        var signal = new ProducerCompletenessSignal
        {
            SequenceEpoch = declaration.Epoch,
            LastEmitted = declaration.LastEmitted,
            CompleteThrough = declaration.CompleteThrough,
            AcknowledgedFilterEpoch = acknowledgedFilterEpoch,
            ProducerReportedAt = UnixTime(reportedAtUnixNanoseconds),
            Activity = activity,
            ActivitySince = UnixTime(activitySinceUnixNanoseconds),
        };
        // The four epoch-chain fields (ADR-0024 amendment). A declaration without Attempted is
        // legacy: none of them is sent, and Sentinel reads the row as lacking an epoch chain.
        if (declaration.Attempted is { } attempted)
        {
            signal.Attempted = attempted;
            if (declaration.PredecessorEpoch is { } predecessor)
            {
                signal.PredecessorEpoch = predecessor;
            }
            if (declaration.SealedAtUnixNanoseconds is { } sealedAt)
            {
                signal.SealedAt = UnixTime(sealedAt);
            }
            if (superseded)
            {
                signal.Superseded = true;
            }
        }
        return signal;
    }

    private static OccurrenceTime UnixTime(long nanoseconds) => new()
    {
        ClockDomainId = ClockDomain,
        Nanoseconds = Int128Codec.FromInt64(nanoseconds),
        UncertaintyNanoseconds = 0,
    };
}
