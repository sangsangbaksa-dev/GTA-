using System.Reflection;

namespace Harbor.Core.Ports;

/// <summary>Read-only authored data bundled with a self-contained game client.</summary>
public class EmbeddedFileSystem(Assembly assembly) : IFileSystem
{
    private readonly IReadOnlyDictionary<string, string> _paths = assembly.GetManifestResourceNames()
        .ToDictionary(path => path.Replace('\\', '/'), StringComparer.Ordinal);

    public bool Exists(string path) => _paths.ContainsKey(path);

    public string ReadAllText(string path)
    {
        if (!_paths.TryGetValue(path, out var resource)) throw new FileNotFoundException("Embedded game data is missing", path);
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException("Embedded game data is missing", path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void WriteAllText(string path, string contents) => throw new NotSupportedException("Embedded game data is read-only");

    public IReadOnlyList<string> ListFiles(string directory, string extension)
    {
        var prefix = directory.TrimEnd('/') + "/";
        return _paths.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)
            && path.EndsWith(extension, StringComparison.Ordinal)
            && path.IndexOf('/', prefix.Length) < 0).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }
}
