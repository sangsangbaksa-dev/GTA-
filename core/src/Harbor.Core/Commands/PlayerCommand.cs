namespace Harbor.Core.Commands;

/// <summary>The only way the engine layer changes simulation state.
/// Keeping all writes as commands keeps the core deterministic and network-ready.</summary>
public abstract record PlayerCommand(int PlayerId);
