namespace Harbor.Core.Sim;

/// <summary>A core module that updates every fixed tick (World, Agents, Heat, Missions, ...).</summary>
public interface ISimSystem
{
    void Initialize(SimWorld world);
    void Step(double dt);
}
