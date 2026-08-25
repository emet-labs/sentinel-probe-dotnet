using System.Numerics;
using System.Text.Json;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Filter;
using Sentinel.Probe.Sdk.Int128;
using Xunit;
using Int128Value = Emet.Sentinel.Model.V1.Int128;

namespace Sentinel.Probe.Sdk.Tests;

public class ConformanceVectorsTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "probe-sdk-conformance");

    private static JsonDocument Load(string name)
    {
        var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name)));
        RejectDuplicateKeys(document.RootElement);
        Assert.Equal("1.0.0", document.RootElement.GetProperty("format_version").GetString());
        return document;
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Assert.True(names.Add(property.Name), $"duplicate JSON key {property.Name}");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                RejectDuplicateKeys(child);
            }
        }
    }

    [Fact]
    public void Int128VectorsAssertWordsAndIndependentDecode()
    {
        using var suite = Load("int128-v1.json");
        Assert.Equal("int128", suite.RootElement.GetProperty("kind").GetString());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in suite.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = vector.GetProperty("id").GetString()!;
            Assert.True(ids.Add(id));
            var value = BigInteger.Parse(vector.GetProperty("value").GetString()!);
            var high = long.Parse(vector.GetProperty("high").GetString()!);
            var low = ulong.Parse(vector.GetProperty("low").GetString()!);
            var encoded = Int128Codec.FromBigInteger(value);
            Assert.Equal(high, encoded.High);
            Assert.Equal(low, encoded.Low);
            Assert.Equal(value, Int128Codec.ToBigInteger(new Int128Value { High = high, Low = low }));
        }
    }

    [Fact]
    public void SpecMatchVectorsInvokeProductionPredicate()
    {
        using var suite = Load("spec-match-v1.json");
        Assert.Equal("spec_match", suite.RootElement.GetProperty("kind").GetString());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in suite.RootElement.GetProperty("cases").EnumerateArray())
        {
            Assert.True(ids.Add(vector.GetProperty("id").GetString()!));
            var fixtureFilter = vector.GetProperty("specification_filter");
            var filter = new SpecificationFilter();
            var fixtureMatch = fixtureFilter.GetProperty("event_match");
            if (fixtureMatch.ValueKind != JsonValueKind.Null)
            {
                filter.EventMatch = new EventMatch();
                foreach (var kind in fixtureMatch.GetProperty("event_kinds").EnumerateArray())
                {
                    filter.EventMatch.EventKinds.Add(kind.GetString()!);
                }
                foreach (var key in fixtureMatch.GetProperty("projected_attribute_keys").EnumerateArray())
                {
                    filter.EventMatch.ProjectedAttributeKeys.Add(key.GetString()!);
                }
            }
            var producerEvent = new ProducerEvent
            {
                Kind = vector.GetProperty("producer_event").GetProperty("kind").GetString()!,
            };
            Assert.Equal(vector.GetProperty("expected").GetBoolean(), SpecMatch.Selects(filter, producerEvent));
        }
    }
}
