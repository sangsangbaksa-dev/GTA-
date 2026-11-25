using System.Collections.Generic;
using System.Text.Json;
using Harbor.Core.Ports;

namespace Harbor.Game.UI;

/// <summary>UI strings from data/locale/ko.json (keys are the ids the server sends: obj.*, notice.*, reject.*).</summary>
public static class Locale
{
    private static Dictionary<string, string> _strings = new();

    public static void Load(IFileSystem fs, string lang = "ko")
    {
        var path = $"data/locale/{lang}.json";
        if (!fs.Exists(path)) return;
        _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(fs.ReadAllText(path)) ?? new();
    }

    public static string T(string key) => _strings.TryGetValue(key, out var v) ? v : key;
    public static string T(string prefix, string key) => _strings.TryGetValue($"{prefix}.{key}", out var v) ? v : key;
}
