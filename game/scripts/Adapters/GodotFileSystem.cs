using Godot;
using Harbor.Core.Ports;

namespace Harbor.Game.Adapters;

/// <summary>Maps core file access to Godot paths (res:// for data, user:// for saves).</summary>
public sealed class GodotFileSystem : IFileSystem
{
    public bool Exists(string path) => FileAccess.FileExists(path);

    public string ReadAllText(string path)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read)
            ?? throw new System.IO.IOException($"Cannot open {path}: {FileAccess.GetOpenError()}");
        return f.GetAsText();
    }

    public void WriteAllText(string path, string contents)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write)
            ?? throw new System.IO.IOException($"Cannot write {path}: {FileAccess.GetOpenError()}");
        f.StoreString(contents);
    }

    public System.Collections.Generic.IReadOnlyList<string> ListFiles(string directory, string extension)
    {
        var files = new System.Collections.Generic.List<string>();
        foreach (var name in DirAccess.GetFilesAt(directory))
            if (name.EndsWith(extension, System.StringComparison.Ordinal)) files.Add(directory.TrimEnd('/') + "/" + name);
        files.Sort(System.StringComparer.Ordinal);
        return files;
    }
}
