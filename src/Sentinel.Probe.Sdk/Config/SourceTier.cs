using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Emet.Sentinel.Model.V1;

namespace Sentinel.Probe.Sdk.Config;

// Source-tier configuration — the .NET analog of sdk/go/config/sourcetier.go (mirrors
// sdk/typescript's schema.ts + source-tier.ts, collapsed into one file like the Go reference).
//
// Tier is NEVER hard-coded: there is no list of anchor handles anywhere in this SDK. The
// deployment declares tiers, the Probe reads them at init and looks up its own source_handle. An
// undeclared handle is an error, never a silent default — defaulting would quietly demote or
// promote a source's evidentiary weight.

/// <summary>Tier values accepted in configuration (deployment-facing spellings, not proto names).</summary>
public static class SourceTierConstants
{
    public const string TierAnchor = "ANCHOR";
    public const string TierContributing = "CONTRIBUTING";
}

/// <summary>One source's declared configuration. Extra holds every other key, preserved.</summary>
public sealed class SourceEntry
{
    public string Tier { get; init; } = "";
    public Dictionary<string, object?> Extra { get; init; } = new();
}

/// <summary>Maps source_handle to its declared entry.</summary>
public interface ISourceTierConfig : IDictionary<string, SourceEntry> { }

/// <summary>A mutable, in-memory source-tier config (the common case).</summary>
public sealed class SourceTierConfig : Dictionary<string, SourceEntry>, ISourceTierConfig { }

public static class SourceTierLoader
{
    /// <summary>Parses JSON configuration bytes.</summary>
    public static ISourceTierConfig LoadSourceTierConfig(string raw)
    {
        using var doc = ParseJson(raw);
        var root = ElementToObject(doc.RootElement);
        if (root is not IDictionary<string, object?> document)
        {
            throw new FormatException($"source-tier: config must be an object, got {Describe(root)}");
        }
        return ParseSourceTierConfig(document);
    }

    /// <summary>
    /// Validates configuration that has already been decoded, so a host can use whatever parser it
    /// likes — the reference's loadSourceTierConfig takes `unknown` for the same reason. YAML
    /// decoding stays out of the SDK: the host decodes and calls this.
    /// </summary>
    public static ISourceTierConfig ParseSourceTierConfig(IDictionary<string, object?> raw)
    {
        var config = new SourceTierConfig();
        foreach (var (handle, value) in raw)
        {
            if (value is not IDictionary<string, object?> entry)
            {
                throw new FormatException($"source-tier: entry for \"{handle}\" must be an object, got {Describe(value)}");
            }
            if (!entry.TryGetValue("tier", out var tierValue) || tierValue == null)
            {
                throw new FormatException($"source-tier: entry for \"{handle}\" has no tier");
            }
            if (tierValue is not string tier)
            {
                throw new FormatException($"source-tier: tier for \"{handle}\" must be a string, got {Describe(tierValue)}");
            }
            if (tier != SourceTierConstants.TierAnchor && tier != SourceTierConstants.TierContributing)
            {
                throw new FormatException(
                    $"source-tier: unknown tier \"{tier}\" for \"{handle}\", want \"{SourceTierConstants.TierAnchor}\" or \"{SourceTierConstants.TierContributing}\"");
            }

            var extra = new Dictionary<string, object?>();
            foreach (var (key, extraValue) in entry)
            {
                if (key != "tier") extra[key] = extraValue;
            }
            config[handle] = new SourceEntry { Tier = tier, Extra = extra };
        }
        return config;
    }

    private static JsonDocument ParseJson(string raw)
    {
        try { return JsonDocument.Parse(raw); }
        catch (JsonException ex) { throw new FormatException($"source-tier: invalid JSON: {ex.Message}", ex); }
    }

    /// <summary>
    /// Resolves the proto SourceTier for a source_handle. An undeclared handle is an error. It is
    /// never SOURCE_TIER_UNSPECIFIED and never a default: a source whose tier nobody declared has no
    /// business claiming one.
    /// </summary>
    public static SourceTier TierForHandle(ISourceTierConfig config, string sourceHandle)
    {
        if (!config.TryGetValue(sourceHandle, out var entry))
        {
            throw new FormatException($"source-tier: no entry for source_handle \"{sourceHandle}\"");
        }
        return entry.Tier switch
        {
            SourceTierConstants.TierAnchor => SourceTier.Anchor,
            SourceTierConstants.TierContributing => SourceTier.Contributing,
            // Unreachable via the constructors, but a caller can build a config literal by hand.
            _ => throw new FormatException($"source-tier: unknown tier \"{entry.Tier}\" for source_handle \"{sourceHandle}\""),
        };
    }

    // --- JSON -> object tree (mirrors Go's map[string]any from encoding/json) -------------

    private static object? ElementToObject(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var prop in el.EnumerateObject()) dict[prop.Name] = ElementToObject(prop.Value);
                return dict;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray()) list.Add(ElementToObject(item));
                return list;
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out long l)) return l;
                return el.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        IDictionary<string, object?> => "object",
        IList<object?> => "array",
        string => "string",
        bool => "bool",
        long => "number",
        double => "number",
        _ => value.GetType().ToString(),
    };
}
