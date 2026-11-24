namespace Harbor.Core.Ports;

/// <summary>Logging port. Godot adapter: GD.Print. Headless: Console.</summary>
public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}
