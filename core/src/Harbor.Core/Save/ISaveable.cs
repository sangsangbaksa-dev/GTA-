namespace Harbor.Core.Save;

/// <summary>Implemented by every system that persists state. Each section is versioned;
/// bump <see cref="SaveVersion"/> and add a migration in Save/Migrations when the format changes.</summary>
public interface ISaveable
{
    string SaveKey { get; }
    int SaveVersion { get; }
    string Serialize();
    void Deserialize(string data, int version);
}
