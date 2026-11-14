using System;
using System.Diagnostics;
using Sentinel.Probe.Sdk.Otlp;

namespace Sentinel.Probe.Sdk.Emission;

/// <summary>
/// Starts Event activities with an explicit parent, so a host does not have to know that
/// <c>ActivitySource.StartActivity</c> parents to <see cref="Activity.Current"/> whatever parent
/// context it is handed.
/// </summary>
public static class ProbeActivities
{
    /// <summary>
    /// Starts an Event activity.
    /// <para>
    /// <paramref name="parent"/> defaults to the ambient activity. An ambient activity that is a Probe
    /// Event (it carries <c>sentinel.event.id</c>) is a local parent and its id becomes
    /// <c>sentinel.parent.event.id</c>. Any other ambient activity (an inbound HTTP request span,
    /// which may continue a foreign <c>traceparent</c>) is treated as a remote parent without an
    /// Event id. With no ambient activity the Event starts a new trace.
    /// </para>
    /// <para>
    /// A <paramref name="parent"/> you pass is used as given: set <c>isRemote</c> on its
    /// <see cref="ActivityContext"/> when the caller is not a Probe, and pass the caller's Event id as
    /// <paramref name="parentEventId"/> (see <see cref="ParentEventPropagation.ExtractParentEventId(System.Net.Http.Headers.HttpHeaders)"/>)
    /// when it is.
    /// </para>
    /// <para>
    /// <see cref="Activity.Current"/> is cleared around the start so the explicit parent is honoured,
    /// then set to the new activity (or restored when the source is not sampled).
    /// </para>
    /// </summary>
    /// <returns>The activity, or null when no listener samples it.</returns>
    public static Activity? StartEvent(
        ActivitySource source,
        string name,
        ActivityKind kind = ActivityKind.Internal,
        ActivityContext parent = default,
        string? parentEventId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var ambient = Activity.Current;
        var context = parent;
        if (context == default && ambient != null)
        {
            var ambientEventId = ambient.GetTagItem(OtlpTraces.EventIdKey) as string;
            var isProbeEvent = !string.IsNullOrEmpty(ambientEventId);
            context = new ActivityContext(
                ambient.TraceId,
                ambient.SpanId,
                ambient.ActivityTraceFlags,
                ambient.TraceStateString,
                isRemote: !isProbeEvent);
            if (isProbeEvent && string.IsNullOrEmpty(parentEventId))
            {
                parentEventId = ambientEventId;
            }
        }

        Activity? started;
        Activity.Current = null;
        try
        {
            started = source.StartActivity(name, kind, context);
        }
        finally
        {
            Activity.Current = ambient;
        }
        if (started == null)
        {
            return null;
        }
        Activity.Current = started;
        if (!string.IsNullOrEmpty(parentEventId))
        {
            started.SetTag(OtlpTraces.ParentEventIdKey, parentEventId);
        }
        return started;
    }
}
