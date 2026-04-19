namespace VicoScreenShare.Protocol;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Canonical <see cref="JsonSerializerOptions"/> for every wire message.
/// Every serializer on both client and server must use <see cref="Options"/> so the
/// field-naming convention cannot silently flip between callers.
/// </summary>
public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
