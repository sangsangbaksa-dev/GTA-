using Harbor.Core.Combat;
using Harbor.Core.Economy;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Server;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests.Agents;

public class ServerAgentTests
{
    private sealed class Rig
    {
        public readonly InMemoryNetwork Net = new(3) { LatencySeconds = 0.03 };
        public readonly GameServer Server;
        public readonly ClientSession Client;
        public readonly List<(ushort Type, byte[] Payload)> Events = new();

        public Rig()
        {
            var ep = Net.CreateServer();
            Server = new GameServer(Repo.Data, ep, new NullLog());
            Client = new ClientSession(Net.Connect(ep), "rook");
            Run(0.5);
        }

        public void Run(double seconds)
        {
            var dt = Server.World.Loop.StepSeconds;
            for (var t = 0.0; t < seconds; t += dt)
            {
                Net.Advance(dt);
                Client.Poll();
                while (Client.Events.TryDequeue(out var e)) Events.Add(e);
                Client.Tick(0, 0, 0, 0, InputButtons.None);
                Server.Update(dt);
            }
        }

        public Actor Me => Server.World.Actors.Get(Client.LocalActorId);
    }

    [Fact]
    public void Joining_Player_Gets_Actor_Info_And_Spawns_At_The_Safehouse()
    {
        var rig = new Rig();
        var infos = rig.Events.Where(e => e.Type == WireEventType.ActorInfo).Select(e => WireEvents.ReadActorInfo(e.Payload)).ToList();
        Assert.Contains(infos, i => i.Id == rig.Client.LocalActorId && i.Model == "chr_player_rook" && i.Name == "rook");
        Assert.True(Vec3d.Distance(rig.Me.Position, Repo.Data.Map.SpawnPoint) < 15);
    }

    [Fact]
    public void Npcs_Near_Players_Are_Replicated_With_Their_Model()
    {
        var rig = new Rig();
        var zone = Repo.Data.Agents.Encounters.First(e => e.Id == "enc_tz_mill_yard");
        rig.Me.Position = zone.Center + new Vec3d(0, 0, 150);
        rig.Run(1.0);
        var infos = rig.Events.Where(e => e.Type == WireEventType.ActorInfo).Select(e => WireEvents.ReadActorInfo(e.Payload)).ToList();
        Assert.Contains(infos, i => i.Kind == ActorKind.Npc && i.Model == "chr_enemy_brawler");
        var npcIds = infos.Where(i => i.Kind == ActorKind.Npc).Select(i => i.Id).ToHashSet();
        Assert.Contains(rig.Client.Remote.Ids, id => npcIds.Contains(id));
    }

    [Fact]
    public void Killing_A_Gang_Member_Drops_Cash_And_Raises_Heat()
    {
        var rig = new Rig();
        var npc = rig.Server.Agents.Spawn("saltline_gunman", rig.Me.Position + new Vec3d(0, 0, 60));
        rig.Server.Combat.Kill(npc.Actor, rig.Me.Id, "test", false);
        rig.Run(0.2);
        Assert.Contains(rig.Events, e => e.Type == WireEventType.CashDropped);
        Assert.True(rig.Server.Heat.HeatOf(rig.Me.Id) > 0);
        // walk over the bag
        var bag = rig.Server.World.Actors.All.Single(a => a.Kind == ActorKind.Loot);
        var p = rig.Server.Economy.ProfileOf(rig.Me.Id)!;
        rig.Me.Position = bag.Position;
        rig.Run(0.3);
        Assert.InRange(rig.Server.Economy.Wallet(p.Id), Repo.Data.Agents.Archetypes["saltline_gunman"].CashDrop[0],
            Repo.Data.Agents.Archetypes["saltline_gunman"].CashDrop[1]);
    }

    [Fact]
    public void Wanted_Player_Draws_Police()
    {
        var rig = new Rig();
        rig.Server.Heat.Report(rig.Me.Id, "robbery"); // level 2
        var lvl = rig.Server.Heat.LevelOf(rig.Me.Id);
        var response = Repo.Data.Heat.Levels[lvl].ResponseSeconds;
        rig.Run(response + 1);
        Assert.True(rig.Server.Agents.Police.UnitsFor(rig.Me.Id) > 0);
    }

    [Fact]
    public void Fast_Travel_Costs_Clean_Money_And_Is_Blocked_While_Wanted()
    {
        var rig = new Rig();
        var p = rig.Server.Economy.ProfileOf(rig.Me.Id)!;
        var bank = rig.Server.Economy.Bank(p.Id);
        rig.Client.SendRequest(RequestType.FastTravel, new RequestBody(7, A: "tp_harbor_front").Encode());
        rig.Run(0.3);
        Assert.True(Vec3d.Distance(rig.Me.Position, new Vec3d(260, 0, -200)) < 6);
        Assert.Equal(bank - Repo.Data.Travel.Cost, rig.Server.Economy.Bank(p.Id));
        Assert.Contains(rig.Events, e => e.Type == WireEventType.Travelled);

        rig.Server.Heat.Report(rig.Me.Id, "murder");
        rig.Client.SendRequest(RequestType.FastTravel, new RequestBody(8, A: "tp_old_mill").Encode());
        rig.Run(0.3);
        var rejected = rig.Events.Where(e => e.Type == WireEventType.RequestRejected).Select(e => RequestCodec.ReadRejected(e.Payload)).ToList();
        Assert.Contains(rejected, r => r.Nonce == 8 && (r.Reason == "wanted" || r.Reason == "cooldown"));
    }
}
