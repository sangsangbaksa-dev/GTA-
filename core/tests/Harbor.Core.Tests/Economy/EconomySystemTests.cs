using Harbor.Core.Economy;
using Harbor.Core.Math;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests.Economy;

public class EconomySystemTests
{
    private sealed class Receiver : IItemReceiver
    {
        public readonly List<string> Got = new();
        public string? Refuse;
        public string? CanReceive(int actorId, ShopItem item) => Refuse;
        public void Receive(int actorId, ShopItem item) => Got.Add(item.Id);
    }

    private sealed class Town
    {
        public readonly SimWorld World = new(new NullLog());
        public readonly EconomySystem Econ;
        public readonly Receiver Items = new();
        public Town()
        {
            Econ = World.AddSystem(new EconomySystem(Repo.Data.Economy) { Items = Items });
        }

        public (Actor actor, PlayerProfile profile) Player(string id, Vec3d? pos = null, long dirty = 0)
        {
            var a = World.Actors.Spawn(ActorKind.Player, pos ?? Vec3d.Zero);
            var p = Econ.GetOrCreateProfile(id, id);
            Econ.BindActor(a.Id, p);
            if (dirty > 0) Econ.Payout(p.Id, Currency.Dirty, dirty, $"seed_{id}", $"seed:{id}");
            return (a, p);
        }

        public void Wait(double seconds) { for (var t = 0.0; t < seconds; t += World.Loop.StepSeconds) World.Update(World.Loop.StepSeconds); }
        public void AssertConserved()
        {
            Assert.Equal(0, Econ.Ledger.Total(Currency.Dirty));
            Assert.Equal(0, Econ.Ledger.Total(Currency.Clean));
        }
    }

    [Fact]
    public void New_Profile_Gets_Starting_Money_Once()
    {
        var t = new Town();
        var (_, p) = t.Player("rook");
        t.Econ.GetOrCreateProfile("rook", "rook");
        Assert.Equal(Repo.Data.Economy.Config.StartingBalance.Clean, t.Econ.Bank(p.Id));
    }

    [Fact]
    public void Buy_Checks_Currency_Rep_Funds_And_Is_Idempotent()
    {
        var t = new Town();
        var (a, p) = t.Player("rook", dirty: 1000);
        Assert.Equal("currency_not_accepted", t.Econ.Buy(a.Id, "wpn_wasp_smg", Currency.Dirty, 1).Error);
        Assert.Equal("rep_too_low", t.Econ.Buy(a.Id, "wpn_wasp_smg", Currency.Clean, 2).Error);
        p.Rep = 3;
        Assert.Equal("insufficient_funds", t.Econ.Buy(a.Id, "wpn_halberd_ar", Currency.Clean, 3).Error);
        Assert.True(t.Econ.Buy(a.Id, "ammo_rifle", Currency.Dirty, 4).Ok);
        Assert.True(t.Econ.Buy(a.Id, "ammo_rifle", Currency.Dirty, 4).Ok); // resend of the same request
        Assert.Single(t.Items.Got);
        Assert.Equal(1000 - 120, t.Econ.Wallet(p.Id));
        t.Items.Refuse = "armor_full";
        Assert.Equal("armor_full", t.Econ.Buy(a.Id, "armor_plate", Currency.Dirty, 5).Error);
        Assert.Equal(1000 - 120, t.Econ.Wallet(p.Id)); // refused before charging
        t.AssertConserved();
    }

    [Fact]
    public void Instant_Laundering_Takes_Fee()
    {
        var t = new Town();
        var (a, p) = t.Player("rook", dirty: 10_000);
        var bank = t.Econ.Bank(p.Id);
        Assert.True(t.Econ.LaunderInstant(a.Id, 10_000, 1).Ok);
        Assert.Equal(0, t.Econ.Wallet(p.Id));
        Assert.Equal(bank + 5_500, t.Econ.Bank(p.Id));
        t.AssertConserved();
    }

    [Fact]
    public void Business_Launders_Over_Time_At_Its_Rate()
    {
        var t = new Town();
        var (a, p) = t.Player("rook", dirty: 30_000);
        t.Econ.Payout(p.Id, Currency.Clean, 40_000, "seed_clean", "seed_clean");
        Assert.Equal("not_owner", t.Econ.LaunderAtBusiness(a.Id, "biz_coin_laundromat", 10_000, 1).Error);
        Assert.True(t.Econ.BuyBusiness(a.Id, "biz_coin_laundromat", 2).Ok);
        Assert.Equal(p.Id, t.Econ.OwnerOf("biz_coin_laundromat"));
        var bankBefore = t.Econ.Bank(p.Id);
        Assert.True(t.Econ.LaunderAtBusiness(a.Id, "biz_coin_laundromat", 20_000, 3).Ok);
        t.Wait(1800); // half an hour at 20k/h → 10k processed
        var credited = t.Econ.Bank(p.Id) - bankBefore;
        Assert.InRange(credited, 6_900, 7_100); // 10k × (1 - 30%)
        t.Wait(1900);
        Assert.InRange(t.Econ.Bank(p.Id) - bankBefore, 13_990, 14_000);
        t.AssertConserved();
    }

    [Fact]
    public void Crewmate_Can_Use_Crew_Business_Others_Cannot()
    {
        var t = new Town();
        var (owner, op) = t.Player("owner");
        var (mate, mp) = t.Player("mate", dirty: 5000);
        var (stranger, _) = t.Player("stranger", dirty: 5000);
        t.Econ.Payout(op.Id, Currency.Clean, 40_000, "seed_clean", "seed_clean");
        t.Econ.BuyBusiness(owner.Id, "biz_coin_laundromat", 1);
        var crew = t.Econ.Crews.Create(op, "Lantern Boys");
        t.Econ.Crews.Join(mp, crew.Id);
        Assert.True(t.Econ.LaunderAtBusiness(mate.Id, "biz_coin_laundromat", 1000, 1).Ok);
        Assert.Equal("not_owner", t.Econ.LaunderAtBusiness(stranger.Id, "biz_coin_laundromat", 1000, 1).Error);
    }

    [Fact]
    public void Stash_Only_Inside_Owned_Safehouse()
    {
        var t = new Town();
        var sh = Repo.Data.Economy.Safehouses.First(s => s.Id == "sh_mill_warehouse");
        var (a, p) = t.Player("rook", dirty: 3000);
        Assert.Equal("not_in_safehouse", t.Econ.StashDeposit(a.Id, 1000, 1).Error);
        a.Position = sh.Center;
        Assert.True(t.Econ.StashDeposit(a.Id, 1000, 2).Ok);
        Assert.Equal(1000, t.Econ.Stash(p.Id));
        Assert.Equal(2000, t.Econ.Wallet(p.Id));
        var loft = Repo.Data.Economy.Safehouses.First(s => s.Id == "sh_harbor_loft");
        a.Position = loft.Center; // not owned
        Assert.Equal("not_in_safehouse", t.Econ.StashWithdraw(a.Id, 500, 3).Error);
    }

    [Fact]
    public void Crew_Vault_Withdraw_Is_Leader_Only_And_Members_Only()
    {
        var t = new Town();
        var (lead, lp) = t.Player("lead", dirty: 5000);
        var (mate, mp) = t.Player("mate");
        var (_, sp) = t.Player("stranger");
        var crew = t.Econ.Crews.Create(lp, "Saltworks");
        t.Econ.Crews.Join(mp, crew.Id);
        Assert.True(t.Econ.CrewDeposit(lead.Id, Currency.Dirty, 4000, 1).Ok);
        Assert.Equal("not_leader", t.Econ.CrewWithdraw(mate.Id, mp.Id, Currency.Dirty, 100, 2).Error);
        Assert.Equal("not_member", t.Econ.CrewWithdraw(lead.Id, sp.Id, Currency.Dirty, 100, 3).Error);
        Assert.True(t.Econ.CrewWithdraw(lead.Id, mp.Id, Currency.Dirty, 1500, 4).Ok);
        Assert.Equal(1500, t.Econ.Wallet(mp.Id));
        t.AssertConserved();
    }

    [Fact]
    public void Death_Drop_Goes_To_Killer_First_Then_Anyone_And_Expires()
    {
        var t = new Town();
        var (victim, vp) = t.Player("victim", new Vec3d(0, 0, 0), dirty: 10_000);
        var (killer, kp) = t.Player("killer", new Vec3d(20, 0, 0));
        var (thief, tp) = t.Player("thief", new Vec3d(20.5, 0, 0));
        var drop = t.Econ.DropOnDeath(victim.Id, killer.Id);
        Assert.NotNull(drop);
        Assert.Equal(7_500, t.Econ.Wallet(vp.Id));
        // the bag lands next to the killer; both are in range but the killer has priority
        t.Wait(0.2);
        Assert.Equal(2_500, t.Econ.Wallet(kp.Id));
        Assert.Equal(0, t.Econ.Wallet(tp.Id));
        Assert.Equal(0, t.Econ.ActiveDrops);

        var (v2, _) = t.Player("victim2", new Vec3d(100, 0, 0), dirty: 200);
        t.Econ.DropOnDeath(v2.Id, -1);
        t.Wait(Repo.Data.Economy.Config.DeathDrop.LifetimeSeconds + 1);
        Assert.Equal(0, t.Econ.ActiveDrops);
        Assert.True(t.Econ.Ledger.Balance(AccountId.Sink("expired_drop"), Currency.Dirty) > 0 || t.Econ.Wallet("victim2") > 0);
        t.AssertConserved();
    }

    [Fact]
    public void Drop_Respects_Min_And_Max()
    {
        var t = new Town();
        var (rich, rp) = t.Player("rich", dirty: 1_000_000);
        t.Econ.DropOnDeath(rich.Id, -1);
        Assert.Equal(1_000_000 - 25_000, t.Econ.Wallet(rp.Id));
        var (poor, pp) = t.Player("poor", new Vec3d(50, 0, 50), dirty: 60);
        t.Econ.DropOnDeath(poor.Id, -1);
        Assert.Equal(0, t.Econ.Wallet(pp.Id)); // min 100, capped by wallet
    }

    [Fact]
    public void Payouts_Diminish_On_Repeat()
    {
        var t = new Town();
        var (_, p) = t.Player("grinder");
        var got = Enumerable.Range(0, 4).Select(i => t.Econ.Payout(p.Id, Currency.Dirty, 1000, "register", $"r{i}")).ToList();
        Assert.Equal(new long[] { 1000, 1000, 800, 600 }, got);
        Assert.Equal(0, t.Econ.Payout(p.Id, Currency.Dirty, 1000, "register", "r0")); // replayed tx
    }
}
