using Harbor.Core.Combat;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests.Combat;

public class DamageTests
{
    private static WeaponDef W(string id) => Repo.Data.Weapons[id];
    private static CombatConfig Cfg => Repo.Data.Combat;

    [Fact]
    public void Data_Loads_All_Weapons_And_Materials()
    {
        Assert.Equal(4, Repo.Data.Weapons.Count);
        Assert.Equal(WeaponClass.Shotgun, W("breaker_12").Class);
        Assert.Equal(8, W("breaker_12").Pellets);
        Assert.Equal(0.6, Cfg.Armor.Absorb);
        Assert.Equal(0.5, Repo.Data.Materials.PassScale(SurfaceMaterial.Wood, WeaponClass.Rifle, 1.2));
        Assert.Equal(0.0, Repo.Data.Materials.PassScale(SurfaceMaterial.Wood, WeaponClass.Pistol, 0.1));
    }

    [Theory]
    [InlineData(10, 1.0)]
    [InlineData(40, 1.0)]
    [InlineData(80, 0.8)]
    [InlineData(120, 0.6)]
    [InlineData(500, 0.6)]
    public void Falloff_Is_Linear_Between_Start_And_End(double dist, double expected)
        => Assert.Equal(expected, DamageModel.Falloff(W("halberd_ar"), dist), 6);

    [Fact]
    public void Zone_Multipliers()
    {
        var w = W("halberd_ar");
        Assert.Equal(56, DamageModel.Raw(w, HitZone.Head, 5, 1, Cfg), 6);
        Assert.Equal(28, DamageModel.Raw(w, HitZone.Torso, 5, 1, Cfg), 6);
        Assert.Equal(21, DamageModel.Raw(w, HitZone.Limbs, 5, 1, Cfg), 6);
    }

    [Fact]
    public void Armor_Absorbs_Share_But_Not_Headshots()
    {
        var v = Vitals.Full();
        v.Armor = 100;
        var r = DamageModel.Apply(ref v, 28, bypassArmor: false, Cfg);
        Assert.Equal(17, r.ArmorLost);
        Assert.Equal(11, r.HealthLost);
        Assert.Equal(83, v.Armor);
        var h = DamageModel.Apply(ref v, 56, bypassArmor: true, Cfg);
        Assert.Equal(0, h.ArmorLost);
        Assert.Equal(56, h.HealthLost);
    }

    [Fact]
    public void Armor_Runs_Out_Then_Health_Takes_All()
    {
        var v = Vitals.Full();
        v.Armor = 5;
        var r = DamageModel.Apply(ref v, 20, false, Cfg);
        Assert.Equal(5, r.ArmorLost);
        Assert.Equal(15, r.HealthLost);
    }

    [Fact]
    public void Damage_Never_Goes_Below_Zero_And_Reports_Zero()
    {
        var v = Vitals.Full();
        var r = DamageModel.Apply(ref v, 500, false, Cfg);
        Assert.Equal(100, r.HealthLost);
        Assert.True(r.ReachedZero);
        Assert.Equal(0, v.Health);
    }

    [Fact]
    public void Hitboxes_Zones_And_Crouch()
    {
        var pose = new PoseSample(new Vec3d(0, 0, -10), Crouched: false, Alive: true);
        var eye = new Vec3d(0, 1.64, 0);
        Assert.True(Hitboxes.Raycast(eye, new Vec3d(0, 0, -1), 50, pose, out var t, out var z));
        Assert.Equal(HitZone.Head, z);
        Assert.Equal(10 - 0.13, t, 3);
        Assert.True(Hitboxes.Raycast(new Vec3d(0, 1.2, 0), new Vec3d(0, 0, -1), 50, pose, out _, out z));
        Assert.Equal(HitZone.Torso, z);
        Assert.True(Hitboxes.Raycast(new Vec3d(0, 0.5, 0), new Vec3d(0, 0, -1), 50, pose, out _, out z));
        Assert.Equal(HitZone.Limbs, z);
        Assert.False(Hitboxes.Raycast(new Vec3d(0.6, 1.64, 0), new Vec3d(0, 0, -1), 50, pose, out _, out _));
        var crouched = pose with { Crouched = true };
        Assert.False(Hitboxes.Raycast(eye + new Vec3d(0, 0.1, 0), new Vec3d(0, 0, -1), 50, crouched, out _, out _));
    }

    [Fact]
    public void Vertical_Ray_Hits_Capsule_Cap()
    {
        var pose = new PoseSample(Vec3d.Zero, false, true);
        Assert.True(Hitboxes.Raycast(new Vec3d(0, 5, 0), new Vec3d(0, -1, 0), 10, pose, out var t, out var z));
        Assert.Equal(HitZone.Head, z);
        Assert.Equal(5 - 1.64 - 0.13, t, 3);
    }

    [Fact]
    public void Short_Downward_Ray_Reaches_Head_Before_Feet()
    {
        var pose = new PoseSample(Vec3d.Zero, false, true);
        Assert.True(Hitboxes.Raycast(new Vec3d(0, 3, 0), new Vec3d(0, -1, 0), 1.5, pose, out var t, out var z));
        Assert.Equal(HitZone.Head, z);
        Assert.Equal(1.23, t, 6);
        Assert.False(Hitboxes.Raycast(new Vec3d(0, 3, 0), new Vec3d(0, -1, 0), 1.2, pose, out _, out _));
    }

    [Theory]
    [InlineData(false, 1.64, HitZone.Head)]
    [InlineData(false, 1.15, HitZone.Torso)]
    [InlineData(true, 1.14, HitZone.Head)]
    [InlineData(true, 0.65, HitZone.Torso)]
    public void Ray_Starting_Inside_Pose_Registers_Immediate_Contact(bool crouched, double y, HitZone expected)
    {
        var pose = new PoseSample(Vec3d.Zero, crouched, true);
        Assert.True(Hitboxes.Raycast(new Vec3d(0, y, 0), new Vec3d(1, 0, 0), 0.1, pose, out var t, out var zone));
        Assert.Equal(0.0, t);
        Assert.Equal(expected, zone);
    }

    [Fact]
    public void Short_Ray_Outside_Hitbox_Does_Not_Register_Contact()
    {
        var pose = new PoseSample(Vec3d.Zero, false, true);
        Assert.False(Hitboxes.Raycast(new Vec3d(0.5, 1.64, 0), new Vec3d(1, 0, 0), 0.1, pose, out _, out _));
    }

    private static (StaticWorld world, Dictionary<int, PoseSample> targets) Range(SurfaceMaterial wall, double thickness)
    {
        var w = new StaticWorld();
        w.Add(Aabb.FromCenter(new Vec3d(0, 1, -5), new Vec3d(2, 2, thickness)), wall);
        return (w, new Dictionary<int, PoseSample> { [2] = new(new Vec3d(0, 0, -10), false, true) });
    }

    [Theory]
    [InlineData(SurfaceMaterial.Wood, WeaponClass.Rifle, 0.5)]
    [InlineData(SurfaceMaterial.Wood, WeaponClass.Pistol, 0.0)]
    [InlineData(SurfaceMaterial.Concrete, WeaponClass.Rifle, 0.0)]
    [InlineData(SurfaceMaterial.ThinMetal, WeaponClass.Smg, 0.6)]
    [InlineData(SurfaceMaterial.Glass, WeaponClass.Pistol, 0.85)]
    public void Penetration_By_Material_And_Class(SurfaceMaterial m, WeaponClass c, double expectedScale)
    {
        var (w, targets) = Range(m, 0.1);
        var tr = Ballistics.Trace(new Vec3d(0, 1.2, 0), new Vec3d(0, 0, -1), 100, 1, targets, w, Repo.Data.Materials, c);
        Assert.Single(tr.Impacts);
        if (expectedScale == 0) Assert.Null(tr.Hit);
        else Assert.Equal(expectedScale, tr.Hit!.Value.PenetrationScale, 6);
    }

    [Fact]
    public void Too_Thick_Wood_Stops_Rifle()
    {
        var (w, targets) = Range(SurfaceMaterial.Wood, 2.0);
        Assert.Null(Ballistics.Trace(new Vec3d(0, 1.2, 0), new Vec3d(0, 0, -1), 100, 1, targets, w, Repo.Data.Materials, WeaponClass.Rifle).Hit);
    }

    [Fact]
    public void Shooter_Never_Hits_Self_And_Dead_Are_Ignored()
    {
        var targets = new Dictionary<int, PoseSample>
        {
            [1] = new(new Vec3d(0, 0, 0), false, true),
            [2] = new(new Vec3d(0, 0, -5), false, false),
        };
        Assert.Null(Ballistics.Trace(new Vec3d(0, 1.2, 0), new Vec3d(0, 0, -1), 100, 1, targets, new StaticWorld(), Repo.Data.Materials, WeaponClass.Rifle).Hit);
    }
}
