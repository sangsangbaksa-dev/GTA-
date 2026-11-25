namespace Harbor.Core.Ports;

/// <summary>File access port (saves, data). Godot adapter maps to user:// and res://.</summary>
public interface IFileSystem
{
    bool Exists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    /// <summary>File paths directly inside <paramref name="directory"/> ending with <paramref name="extension"/> (e.g. ".json"), sorted.</summary>
    IReadOnlyList<string> ListFiles(string directory, string extension);
}
