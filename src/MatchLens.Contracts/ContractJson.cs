using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatchLens.Contracts;

/// <summary>The one JSON shape every service and the frontend use: camelCase, enums as camelCase strings, nulls omitted.</summary>
public static class ContractJson
{
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
