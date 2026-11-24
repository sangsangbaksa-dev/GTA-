using Godot;
using Harbor.Core.Sim;
using Harbor.Game.Adapters;

namespace Harbor.Game.Bridge;

/// <summary>Autoload-style root node that owns the engine-agnostic <see cref="SimWorld"/>
/// and drives it from Godot's frame loop. Views read state/events; input sends commands.</summary>
public partial class CoreHost : Node
{
    public SimWorld World { get; private set; } = null!;

    public override void _Ready()
    {
        World = new SimWorld(new GodotLog());
        // M1+: World.AddSystem(new WorldSystem(...)); World.AddSystem(new AgentSystem(...)); ...
        World.Log.Info("Harbor core started");
    }

    public override void _Process(double delta) => World.Update(delta);
}
