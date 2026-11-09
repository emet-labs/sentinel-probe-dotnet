using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Sentinel.Probe.Sdk.Tests;

// Builds ended Activity instances for span-to-event tests. .NET has no tracetest.SpanStub, so we
// build a real Activity through an ActivitySource that is always sampled by a loopback listener.
// Links are passed to StartActivity (Activity has no public SetLinks), and typed tags are set
// afterwards via SetTag — which preserves runtime types through TagObjects (the S1 fix).
internal static class ActivityBuilder
{
    public static readonly DateTime StartTime =
        DateTimeOffset.FromUnixTimeSeconds(1700000000).AddTicks(1234567).UtcDateTime; // 1700000000.1234567s

    private static readonly ActivitySource Source = new("sentinel.test", "1.0.0");

    static ActivityBuilder()
    {
        // Ensure StartActivity always returns a non-null, fully-recorded activity regardless of
        // the ambient Activity.Current, so tests do not depend on any host listener.
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
    }

    public static ActivitySpanId SpanIdFromHex(string hex) => ActivitySpanId.CreateFromString(hex.AsSpan());

    public static Activity Build(
        string name = "x",
        IEnumerable<KeyValuePair<string, object?>>? tags = null,
        string? parentSpanHex = null,
        IEnumerable<(string SpanHex, IEnumerable<KeyValuePair<string, object?>>? Tags)>? links = null,
        DateTime? startTime = null,
        DateTime? endTime = null)
    {
        ActivityContext? parentContext = null;
        if (parentSpanHex != null)
        {
            parentContext = new ActivityContext(ActivityTraceId.CreateRandom(), SpanIdFromHex(parentSpanHex), ActivityTraceFlags.None);
        }

        List<ActivityLink>? linkList = null;
        if (links != null)
        {
            linkList = new List<ActivityLink>();
            foreach (var (hex, lt) in links)
            {
                var ctx = new ActivityContext(ActivityTraceId.CreateRandom(), SpanIdFromHex(hex), ActivityTraceFlags.None);
                linkList.Add(new ActivityLink(ctx, ToTags(lt)));
            }
        }

        DateTimeOffset? startOffset = startTime.HasValue ? new DateTimeOffset(startTime.Value, TimeSpan.Zero) : null;
        var activity = Source.StartActivity(name, ActivityKind.Internal, parentContext ?? default, links: linkList, startTime: startOffset ?? default);
        // The listener samples AllDataAndRecorded, so activity is never null here.
        if (activity == null) throw new InvalidOperationException("test ActivitySource was not sampled");

        if (tags != null)
        {
            foreach (var t in tags) activity.SetTag(t.Key, t.Value);
        }
        if (endTime.HasValue)
        {
            activity.SetEndTime(DateTime.SpecifyKind(endTime.Value, DateTimeKind.Utc));
        }
        activity.Stop();
        return activity;
    }

    private static ActivityTagsCollection? ToTags(IEnumerable<KeyValuePair<string, object?>>? tags)
    {
        if (tags == null) return null;
        var col = new ActivityTagsCollection();
        foreach (var t in tags) col.Add(t);
        return col;
    }
}
