using Godot;
using Harbor.Core.Ports;
using Harbor.Game.Adapters;

namespace Harbor.Game.Bridge;

/// <summary>Where data/ lives: next to the Godot project in the repo (editor / dev runs), or packed into
/// res://data in an exported build (copied by the export preset, see game/README.md).</summary>
public static class DataPaths
{
    private static IFileSystem? _fs;

    public static IFileSystem FileSystem()
    {
        if (_fs != null) return _fs;
        if (FileAccess.FileExists("res://data/combat/combat.json")) return _fs = new GodotFileSystem("res://");
        var repo = ProjectSettings.GlobalizePath("res://").TrimEnd('/') + "/..";
        return _fs = new DiskFileSystem(System.IO.Path.GetFullPath(repo));
    }
}
