namespace Harbor.Core.Ports;

/// <summary>Plain System.IO implementation for the dedicated server, tools and tests.
/// Paths are relative to <paramref name="root"/>.</summary>
public sealed class DiskFileSystem(string root) : IFileSystem
{
    private string Full(string p) => Path.Combine(root, p);
    public bool Exists(string path) => File.Exists(Full(path));
    public string ReadAllText(string path) => File.ReadAllText(Full(path));
    public void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(path))!);
        File.WriteAllText(Full(path), contents);
    }

    public IReadOnlyList<string> ListFiles(string directory, string extension)
    {
        var full = Full(directory);
        if (!Directory.Exists(full)) return Array.Empty<string>();
        return Directory.GetFiles(full, "*" + extension)
            .Select(f => Path.Combine(directory, Path.GetFileName(f)).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
    }
}
