using Harbor.Core.Commands;
using Harbor.Core.Events;
using Harbor.Core.Ports;

namespace Harbor.Core.Sim;

/// <summary>Root of the simulation. Owns systems, the event bus and the command queue.
/// Hosted by game/scripts/Bridge/CoreHost.cs in Godot, or by any other host (tests, another game).</summary>
public sealed class SimWorld
{
    public EventBus Events { get; } = new();
    public FixedTickLoop Loop { get; }
    public ILog Log { get; }

    private readonly List<ISimSystem> _systems = new();
    private readonly Queue<PlayerCommand> _commands = new();
    private readonly Dictionary<Type, Action<PlayerCommand>> _commandHandlers = new();

    public SimWorld(ILog log, double tickRateHz = 30.0)
    {
        Log = log;
        Loop = new FixedTickLoop(tickRateHz);
    }

    public T AddSystem<T>(T system) where T : ISimSystem
    {
        _systems.Add(system);
        system.Initialize(this);
        return system;
    }

    public void HandleCommand<T>(Action<T> handler) where T : PlayerCommand
        => _commandHandlers[typeof(T)] = c => handler((T)c);

    public void Enqueue(PlayerCommand command) => _commands.Enqueue(command);

    /// <summary>Called by the host every frame.</summary>
    public void Update(double frameSeconds) => Loop.Advance(frameSeconds, Step);

    private void Step(double dt)
    {
        while (_commands.TryDequeue(out var cmd))
        {
            if (_commandHandlers.TryGetValue(cmd.GetType(), out var h)) h(cmd);
            else Log.Warn($"Unhandled command {cmd.GetType().Name}");
        }
        foreach (var s in _systems) s.Step(dt);
    }
}
