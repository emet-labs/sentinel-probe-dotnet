using System;
using System.Diagnostics;
using Emet.Sentinel.Model.V1;
using OpenTelemetry;

namespace Sentinel.Probe.Sdk.Emission;

// ProbeSpanProcessor — the .NET analog the Go reference describes as SpanProcessor.OnEnd
// (S2 fix): a span processor that calls SpanToEvent in OnEnd. .NET OTel's span processor base is
// BaseProcessor<Activity>, so the Probe wires span-to-event conversion into the SDK's processor
// pipeline exactly as a host would attach any other processor to a TracerProvider.
//
// The processor never decides where events go: it hands each converted ProducerEvent to an
// injected callback (the host's emitter/enqueuer), keeping the SDK export-agnostic per ADR-0002.

/// <summary>
/// A span processor that converts each ended Activity into a ProducerEvent via a supplied
/// conversion and forwards it to a callback. Mirrors SpanProcessor.OnEnd(ReadOnlySpan).
/// </summary>
public sealed class ProbeSpanProcessor : BaseProcessor<Activity>
{
    private readonly Func<Activity, ProducerEvent?> _convert;
    private readonly Action<ProducerEvent>? _onEvent;

    /// <param name="convert">Converts an ended activity into a ProducerEvent (or null to drop).</param>
    /// <param name="onEvent">Receives each non-null converted event; null is a no-op sink.</param>
    public ProbeSpanProcessor(Func<Activity, ProducerEvent?> convert, Action<ProducerEvent>? onEvent = null)
    {
        _convert = convert ?? throw new ArgumentNullException(nameof(convert));
        _onEvent = onEvent;
    }

    /// <summary>
    /// Invoked by the SDK when an activity ends. Converts the activity and forwards the event; a
    /// null conversion is silently dropped, matching SpanToEvent's contract that some activities
    /// are not events.
    /// </summary>
    public override void OnEnd(Activity activity)
    {
        var @event = _convert(activity);
        if (@event != null)
        {
            _onEvent?.Invoke(@event);
        }
    }
}
