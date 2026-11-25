using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Harbor.Core.Ports;

namespace Harbor.Game.Adapters;

internal sealed class EmbeddedGameFiles : IFileSystem
{
    private static readonly System.Reflection.Assembly Assembly = typeof(EmbeddedGameFiles).Assembly;
    private readonly string[] _paths = Assembly.GetManifestResourceNames();

    public bool Exists(string path) => Array.IndexOf(_paths, path) >= 0;

    public string ReadAllText(string path)
    {
        using var stream = Assembly.GetManifestResourceStream(path)
            ?? throw new FileNotFoundException("Embedded game data is missing", path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void WriteAllText(string path, string contents) => throw new NotSupportedException("Embedded game data is read-only");

    public IReadOnlyList<string> ListFiles(string directory, string extension)
    {
        var prefix = directory.TrimEnd('/') + "/";
        return _paths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)
            && p.EndsWith(extension, StringComparison.Ordinal)
            && p.IndexOf('/', prefix.Length) < 0).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }
}
