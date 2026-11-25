using Harbor.Core.Agents;
using Harbor.Core.Combat;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests.Agents;

/// <summary>Rule harness: combat + agents on a bare world (no network, no generated encounters).</summary>
internal sealed class AgentArena
{
    public readonly SimWorld World = new(new NullLog());
    public readonly StaticWorld Static = new();
    public readonly CombatSystem Combat;
    public readonly AgentSystem Agents;
    public readonly List<ActorDamaged> Damage = new();
    public readonly List<NpcKilled> NpcDeaths = new();
    public int Wanted;

    public AgentArena(List<EncounterDef>? encounters = null)
    {
        Combat = World.AddSystem(new CombatSystem(Repo.Data.Combat, Repo.Data.Materials, Repo.Data.Weapons, Static, null));
        var data = Repo.Data.Agents with { Encounters = encounters ?? new List<EncounterDef>() };
        Agents = World.AddSystem(new AgentSystem(data, Combat, Static));
        Agents.WantedLevel = _ => Wanted;
        World.Events.Subscribe<ActorDamaged>(Damage.Add);
        World.Events.Subscribe<NpcKilled>(NpcDeaths.Add);
    }

    public Actor Player(Vec3d pos)
    {
        var a = World.Actors.Spawn(ActorKind.Player, pos);
        Combat.Register(a);
        return a;
    }

    public void Run(double seconds)
    {
        var dt = World.Loop.StepSeconds;
        for (var t = 0.0; t < seconds; t += dt) World.Update(dt);
    }

    public int DamageTo(int id) => Damage.Where(d => d.VictimId == id).Sum(d => d.HealthLost + d.ArmorLost);
}

public class AgentTests
{
    [Fact]
    public void Gunman_Spots_And_Shoots_A_Visible_Player()
    {
        var arena = new AgentArena();
        var p = arena.Player(new Vec3d(0, 0, -15));
        var g = arena.Agents.Spawn("velvet_gunman", Vec3d.Zero);
        g.Actor.Move.Yaw = 0; // facing -Z, towards the player
        arena.Run(0.5);
        Assert.Equal(p.Id, g.Target);
        arena.Run(3);
        Assert.True(arena.DamageTo(p.Id) > 0);
    }

    [Fact]
    public void Wall_Blocks_Sight()
    {
        var arena = new AgentArena();
        arena.Static.Add(Aabb.FromCenter(new Vec3d(0, 2, -7), new Vec3d(10, 4, 1)), SurfaceMaterial.Concrete);
        var p = arena.Player(new Vec3d(0, 0, -15));
        var g = arena.Agents.Spawn("velvet_gunman", Vec3d.Zero);
        g.Actor.Move.Yaw = 0;
        arena.Run(3);
        Assert.Equal(-1, g.Target);
        Assert.Equal(0, arena.DamageTo(p.Id));
    }

    [Fact]
    public void Player_Behind_The_Npc_Is_Not_Noticed_Until_Shots()
    {
        var arena = new AgentArena();
        var p = arena.Player(new Vec3d(0, 0, 20));
        var g = arena.Agents.Spawn("velvet_gunman", Vec3d.Zero);
        g.Actor.Move.Yaw = 0; // facing away from the player
        arena.Run(0.5);
        Assert.Equal(-1, g.Target);
        arena.Combat.ApplyDamage(g.Actor, p, 10, HitZone.Torso, false, "test");
        arena.Run(0.2);
        Assert.Equal(p.Id, g.Target);
    }

    [Fact]
    public void Brawler_Charges_And_Hits_In_Melee()
    {
        var arena = new AgentArena();
        var p = arena.Player(new Vec3d(0, 0, -12));
        var b = arena.Agents.Spawn("saltline_brawler", Vec3d.Zero);
        b.Actor.Move.Yaw = 0;
        arena.Run(5);
        Assert.True(Vec3d.Distance(b.Actor.Position, p.Position) < 2.5);
        Assert.Contains(arena.Damage, d => d.VictimId == p.Id && d.Source == "melee");
    }

    [Fact]
    public void Enforcer_Shield_Blocks_Frontal_Damage()
    {
        var arena = new AgentArena();
        var front = arena.Player(new Vec3d(0, 0, -10));
        var back = arena.Player(new Vec3d(0, 0, 10));
        var e = arena.Agents.Spawn("ironclad_enforcer", Vec3d.Zero);
        e.Actor.Move.Yaw = 0;
        e.Actor.Vitals.Armor = 0;
        var hp = e.Actor.Vitals.Health;
        arena.Combat.ApplyDamage(e.Actor, front, 40, HitZone.Torso, false, "test");
        var frontLoss = hp - e.Actor.Vitals.Health;
        hp = e.Actor.Vitals.Health;
        arena.Combat.ApplyDamage(e.Actor, back, 40, HitZone.Torso, false, "test");
        var backLoss = hp - e.Actor.Vitals.Health;
        Assert.True(frontLoss <= 40 * 0.2, $"front {frontLoss}");
        Assert.Equal(40, backLoss);
    }

    [Fact]
    public void Rival_Factions_Fight_Each_Other()
    {
        var arena = new AgentArena();
        var s = arena.Agents.Spawn("saltline_gunman", Vec3d.Zero);
        var v = arena.Agents.Spawn("velvet_gunman", new Vec3d(0, 0, -14));
        s.Actor.Move.Yaw = 0;
        v.Actor.Move.Yaw = System.Math.PI;
        arena.Run(0.4);
        Assert.Equal(v.Actor.Id, s.Target);
        arena.Run(3);
        Assert.True(arena.DamageTo(v.Actor.Id) + arena.DamageTo(s.Actor.Id) > 0);
    }

    [Fact]
    public void Police_Respond_To_Wanted_Level_And_Stand_Down()
    {
        var arena = new AgentArena();
        var p = arena.Player(new Vec3d(0, 0, 0));
        arena.Wanted = 2;
        arena.Agents.Police.SetWanted(p.Id, 2, units: 4, responseSeconds: 3);
        arena.Run(2);
        Assert.Equal(0, arena.Agents.Police.UnitsFor(p.Id));
        arena.Run(8);
        Assert.InRange(arena.Agents.Police.UnitsFor(p.Id), 2, 4);
        var cop = arena.Agents.Agents.Values.First(a => a.AssignedTo == p.Id);
        Assert.True(arena.Agents.IsHostile(cop, p));

        arena.Wanted = 0;
        arena.Agents.Police.SetWanted(p.Id, 0, 0, 0);
        Assert.False(arena.Agents.IsHostile(cop, p));
        arena.Run(Repo.Data.Agents.Police.DespawnAfterSeconds + 2);
        Assert.Equal(0, arena.Agents.Police.UnitsFor(p.Id));
        Assert.DoesNotContain(arena.World.Actors.All, a => a.Kind == ActorKind.Npc);
    }

    [Fact]
    public void Encounter_Spawns_Near_Players_And_Despawns_When_Left()
    {
        var enc = new EncounterDef
        {
            Id = "test", Position = new double[] { 0, 0, 0 }, Radius = 5,
            Members = new() { new EncounterMember("saltline_gunman", 3) },
        };
        var arena = new AgentArena(new() { enc });
        var p = arena.Player(new Vec3d(1000, 0, 0));
        arena.Run(1);
        Assert.False(arena.Agents.IsEncounterActive("test"));
        p.Position = new Vec3d(150, 0, 0);
        arena.Run(0.2);
        Assert.True(arena.Agents.IsEncounterActive("test"));
        Assert.Equal(3, arena.World.Actors.All.Count(a => a.Kind == ActorKind.Npc));
        p.Position = new Vec3d(2000, 0, 0);
        arena.Run(32);
        Assert.False(arena.Agents.IsEncounterActive("test"));
        Assert.Equal(0, arena.World.Actors.All.Count(a => a.Kind == ActorKind.Npc));
    }

    [Fact]
    public void Dead_Npc_Is_Reported_And_Cleaned_Up()
    {
        var arena = new AgentArena();
        var p = arena.Player(new Vec3d(0, 0, 30));
        var g = arena.Agents.Spawn("saltline_gunman", Vec3d.Zero);
        arena.Combat.Kill(g.Actor, p.Id, "test", false);
        Assert.Single(arena.NpcDeaths);
        Assert.Equal(p.Id, arena.NpcDeaths[0].KillerId);
        arena.Run(Repo.Data.Agents.CorpseSeconds + 0.5);
        Assert.False(arena.World.Actors.TryGet(g.Actor.Id, out _));
    }

    [Fact]
    public void Cover_Search_Finds_A_Hidden_Spot_Behind_A_Box()
    {
        var arena = new AgentArena();
        arena.Static.Add(Aabb.FromCenter(new Vec3d(0, 0.5, -3), new Vec3d(2, 1.0, 0.6)), SurfaceMaterial.Concrete);
        var spot = arena.Agents.FindCover(Vec3d.Zero, new Vec3d(0, 0, -20), 10);
        Assert.NotNull(spot);
        Assert.True(spot!.Value.Z > -3, "cover spot is on the far side from the threat");
    }

    [Fact]
    public void Agent_Data_Is_Valid_And_References_Known_Weapons()
    {
        var d = Repo.Data.Agents;
        Assert.Contains("ironclad", d.Factions.Keys);
        Assert.Contains("police", d.Factions.Keys);
        Assert.NotEmpty(d.Encounters);
        foreach (var a in d.Archetypes.Values.Where(a => a.Weapon.Length > 0)) Assert.Contains(a.Weapon, Repo.Data.Weapons.Keys);
    }
}
