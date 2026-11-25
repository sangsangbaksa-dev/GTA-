using Harbor.Core.Ports;

namespace Harbor.Core.Tests;

internal sealed class NullLog : ILog
{
    public readonly List<string> Warnings = new();
    public void Info(string m) { }
    public void Warn(string m) => Warnings.Add(m);
    public void Error(string m) => Warnings.Add(m);
}

internal static class Repo
{
    /// <summary>Repository root (contains data/), found by walking up from the test binary.</summary>
    public static string Root
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "data", "weapons"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("repo root with data/ not found");
        }
    }

    private static Harbor.Core.Data.GameData? _data;
    public static Harbor.Core.Data.GameData Data => _data ??= Harbor.Core.Data.GameData.Load(new DiskFileSystem(Root));

    private static Harbor.Core.Data.GameData? _quiet;
    /// <summary>Same data without NPCs, for rule tests that must not be interrupted by gangs or police.</summary>
    public static Harbor.Core.Data.GameData DataWithoutNpcs => _quiet ??= new Harbor.Core.Data.GameData
    {
        Weapons = Data.Weapons, Combat = Data.Combat, Materials = Data.Materials, Economy = Data.Economy, Heat = Data.Heat,
        Activities = Data.Activities, Map = Data.Map, Travel = Data.Travel, Agents = Harbor.Core.Agents.AgentData.Empty,
    };
}
