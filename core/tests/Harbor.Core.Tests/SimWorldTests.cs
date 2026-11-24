using Harbor.Core.Commands;
using Harbor.Core.Events;
using Harbor.Core.Ports;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests;

public class SimWorldTests
{
    private sealed class NullLog : ILog
    {
        public void Info(string m) { }
        public void Warn(string m) { }
        public void Error(string m) { }
    }

    private sealed record Honk(int PlayerId) : PlayerCommand(PlayerId);
    private sealed record Honked(int PlayerId) : ISimEvent;

    private sealed class CountingSystem : ISimSystem
    {
        public int Steps;
        public void Initialize(SimWorld world) { }
        public void Step(double dt) => Steps++;
    }

    [Fact]
    public void FixedTick_RunsExpectedStepCount()
    {
        var world = new SimWorld(new NullLog(), tickRateHz: 30);
        var sys = world.AddSystem(new CountingSystem());

        world.Update(0.1); // 3 steps at 30Hz

        Assert.Equal(3, sys.Steps);
        Assert.Equal(3, world.Loop.Tick);
    }

    [Fact]
    public void Commands_AreProcessedOnTick_AndCanPublishEvents()
    {
        var world = new SimWorld(new NullLog());
        world.HandleCommand<Honk>(c => world.Events.Publish(new Honked(c.PlayerId)));
        Honked? received = null;
        world.Events.Subscribe<Honked>(e => received = e);

        world.Enqueue(new Honk(7));
        Assert.Null(received);          // not processed until a tick runs

        world.Update(world.Loop.StepSeconds);
        Assert.Equal(new Honked(7), received);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var bus = new EventBus();
        var count = 0;
        var sub = bus.Subscribe<Honked>(_ => count++);
        bus.Publish(new Honked(1));
        sub.Dispose();
        bus.Publish(new Honked(1));
        Assert.Equal(1, count);
    }
}
