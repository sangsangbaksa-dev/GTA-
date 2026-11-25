using Harbor.Core.Geometry;
using Harbor.Core.Net;
using Harbor.Core.Sim;

namespace Harbor.Core.Tests.Net;

/// <summary>One authoritative server + N clients over an <see cref="InMemoryNetwork"/>, stepped in lockstep ticks.</summary>
internal sealed class NetHarness
{
    public readonly InMemoryNetwork Network;
    public readonly SimWorld World;
    public readonly ServerSession Server;
    public readonly List<ClientSession> Clients = new();
    public readonly StaticWorld Static;
    public readonly InMemoryNetwork.Endpoint ServerEndpoint;
    public double Dt => World.Loop.StepSeconds;

    public NetHarness(double latency = 0.05, double loss = 0, double jitter = 0, StaticWorld? world = null, ServerConfig? config = null)
    {
        Network = new InMemoryNetwork(seed: 7) { LatencySeconds = latency, LossRate = loss, JitterSeconds = jitter };
        World = new SimWorld(new NullLog());
        Static = world ?? new StaticWorld();
        ServerEndpoint = Network.CreateServer();
        Server = World.AddSystem(new ServerSession(ServerEndpoint, config, staticWorld: Static));
    }

    public ClientSession AddClient(string name)
    {
        var c = new ClientSession(Network.Connect(ServerEndpoint), name, staticWorld: Static);
        Clients.Add(c);
        return c;
    }

    /// <summary>Advance one tick. <paramref name="input"/> supplies each client's input (null = idle).</summary>
    public void Step(Func<int, ClientSession, (double r, double f, double yaw, InputButtons b)>? input = null)
    {
        Network.Advance(Dt);
        for (var i = 0; i < Clients.Count; i++)
        {
            var c = Clients[i];
            c.Poll();
            var (r, f, yaw, b) = input?.Invoke(i, c) ?? (0, 0, 0, InputButtons.None);
            c.Tick(r, f, yaw, 0, b);
        }
        World.Update(Dt);
    }

    public void Run(int ticks, Func<int, ClientSession, (double r, double f, double yaw, InputButtons b)>? input = null)
    {
        for (var t = 0; t < ticks; t++) Step(input);
    }

    public void JoinAll(int maxTicks = 30)
    {
        for (var t = 0; t < maxTicks && Clients.Any(c => !c.IsJoined); t++) Step();
    }
}
