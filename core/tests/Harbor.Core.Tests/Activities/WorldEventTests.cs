using Harbor.Core.Activities;
using Harbor.Core.Economy;
using Harbor.Core.Math;
using Harbor.Core.Sim;
using Xunit;
using static Harbor.Core.Tests.Activities.ActivityTown;

namespace Harbor.Core.Tests.Activities;

public class TurfTests
{
    private static TurfConfig Cfg() => new()
    {
        CaptureSeconds = 10, AttackIntervalSeconds = 100, AttackNoticeSeconds = 20, AttackWindowSeconds = 30, PayoutIntervalSeconds = 10,
        ShopDiscount = 0.1,
        Zones = new() { new TurfZoneDef { Id = "z1", District = "north", At = new double[] { 0, 0, 0 }, Radius = 10, IncomePerHour = 3600 } },
    };

    [Fact]
    public void Uncontested_Crew_Captures_And_Earns_Into_Vault()
    {
        var t = new ActivityTown(Acts(Array.Empty<ContractDef>(), turf: Cfg()));
        var a = t.Player("rook", Vec3d.Zero);
        var crew = t.Crew("saltline", a);
        t.Wait(9);
        Assert.Equal(0, t.Act.Turf.Get("z1")!.Owner);
        t.Wait(1.1);
        Assert.Equal(crew.Id, t.Act.Turf.Get("z1")!.Owner);
        t.Wait(20.1);
        Assert.Equal(20, t.Econ.Ledger.Balance(AccountId.Crew(crew.Id), Currency.Dirty), 1.0); // 1 $/s
        t.AssertConserved();
    }

    [Fact]
    public void Contested_Zone_Holds_Progress()
    {
        var t = new ActivityTown(Acts(Array.Empty<ContractDef>(), turf: Cfg()));
        var a = t.Player("rook", Vec3d.Zero);
        var b = t.Player("knox", new Vec3d(500, 0, 0));
        t.Crew("saltline", a);
        t.Crew("velvet", b);
        t.Wait(5);
        b.Position = new Vec3d(2, 0, 0);
        t.Wait(20);
        Assert.Equal(0, t.Act.Turf.Get("z1")!.Owner);
        Assert.InRange(t.Act.Turf.Get("z1")!.CaptureProgress, 4.9, 5.1);
    }

    [Fact]
    public void Attack_Window_Lets_Rival_Take_Zone_And_Offline_Owner_Is_Protected()
    {
        var t = new ActivityTown(Acts(Array.Empty<ContractDef>(), turf: Cfg()));
        var a = t.Player("rook", Vec3d.Zero);
        var b = t.Player("knox", new Vec3d(500, 0, 0));
        var ca = t.Crew("saltline", a);
        var cb = t.Crew("velvet", b);
        t.Wait(10.1);
        a.Position = new Vec3d(300, 0, 0);
        b.Position = Vec3d.Zero;
        t.Wait(30);
        Assert.Equal(ca.Id, t.Act.Turf.Get("z1")!.Owner); // not attackable yet

        // owner goes offline: the attack clock stops and no income accrues
        var vault = t.Econ.Ledger.Balance(AccountId.Crew(ca.Id), Currency.Dirty);
        t.Econ.UnbindActor(a.Id);
        t.Wait(200);
        Assert.Empty(t.Events.OfType<TurfAttackScheduled>());
        Assert.Equal(vault, t.Econ.Ledger.Balance(AccountId.Crew(ca.Id), Currency.Dirty));
        Assert.Equal(ca.Id, t.Act.Turf.Get("z1")!.Owner);

        t.Econ.BindActor(a.Id, t.Econ.Profile("rook")!);
        t.Until(() => t.Events.OfType<TurfAttackScheduled>().Any(), 100, "attack notice");
        t.Until(() => t.Events.OfType<TurfAttackStarted>().Any(), 21, "attack window");
        t.Wait(10.1);
        Assert.Equal(cb.Id, t.Act.Turf.Get("z1")!.Owner);
        Assert.Contains(t.Events.OfType<TurfCaptured>(), e => e.NewCrew == cb.Id && e.OldCrew == ca.Id);
    }

    [Fact]
    public void Owner_Defends_By_Holding_Through_Window()
    {
        var t = new ActivityTown(Acts(Array.Empty<ContractDef>(), turf: Cfg()));
        var a = t.Player("rook", Vec3d.Zero);
        t.Crew("saltline", a);
        t.Wait(10.1);
        t.Until(() => t.Events.OfType<TurfDefended>().Any(), 140, "defended");
    }

    [Fact]
    public void Owned_District_Gets_Shop_Discount()
    {
        var t = new ActivityTown(Acts(Array.Empty<ContractDef>(), turf: Cfg()));
        var a = t.Player("rook", Vec3d.Zero, dirty: 1000);
        t.Crew("saltline", a);
        var item = t.Econ.Data.Shop["ammo_rifle"];
        Assert.Equal(item.Price, t.Econ.PriceFor(a.Id, item));
        t.Wait(10.1);
        Assert.Equal((long)System.Math.Round(item.Price * 0.9), t.Econ.PriceFor(a.Id, item));
    }
}

public class SupplyDropTests
{
    private static SupplyDropConfig Cfg() => new()
    {
        FirstDropSeconds = 20, IntervalSeconds = 100, NoticeSeconds = 10, OpenSeconds = 5, OpenRadius = 2, LifetimeSeconds = 30, Cash = 6000,
        Items = new() { "ammo_rifle", "armor_plate" },
        Sites = new() { new SupplySiteDef("s1", "north", new double[] { 0, 0, 0 }), new SupplySiteDef("s2", "south", new double[] { 50, 0, 0 }) },
    };

    [Fact]
    public void Drop_Is_Announced_Lands_And_First_Uninterrupted_Opener_Wins()
    {
        var t = new ActivityTown(Acts(Array.Empty<ContractDef>(), supply: Cfg()));
        t.Wait(10.1);
        var ann = t.Events.OfType<SupplyDropAnnounced>().Single();
        Assert.InRange(ann.LandsInSeconds, 9.5, 10.0);
        t.Wait(10);
        var landed = t.Events.OfType<SupplyDropLanded>().Single();
        var a = t.Player("rook", landed.Position);
        var b = t.Player("knox", landed.Position + new Vec3d(1, 0, 0));
        t.Act.Input.Set(a.Id, true);
        t.Wait(2);
        t.Act.Input.Set(b.Id, true);
        t.Wait(2);
        a.Vitals.SecondsSinceDamage = 0; // a gets shot at 4 s → progress lost
        t.Wait(3.2);
        var opened = t.Events.OfType<SupplyDropOpened>().Single();
        Assert.Equal(b.Id, opened.ActorId);
        Assert.Equal(6000, t.Wallet(b));
        Assert.Equal(new[] { "ammo_rifle", "armor_plate" }, t.Granted.Where(g => g.Actor == b.Id).Select(g => g.Item));
        Assert.Empty(t.Act.Supply.Active);
        Assert.False(t.World.Actors.TryGet(landed.CrateActorId, out _));
    }

    [Fact]
    public void Unopened_Drop_Expires_And_Next_Site_Rotates()
    {
        var t = new ActivityTown(Acts(Array.Empty<ContractDef>(), supply: Cfg()));
        t.Wait(51);
        Assert.Single(t.Events.OfType<SupplyDropExpired>());
        t.Until(() => t.Events.OfType<SupplyDropAnnounced>().Count() == 2, 100, "second announcement");
        var sites = t.Events.OfType<SupplyDropAnnounced>().Select(e => e.SiteId).ToList();
        Assert.NotEqual(sites[0], sites[1]);
    }
}

public class BountyTests
{
    private static ActivityTown Town() => new(Acts(Array.Empty<ContractDef>()));

    private static void MakeMostWanted(ActivityTown t, Actor a)
    {
        t.Heat.AddLevels(a.Id, Repo.Data.Economy.Config.Bounty.MinHeatLevel);
        // keep committing crimes so heat doesn't decay
        for (var s = 0; s < Repo.Data.Economy.Config.Bounty.HoldSeconds + 1; s += 5)
        {
            t.Heat.Report(a.Id, "shots_fired");
            t.Wait(5);
        }
    }

    [Fact]
    public void Sustained_Wanted_Level_Places_Bounty_And_Hunter_Is_Paid_Clean()
    {
        var t = Town();
        var target = t.Player("rook", Vec3d.Zero, dirty: 10000);
        var hunter = t.Player("knox", new Vec3d(20, 0, 0));
        var bankBefore = t.Econ.Bank("knox");
        MakeMostWanted(t, target);
        var placed = t.Events.OfType<BountyPlaced>().Single();
        Assert.Equal(2000 + 2000, placed.Amount); // base + 20 % of wallet
        Assert.Equal(8000, t.Wallet(target));

        t.Kill(target, hunter);
        Assert.Equal(4000, t.Econ.Bank("knox") - bankBefore);
        Assert.Single(t.Events.OfType<BountyClaimed>());
        Assert.Null(t.Act.Bounties.On(target.Id));
        t.AssertConserved();
    }

    [Fact]
    public void Evading_Returns_Seized_Cash()
    {
        var t = Town();
        var target = t.Player("rook", Vec3d.Zero, dirty: 10000);
        MakeMostWanted(t, target);
        Assert.Equal(8000, t.Wallet(target));
        t.Until(() => t.Heat.LevelOf(target.Id) == 0, 1200, "heat decay");
        t.Tick();
        Assert.Equal(10000, t.Wallet(target));
        Assert.Equal("evaded", t.Events.OfType<BountyCleared>().Single().Reason);
        t.AssertConserved();
    }

    [Fact]
    public void Crew_Mates_Cannot_Farm_Each_Others_Bounty()
    {
        var t = Town();
        var target = t.Player("rook", Vec3d.Zero, dirty: 10000);
        var mate = t.Player("vale", Vector(1));
        t.Crew("saltline", target, mate);
        t.Act.Bounties.Place(target.Id, "harassment");
        var bank = t.Econ.Bank("vale");
        t.Kill(target, mate);
        Assert.Equal(bank, t.Econ.Bank("vale"));
        Assert.Equal("died", t.Events.OfType<BountyCleared>().Single().Reason);
        t.AssertConserved();
    }

    private static Vec3d Vector(double x) => new(x, 0, 0);
}
