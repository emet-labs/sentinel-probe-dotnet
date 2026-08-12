using System.Collections.Generic;
using Xunit;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Config;

namespace Sentinel.Probe.Sdk.Tests;

public class SourceTierTests
{
    private static ISourceTierConfig MustLoad(string document) => SourceTierLoader.LoadSourceTierConfig(document);

    [Fact]
    public void ValidConfigParses()
    {
        var loaded = MustLoad(@"{""gateway.tool-calls"": {""tier"": ""ANCHOR""}, ""agent.runtime"": {""tier"": ""CONTRIBUTING""}}");
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public void TierForHandleResolvesBothTiers()
    {
        var loaded = MustLoad(@"{""gateway.tool-calls"": {""tier"": ""ANCHOR""}, ""agent.runtime"": {""tier"": ""CONTRIBUTING""}}");
        Assert.Equal(SourceTier.SourceTierAnchor, SourceTierLoader.TierForHandle(loaded, "gateway.tool-calls"));
        Assert.Equal(SourceTier.SourceTierContributing, SourceTierLoader.TierForHandle(loaded, "agent.runtime"));
    }

    [Fact]
    public void UndeclaredHandleIsAnError()
    {
        var loaded = MustLoad(@"{""gateway.tool-calls"": {""tier"": ""ANCHOR""}}");
        var ex = Assert.Throws<System.FormatException>(() => SourceTierLoader.TierForHandle(loaded, "unknown.handle"));
        Assert.Contains(@"no entry for source_handle ""unknown.handle""", ex.Message);
    }

    [Fact]
    public void EmptyConfigNeverDefaults()
    {
        var loaded = MustLoad("{}");
        Assert.Empty(loaded);
        Assert.Throws<System.FormatException>(() => SourceTierLoader.TierForHandle(loaded, "any.handle"));
    }

    [Theory]
    [InlineData(@"{""bad.source"": {""tier"": ""PRIMARY""}}")]
    [InlineData(@"{""bad.source"": {""tier"": ""anchor""}}")] // case-sensitive
    [InlineData(@"{""bad.source"": {""tier"": """"}}")]
    [InlineData(@"{""bad.source"": {""tier"": ""SOURCE_TIER_ANCHOR""}}")] // proto spelling, not config
    public void InvalidTierValueIsRejected(string document)
    {
        Assert.Throws<System.FormatException>(() => MustLoad(document));
    }

    [Theory]
    [InlineData(@"{""bad.source"": {""tier"": 123}}")]
    [InlineData(@"{""bad.source"": {""tier"": null}}")]
    [InlineData(@"{""bad.source"": {""tier"": [""ANCHOR""]}}")]
    public void NonStringTierIsRejected(string document)
    {
        Assert.Throws<System.FormatException>(() => MustLoad(document));
    }

    [Fact]
    public void MissingTierIsRejected()
    {
        Assert.Throws<System.FormatException>(() => MustLoad(@"{""bad.source"": {""note"": ""x""}}"));
    }

    [Theory]
    [InlineData(@"""not-an-object""")]
    [InlineData(@"[]")]
    [InlineData(@"42")]
    [InlineData(@"null")]
    [InlineData(@"not json at all")]
    public void NonObjectConfigIsRejected(string document)
    {
        Assert.Throws<System.FormatException>(() => MustLoad(document));
    }

    [Fact]
    public void NonObjectEntryIsRejected()
    {
        Assert.Throws<System.FormatException>(() => MustLoad(@"{""bad.source"": ""ANCHOR""}"));
    }

    // zod .passthrough(): a deployment may carry its own annotations alongside the tier.
    [Fact]
    public void ExtraKeysArePreserved()
    {
        var loaded = MustLoad(@"{""gateway.tool-calls"": {""tier"": ""ANCHOR"", ""extra"": ""metadata"", ""owner"": {""team"": ""platform""}}}");
        var entry = loaded["gateway.tool-calls"];
        Assert.Equal(SourceTierConstants.TierAnchor, entry.Tier);
        Assert.Equal("metadata", entry.Extra["extra"]);
        var owner = (IDictionary<string, object?>)entry.Extra["owner"]!;
        Assert.Equal("platform", owner["team"]);
        Assert.False(entry.Extra.ContainsKey("tier"));
    }

    [Fact]
    public void DottedHandlesAreValidKeys()
    {
        var loaded = MustLoad(@"{""gateway.tool-calls"": {""tier"": ""ANCHOR""}, ""agent.runtime.v2"": {""tier"": ""CONTRIBUTING""}}");
        Assert.Equal(SourceTier.SourceTierAnchor, SourceTierLoader.TierForHandle(loaded, "gateway.tool-calls"));
        Assert.Equal(SourceTier.SourceTierContributing, SourceTierLoader.TierForHandle(loaded, "agent.runtime.v2"));
    }

    // A host that decodes YAML/TOML hands the decoded map straight in.
    [Fact]
    public void ParseSourceTierConfigIsParserAgnostic()
    {
        var raw = new Dictionary<string, object?>
        {
            ["gateway.tool-calls"] = new Dictionary<string, object?> { ["tier"] = "ANCHOR", ["weight"] = 3L },
        };
        var loaded = SourceTierLoader.ParseSourceTierConfig(raw);
        Assert.Equal(SourceTier.SourceTierAnchor, SourceTierLoader.TierForHandle(loaded, "gateway.tool-calls"));
        Assert.Equal(3L, loaded["gateway.tool-calls"].Extra["weight"]);
    }

    [Fact]
    public void TierForHandleRejectsAHandBuiltUnknownTier()
    {
        var config = new SourceTierConfig { ["h"] = new SourceEntry { Tier = "BRONZE" } };
        Assert.Throws<System.FormatException>(() => SourceTierLoader.TierForHandle(config, "h"));
    }
}
