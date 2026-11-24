namespace Harbor.Core.Ports;

/// <summary>File access port (saves, data). Godot adapter maps to user:// and res://.</summary>
public interface IFileSystem
{
    bool Exists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
}
