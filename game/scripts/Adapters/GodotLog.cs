using Godot;
using Harbor.Core.Ports;

namespace Harbor.Game.Adapters;

public sealed class GodotLog : ILog
{
    public void Info(string message) => GD.Print($"[Harbor] {message}");
    public void Warn(string message) => GD.PushWarning($"[Harbor] {message}");
    public void Error(string message) => GD.PushError($"[Harbor] {message}");
}
