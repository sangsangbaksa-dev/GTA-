using Harbor.Core.Combat;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests.Combat;

public class CombatSystemTests
{
    private sealed class Arena
    {
        public readonly SimWorld World = new(new NullLog());
        public readonly StaticWorld Static = new();
        public readonly CombatSystem Combat;
        public readonly List<object> Events = new();
        public double Dt => World.Loop.StepSeconds;

        public Arena()
        {
            Combat = World.AddSystem(new CombatSystem(Repo.Data.Combat, Repo.Data.Materials, Repo.Data.Weapons, Static, null));
            World.Events.Subscribe<ActorKilled>(Events.Add);
            World.Events.Subscribe<ActorDowned>(Events.Add);
            World.Events.Subscribe<ActorRevived>(Events.Add);
            World.Events.Subscribe<ActorDamaged>(Events.Add);
            World.Events.Subscribe<ShotFired>(Events.Add);
            World.Events.Subscribe<ExplosiveDetonated>(Events.Add);
            World.Events.Subscribe<ActorRespawned>(Events.Add);
            World.Events.Subscribe<RepeatKillDetected>(Events.Add);
        }

        public Actor Spawn(ActorKind kind, Vec3d pos, int team = 0, string? weapon = null)
        {
            var a = World.Actors.Spawn(kind, pos);
            a.Team = team;
            Combat.Register(a);
            if (weapon != null)
            {
                Combat.GiveWeapon(a.Id, weapon);
                var l = Combat.LoadoutOf(a.Id)!;
                l.Active = l.Slots.FindIndex(s => s.Def.Id == weapon);
                Combat.AddReserveAmmo(a.Id, l.Current!.Def.Class, 300);
            }
            return a;
        }

        /// <summary>Aim from shooter's eye at target's chest (or head) and hold inputs for n ticks.</summary>
        public void Hold(Actor shooter, Actor? target, int ticks, bool fire = true, bool reload = false, bool interact = false,
            bool head = false, Func<bool>? until = null)
        {
            for (var i = 0; i < ticks; i++)
            {
                if (until?.Invoke() == true) return;
                double yaw = 0, pitch = 0;
                if (target != null)
                {
                    var eye = shooter.Position + new Vec3d(0, Repo.Data.Combat.EyeHeight, 0);
                    var aimAt = target.Position + new Vec3d(0, head ? 1.64 : 1.15, 0);
                    var d = (aimAt - eye).Normalized();
                    yaw = System.Math.Atan2(-d.X, -d.Z);
                    pitch = System.Math.Asin(d.Y);
                }
                var slot = (byte)Combat.LoadoutOf(shooter.Id)!.Active;
                Combat.ProcessInput(shooter.Id, new CombatInput(fire, true, reload, interact, slot, yaw, pitch, World.Loop.Tick), Dt);
                World.Update(Dt);
            }
        }

        public void Wait(double seconds) { for (var t = 0.0; t < seconds; t += Dt) World.Update(Dt); }
        public int Count<T>() => Events.OfType<T>().Count();
    }

    [Fact]
    public void Auto_Rifle_Fires_At_Rate_And_Semi_Needs_Release()
    {
        var a = new Arena();
        var rifle = a.Spawn(ActorKind.Player, Vec3d.Zero, weapon: "halberd_ar");
        a.Hold(rifle, null, 30); // 1 second
        Assert.InRange(a.Count<ShotFired>(), 10, 12); // 650 rpm ≈ 10.8/s

        var b = new Arena();
        var pistol = b.Spawn(ActorKind.Player, Vec3d.Zero);
        b.Hold(pistol, null, 30);
        Assert.Equal(1, b.Count<ShotFired>());
    }

    [Fact]
    public void Empty_Magazine_Reloads_From_Reserve()
    {
        var a = new Arena();
        var p = a.Spawn(ActorKind.Player, Vec3d.Zero, weapon: "wasp_smg");
        var l = a.Combat.LoadoutOf(p.Id)!;
        var reserve = l.ReserveOf(WeaponClass.Smg);
        a.Hold(p, null, 90);
        Assert.Equal(30, a.Count<ShotFired>()); // one full magazine, then auto-reload starts
        a.Hold(p, null, 1, fire: false);
        a.Wait(2.0);
        Assert.False(l.IsReloading, $"reload left {l.ReloadLeft}");
        Assert.True(l.Current!.InMagazine == 30, $"mag {l.Current.InMagazine} res {l.ReserveOf(WeaponClass.Smg)} active {l.Active} {l.Current.Def.Id} slots {l.Slots.Count}");
        Assert.Equal(30, l.Current!.InMagazine);
        Assert.Equal(reserve - 30, l.ReserveOf(WeaponClass.Smg));
    }

    [Fact]
    public void Pvp_Kill_Then_Respawn_With_Full_Health()
    {
        var a = new Arena();
        var shooter = a.Spawn(ActorKind.Player, Vec3d.Zero, team: 1, weapon: "halberd_ar");
        var victim = a.Spawn(ActorKind.Player, new Vec3d(0, 0, -10), team: 2);
        a.Combat.SpawnPointFor = _ => new Vec3d(100, 0, 100);
        a.Hold(shooter, victim, 30);
        var kill = Assert.Single(a.Events.OfType<ActorKilled>());
        Assert.Equal(shooter.Id, kill.KillerId);
        Assert.Equal(0, a.Count<ActorDowned>()); // PvP: no downed state
        Assert.False(victim.IsAlive);
        a.Wait(Repo.Data.Combat.RespawnSeconds + 0.1);
        Assert.True(victim.IsAlive);
        Assert.Equal(100, victim.Vitals.Health);
        Assert.Equal(new Vec3d(100, 0, 100), victim.Position);
    }

    [Fact]
    public void Headshot_Flag_Is_Reported()
    {
        var a = new Arena();
        var shooter = a.Spawn(ActorKind.Player, Vec3d.Zero, team: 1, weapon: "halberd_ar");
        var victim = a.Spawn(ActorKind.Npc, new Vec3d(0, 0, -8));
        a.Hold(shooter, victim, 15, head: true);
        Assert.True(Assert.Single(a.Events.OfType<ActorKilled>()).Headshot);
    }

    [Fact]
    public void Npc_Downs_Player_And_Crewmate_Revives()
    {
        var a = new Arena();
        var npc = a.Spawn(ActorKind.Npc, Vec3d.Zero, team: 9, weapon: "halberd_ar");
        var victim = a.Spawn(ActorKind.Player, new Vec3d(0, 0, -10), team: 1);
        var medic = a.Spawn(ActorKind.Player, new Vec3d(1, 0, -10), team: 1);
        a.Hold(npc, victim, 30, until: () => (victim.Status & ActorFlags.Downed) != 0);
        Assert.Single(a.Events.OfType<ActorDowned>());
        Assert.True((victim.Status & ActorFlags.Downed) != 0);
        a.Hold(npc, null, 1, fire: false);
        a.Hold(medic, null, (int)(Repo.Data.Combat.Downed.ReviveSeconds * 30) + 1, fire: false, interact: true);
        Assert.Single(a.Events.OfType<ActorRevived>());
        Assert.Equal(Repo.Data.Combat.Downed.ReviveHealth, victim.Vitals.Health);
    }

    [Fact]
    public void Downed_Bleeds_Out()
    {
        var a = new Arena();
        var npc = a.Spawn(ActorKind.Npc, Vec3d.Zero, weapon: "halberd_ar");
        var victim = a.Spawn(ActorKind.Player, new Vec3d(0, 0, -10), team: 1);
        a.Hold(npc, victim, 30, until: () => (victim.Status & ActorFlags.Downed) != 0);
        a.Wait(Repo.Data.Combat.Downed.Seconds + 0.5);
        Assert.Equal("bleed_out", Assert.Single(a.Events.OfType<ActorKilled>()).Source);
    }

    [Fact]
    public void No_Friendly_Fire_And_Passive_Is_Immune()
    {
        var a = new Arena();
        var s = a.Spawn(ActorKind.Player, Vec3d.Zero, team: 1, weapon: "halberd_ar");
        var mate = a.Spawn(ActorKind.Player, new Vec3d(0, 0, -10), team: 1);
        a.Hold(s, mate, 10);
        Assert.Equal(100, mate.Vitals.Health);

        var b = new Arena();
        var s2 = b.Spawn(ActorKind.Player, Vec3d.Zero, team: 1, weapon: "halberd_ar");
        var passive = b.Spawn(ActorKind.Player, new Vec3d(0, 0, -10), team: 2);
        passive.Status |= ActorFlags.Passive;
        b.Hold(s2, passive, 10);
        Assert.Equal(100, passive.Vitals.Health);
    }

    [Fact]
    public void Health_Regenerates_Only_To_Cap_After_Delay()
    {
        var a = new Arena();
        var p = a.Spawn(ActorKind.Player, Vec3d.Zero);
        p.Vitals.Health = 20;
        a.Wait(3);
        Assert.Equal(20, p.Vitals.Health);
        a.Wait(10);
        Assert.Equal(Repo.Data.Combat.Health.RegenCap, p.Vitals.Health);
    }

    [Fact]
    public void Cover_Stops_Bullets()
    {
        var a = new Arena();
        a.Static.Add(Aabb.FromCenter(new Vec3d(0, 1, -5), new Vec3d(3, 2, 0.6)), SurfaceMaterial.Concrete, "cover_high");
        var s = a.Spawn(ActorKind.Player, Vec3d.Zero, team: 1, weapon: "halberd_ar");
        var t = a.Spawn(ActorKind.Npc, new Vec3d(0, 0, -10));
        a.Hold(s, t, 30);
        Assert.True(a.Count<ShotFired>() > 5);
        Assert.Equal(0, a.Count<ActorDamaged>());
    }

    [Fact]
    public void Explosive_Barrel_Chains_And_Hurts_Nearby_But_Not_Behind_Concrete()
    {
        var a = new Arena();
        var b1 = a.Static.Add(Aabb.FromCenter(new Vec3d(0, 0.45, -10), new Vec3d(0.6, 0.9, 0.6)), SurfaceMaterial.ThinMetal, "explosive");
        var b2 = a.Static.Add(Aabb.FromCenter(new Vec3d(3, 0.45, -10), new Vec3d(0.6, 0.9, 0.6)), SurfaceMaterial.ThinMetal, "explosive");
        a.Combat.RegisterExplosive(b1);
        a.Combat.RegisterExplosive(b2);
        a.Static.Add(Aabb.FromCenter(new Vec3d(-2.5, 1, -10), new Vec3d(0.4, 3, 3)), SurfaceMaterial.Concrete, "cover_full");
        var shooter = a.Spawn(ActorKind.Player, Vec3d.Zero, team: 1, weapon: "halberd_ar");
        var near = a.Spawn(ActorKind.Npc, new Vec3d(5, 0, -10));
        var shielded = a.Spawn(ActorKind.Npc, new Vec3d(-4, 0, -10));
        // aim at the first barrel
        var dummy = a.World.Actors.Spawn(ActorKind.Pickup, new Vec3d(0, -0.9, -10));
        a.Hold(shooter, dummy, 6);
        a.Wait(1);
        Assert.Equal(2, a.Count<ExplosiveDetonated>());
        Assert.True(near.Vitals.Health < 100);
        Assert.Equal(100, shielded.Vitals.Health);
        Assert.Equal(0, a.Static.Count - 1); // only the concrete wall remains
    }

    [Fact]
    public void Near_Miss_Suppresses()
    {
        var a = new Arena();
        var s = a.Spawn(ActorKind.Player, Vec3d.Zero, team: 1, weapon: "halberd_ar");
        var bystander = a.Spawn(ActorKind.Npc, new Vec3d(1.0, 0, -10));
        var aimPoint = a.World.Actors.Spawn(ActorKind.Pickup, new Vec3d(0, 0, -30));
        a.Hold(s, aimPoint, 5);
        Assert.True(a.Combat.SuppressionOf(bystander.Id) > 0);
    }

    [Fact]
    public void Repeat_Kills_Are_Detected()
    {
        var a = new Arena();
        var griefer = a.Spawn(ActorKind.Player, Vec3d.Zero, team: 1, weapon: "halberd_ar");
        var victim = a.Spawn(ActorKind.Player, new Vec3d(0, 0, -10), team: 2);
        a.Combat.SpawnPointFor = _ => new Vec3d(0, 0, -10);
        for (var i = 0; i < 3; i++)
        {
            a.Hold(griefer, victim, 30);
            a.Wait(Repo.Data.Combat.RespawnSeconds + 0.1);
        }
        Assert.NotEmpty(a.Events.OfType<RepeatKillDetected>());
    }

    [Theory]
    [InlineData("halberd_ar", false)]
    [InlineData("kestrel_p10", true)]
    public void Carrying_A_Bag_Allows_Only_One_Handed_Weapons(string weapon, bool canFire)
    {
        var a = new Arena();
        var shooter = a.Spawn(ActorKind.Player, Vec3d.Zero, team: 1, weapon: weapon);
        var target = a.Spawn(ActorKind.Npc, new Vec3d(0, 0, -8), team: 2);
        shooter.Status |= ActorFlags.Carrying;
        a.Hold(shooter, target, 10);
        Assert.Equal(canFire, a.Events.OfType<ShotFired>().Any());
    }
}
