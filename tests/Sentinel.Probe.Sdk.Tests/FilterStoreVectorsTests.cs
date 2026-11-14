using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Client;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

// Executes the shared filter_store suite (manifest-v1.json, 1.4.0) through the production
// FilterStore. Epochs are decimal strings parsed as ulong, so the full unsigned range is exact.
public class FilterStoreVectorsTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "probe-sdk-conformance");

    private static JsonElement Read(string name) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name))).RootElement.Clone();

    private static ulong? Epoch(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : ulong.Parse(value.GetString()!);

    private static EventFilter Build(JsonElement fixture)
    {
        var filter = new EventFilter();
        var epoch = Epoch(fixture.GetProperty("epoch"));
        if (epoch.HasValue)
        {
            filter.Epoch = epoch.Value;
        }
        filter.Specifications.Add(new SpecificationFilter { SpecificationId = fixture.GetProperty("label").GetString()! });
        return filter;
    }

    [Fact]
    public void VectorsReplayTheProductionStore()
    {
        var manifest = Read("manifest-v1.json");
        Assert.Equal("1.4.0", manifest.GetProperty("format_version").GetString());
        var entry = Assert.Single(manifest.GetProperty("suites").EnumerateArray(),
            suite => suite.GetProperty("kind").GetString() == "filter_store");
        var document = Read(entry.GetProperty("path").GetString()!);
        Assert.Equal("1.2.0", document.GetProperty("format_version").GetString());
        Assert.Equal("filter_store", document.GetProperty("kind").GetString());
        var cases = document.GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var vector in cases)
        {
            var initial = vector.GetProperty("initial");
            EventFilter? applied = initial.ValueKind == JsonValueKind.Null ? null : Build(initial);
            var store = new FilterStore(applied);
            // The held reference is asserted until an equal-epoch set, which pins only content.
            var trackReference = true;
            var index = 0;
            foreach (var step in vector.GetProperty("steps").EnumerateArray())
            {
                var at = vector.GetProperty("id").GetString() + " step " + index++;
                switch (step.GetProperty("op").GetString())
                {
                    case "should_refresh":
                        Assert.True(step.GetProperty("expected").GetBoolean() == store.ShouldRefresh(Epoch(step.GetProperty("epoch"))), at);
                        break;
                    case "set":
                        var offered = Build(step.GetProperty("filter"));
                        var replaced = store.Set(offered);
                        var expected = step.GetProperty("expected");
                        if (expected.TryGetProperty("applied", out var wantApplied))
                        {
                            Assert.True(wantApplied.GetBoolean() == replaced, at);
                            if (replaced)
                            {
                                applied = offered;
                            }
                        }
                        else
                        {
                            trackReference = false;
                        }
                        var held = store.Get();
                        Assert.True(held != null, at);
                        if (trackReference)
                        {
                            Assert.True(ReferenceEquals(applied, held), at);
                        }
                        var want = expected.GetProperty("held");
                        Assert.True(held!.Specifications.Count == 1 && held.Specifications[0].SpecificationId == want.GetProperty("label").GetString(), at);
                        var wantEpoch = Epoch(want.GetProperty("epoch"));
                        Assert.True(held.HasEpoch == wantEpoch.HasValue, at);
                        Assert.True(store.Epoch() == wantEpoch, at);
                        break;
                    default:
                        throw new InvalidOperationException(at + ": unknown op");
                }
            }
        }
    }
}
