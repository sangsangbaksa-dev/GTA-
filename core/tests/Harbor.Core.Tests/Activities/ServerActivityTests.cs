using Harbor.Core.Activities;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Server;
using Xunit;

namespace Harbor.Core.Tests.Activities;

/// <summary>A1 completion criterion: a real contract from data/ played to the end through the server over the wire.</summary>
public class ServerActivityTests
{
    private sealed class Rig
    {
        public readonly InMemoryNetwork Net = new(9) { LatencySeconds = 0.06, JitterSeconds = 0.01, LossRate = 0.05 };
        public readonly GameServer Server;
        public readonly ClientSession Client;
        public readonly List<(ushort Type, byte[] Payload)> Events = new();
        public bool Interact;

        public Rig()
        {
            var ep = Net.CreateServer();
            Server = new GameServer(Repo.DataWithoutNpcs, ep, new NullLog());
            Client = new ClientSession(Net.Connect(ep), "rook");
            Run(1);
        }

        public void Run(double seconds)
        {
            var dt = Server.World.Loop.StepSeconds;
            for (var t = 0.0; t < seconds; t += dt)
            {
                Net.Advance(dt);
                Client.Poll();
                while (Client.Events.TryDequeue(out var e)) Events.Add(e);
                Client.Tick(0, 0, 0, 0, Interact ? InputButtons.Interact : InputButtons.None);
                Server.Update(dt);
            }
        }

        public void TeleportTo(double x, double z) => Server.World.Actors.Get(Client.LocalActorId).Position = new Vec3d(x, 0, z);
    }

    [Fact]
    public void Register_Run_Plays_To_Completion_Over_The_Network()
    {
        var rig = new Rig();
        var def = Repo.Data.Activities.Contracts["ct_register_run"];
        rig.Client.SendRequest(RequestType.ContractStart, new RequestBody(1, A: def.Id).Encode());
        rig.Run(0.5);
        Assert.True(rig.Server.Activities.IsBusy(rig.Client.LocalActorId));

        rig.TeleportTo(300, -120);       // reach the store
        rig.Run(0.5);
        rig.TeleportTo(302, -118);       // hold interact at the register
        rig.Interact = true;
        rig.Run(def.Objectives[1].Seconds + 0.5);
        rig.Interact = false;
        Assert.True(rig.Server.Heat.LevelOf(rig.Client.LocalActorId) >= 3); // robbery is loud
        rig.TeleportTo(300, -120);       // hold out
        rig.Run(def.Objectives[2].Seconds + 0.5);
        rig.TeleportTo(600, -120);       // escape
        rig.Run(1);

        var updates = rig.Events.Where(e => e.Type == WireEventType.ActivityUpdate).Select(e => WireEvents.ReadObjective(e.Payload)).ToList();
        Assert.Equal(new[] { "obj.reach_store", "obj.empty_register", "obj.hold_out", "obj.lose_police" },
            updates.Where(u => u.Progress == 0).Select(u => u.Label).Distinct());
        Assert.Contains(updates, u => u.Label == "obj.hold_out" && u.Progress >= 0.5);

        var result = WireEvents.ReadResult(rig.Events.Single(e => e.Type == WireEventType.ActivityResult).Payload);
        Assert.True(result.Success, result.Reason);
        Assert.InRange(result.Paid, def.Reward.Min, def.Reward.Max);
        var balance = RequestCodec.ReadBalance(rig.Events.Last(e => e.Type == WireEventType.Balance).Payload);
        Assert.Equal(result.Paid, balance.Wallet);
        Assert.False(rig.Server.Activities.IsBusy(rig.Client.LocalActorId));
    }

    [Fact]
    public void Busy_Player_Cannot_Go_Passive_And_Bad_Contract_Is_Rejected()
    {
        var rig = new Rig();
        rig.Client.SendRequest(RequestType.ContractStart, new RequestBody(1, A: "no_such_contract").Encode());
        rig.Client.SendRequest(RequestType.ContractStart, new RequestBody(2, A: "ct_register_run").Encode());
        rig.Run(0.5);
        rig.TeleportTo(-420, 310); // free safehouse
        rig.Client.SendRequest(RequestType.TogglePassive, new RequestBody(3).Encode());
        rig.Run(0.5);
        var rejected = rig.Events.Where(e => e.Type == WireEventType.RequestRejected).Select(e => RequestCodec.ReadRejected(e.Payload)).ToList();
        Assert.Contains(rejected, r => r.Nonce == 1 && r.Reason == "unknown_contract");
        Assert.Contains(rejected, r => r.Nonce == 3 && r.Reason == "in_activity");
    }

    [Fact]
    public void Shipped_Activity_Data_Loads_And_Is_Consistent()
    {
        var a = Repo.Data.Activities;
        Assert.Equal(3, a.Heists.Count);
        Assert.Equal(9, a.Turf.Zones.Count);
        Assert.True(a.SupplyDrops.Sites.Count >= 2);
        foreach (var h in a.Heists.Values)
        {
            var finale = a.Contracts[h.Finale];
            var take = finale.Objectives.Where(o => o.Type == Harbor.Core.Missions.ObjectiveType.Deliver).Sum(o => o.ValueEach * o.Count);
            Assert.True(take > 0, h.Id);
        }
        // CONTENT_DESIGN §6.2 totals
        long Take(string id) => a.Contracts[a.Heists[id].Finale].Objectives.Sum(o => o.ValueEach * o.Count);
        Assert.Equal(60000, Take("hs_saltworks_payday"));
        Assert.Equal(110000, Take("hs_blue_pier"));
        Assert.Equal(240000, Take("hs_lantern_trust"));
    }
}
