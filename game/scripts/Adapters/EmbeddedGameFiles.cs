using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Harbor.Core.Ports;

namespace Harbor.Game.Adapters;

internal sealed class EmbeddedGameFiles : IFileSystem
{
    private static readonly System.Reflection.Assembly Assembly = typeof(EmbeddedGameFiles).Assembly;
    private static readonly IReadOnlyDictionary<string, string> Paths = Assembly.GetManifestResourceNames()
        .ToDictionary(path => path.Replace('\\', '/'), StringComparer.Ordinal);

    public bool Exists(string path) => Paths.ContainsKey(path);

    public string ReadAllText(string path)
    {
        if (!Paths.TryGetValue(path, out var resource)) throw new FileNotFoundException("Embedded game data is missing", path);
        using var stream = Assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException("Embedded game data is missing", path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void WriteAllText(string path, string contents) => throw new NotSupportedException("Embedded game data is read-only");

    public IReadOnlyList<string> ListFiles(string directory, string extension)
    {
        var prefix = directory.TrimEnd('/') + "/";
        return Paths.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)
            && p.EndsWith(extension, StringComparison.Ordinal)
            && p.IndexOf('/', prefix.Length) < 0).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }
}
