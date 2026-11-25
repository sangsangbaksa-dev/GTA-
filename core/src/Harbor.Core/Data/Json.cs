using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Core.Ports;

namespace Harbor.Core.Data;

public sealed class DataException(string message) : Exception(message);

/// <summary>Shared JSON settings for data/ files (camelCase, enums as camelCase strings, comments allowed).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T Read<T>(IFileSystem fs, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(fs.ReadAllText(path), Options)
                   ?? throw new DataException($"{path}: empty document");
        }
        catch (JsonException e)
        {
            throw new DataException($"{path}: {e.Message}");
        }
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new DataException(message);
    }
}
