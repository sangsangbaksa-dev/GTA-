using Godot;
using Harbor.Core.Ports;
using Harbor.Game.Adapters;

namespace Harbor.Game.Bridge;

/// <summary>Where data/ lives: next to the Godot project in the repo (editor / dev runs), or packed into
/// the game assembly in an exported build.</summary>
public static class DataPaths
{
    private static IFileSystem? _fs;

    public static IFileSystem FileSystem()
    {
        if (_fs != null) return _fs;
        if (FileAccess.FileExists("res://data/combat/combat.json")) return _fs = new GodotFileSystem("res://");
        var repo = ProjectSettings.GlobalizePath("res://").TrimEnd('/') + "/..";
        var disk = new DiskFileSystem(System.IO.Path.GetFullPath(repo));
        return _fs = disk.Exists("data/combat/combat.json") ? disk : new EmbeddedGameFiles();
    }
}
