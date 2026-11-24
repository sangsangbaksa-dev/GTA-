using Harbor.Core.Ports;

namespace Harbor.Core.Tests;

internal sealed class NullLog : ILog
{
    public readonly List<string> Warnings = new();
    public void Info(string m) { }
    public void Warn(string m) => Warnings.Add(m);
    public void Error(string m) => Warnings.Add(m);
}
