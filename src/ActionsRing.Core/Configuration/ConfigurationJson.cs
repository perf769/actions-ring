using System.Text.Json;
using System.Text.Json.Serialization;

namespace ActionsRing.Core.Configuration;

/// <summary>Canonical JSON settings used for durable configuration files.</summary>
public static class ConfigurationJson
{
    private static readonly JsonSerializerOptions CanonicalOptions = CreateOptions(writeIndented: true);
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(writeIndented: false);
    private static readonly JsonSerializerOptions CloneOptions = CreateCloneOptions();

    public static JsonSerializerOptions Options => new(CanonicalOptions);

    public static string Serialize(ActionsRingConfiguration configuration, bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return JsonSerializer.Serialize(configuration, writeIndented ? CanonicalOptions : CompactOptions);
    }

    public static ActionsRingConfiguration Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<ActionsRingConfiguration>(json, CanonicalOptions)
               ?? throw new JsonException("Configuration document is JSON null.");
    }

    public static ActionsRingConfiguration Clone(ActionsRingConfiguration configuration) =>
        JsonSerializer.Deserialize<ActionsRingConfiguration>(
            JsonSerializer.Serialize(configuration, CloneOptions),
            CloneOptions) ?? throw new JsonException("Configuration document is JSON null.");

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 128,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static JsonSerializerOptions CreateCloneOptions()
    {
        var options = CreateOptions(writeIndented: false);
        options.Converters.Clear();
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
        options.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        return options;
    }
}
