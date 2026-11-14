using System;
using System.Collections.Generic;
using System.Net.Http.Headers;

namespace Sentinel.Probe.Sdk.Emission;

/// <summary>
/// Carries the caller's current Event id to the callee, next to <c>traceparent</c>, so the callee
/// can set <c>sentinel.parent.event.id</c> and the causal edge composes across services.
/// The carrier header is <c>x-sentinel-parent-event-id</c>. The <c>sentinel</c> member of W3C
/// <c>tracestate</c> (<c>sentinel=&lt;eventId&gt;</c>) is read as a fallback; it is never written.
/// A caller that is not a Probe sends neither.
/// </summary>
public static class ParentEventPropagation
{
    /// <summary>The header (or Kafka header key) that carries the parent Event id.</summary>
    public const string ParentEventIdHeader = "x-sentinel-parent-event-id";

    /// <summary>The W3C trace-context state header.</summary>
    public const string TraceStateHeader = "tracestate";

    /// <summary>The <c>tracestate</c> member key that may carry the parent Event id.</summary>
    public const string TraceStateKey = "sentinel";

    /// <summary>Writes the parent Event id into a string-keyed carrier.</summary>
    public static void InjectParentEventId(IDictionary<string, string> carrier, string eventId)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        Inject(carrier, static (c, k, v) => c[k] = v, eventId);
    }

    /// <summary>Writes the parent Event id through <paramref name="setter"/> (any header collection).</summary>
    public static void Inject<TCarrier>(TCarrier carrier, Action<TCarrier, string, string> setter, string eventId)
    {
        ArgumentNullException.ThrowIfNull(setter);
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Parent Event id is required.", nameof(eventId));
        }
        if (HasControlCharacter(eventId))
        {
            throw new ArgumentException("Parent Event id contains a control character.", nameof(eventId));
        }
        setter(carrier, ParentEventIdHeader, eventId);
    }

    /// <summary>Writes the parent Event id into outgoing HTTP request headers.</summary>
    public static void InjectParentEventId(HttpHeaders headers, string eventId)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Inject(headers, static (h, k, v) =>
        {
            h.Remove(k);
            h.TryAddWithoutValidation(k, v);
        }, eventId);
    }

    /// <summary>Reads the parent Event id from a string-keyed carrier (header names match case-insensitively).</summary>
    public static string? ExtractParentEventId(IEnumerable<KeyValuePair<string, string>> carrier)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        return Extract(carrier, static (c, name) =>
        {
            foreach (var pair in c)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
            return null;
        });
    }

    /// <summary>Reads the parent Event id from incoming HTTP headers.</summary>
    public static string? ExtractParentEventId(HttpHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return Extract(headers, static (h, name) => h.TryGetValues(name, out var values) ? string.Join(",", values) : null);
    }

    /// <summary>
    /// Reads the parent Event id through <paramref name="getter"/>, which returns a header's value
    /// or null. The dedicated header wins; the <c>sentinel</c> member of <c>tracestate</c> is the fallback.
    /// A blank value counts as absent. Returns null when the caller is not a Probe.
    /// </summary>
    public static string? Extract<TCarrier>(TCarrier carrier, Func<TCarrier, string, string?> getter)
    {
        ArgumentNullException.ThrowIfNull(getter);
        var direct = getter(carrier, ParentEventIdHeader)?.Trim();
        if (!string.IsNullOrEmpty(direct) && !HasControlCharacter(direct))
        {
            return direct;
        }
        var state = getter(carrier, TraceStateHeader);
        return state == null ? null : FromTraceState(state);
    }

    /// <summary>Finds <c>sentinel=&lt;eventId&gt;</c> in a <c>tracestate</c> list.</summary>
    public static string? FromTraceState(string traceState)
    {
        ArgumentNullException.ThrowIfNull(traceState);
        foreach (var member in traceState.Split(','))
        {
            var text = member.Trim();
            var eq = text.IndexOf('=');
            if (eq <= 0 || !string.Equals(text.Substring(0, eq).Trim(), TraceStateKey, StringComparison.Ordinal))
            {
                continue;
            }
            var value = text.Substring(eq + 1).Trim();
            if (value.Length > 0 && !HasControlCharacter(value))
            {
                return value;
            }
        }
        return null;
    }

    private static bool HasControlCharacter(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }
        return false;
    }
}
