using Harbor.Core.Activities;
using Harbor.Core.Economy;
using Harbor.Core.Math;
using Harbor.Core.Sim;
using Xunit;
using static Harbor.Core.Tests.Activities.ActivityTown;

namespace Harbor.Core.Tests.Activities;

public class HeistTests
{
    private static ActivityData Heist() => Acts(
        new[]
        {
            Contract("prep_a", new[] { Reach(10, 0) }, 100, 100, kind: ContractKind.HeistPrep),
            Contract("prep_b", new[] { Reach(20, 0) }, 100, 100, kind: ContractKind.HeistPrep),
            Contract("finale", new[] { Deliver(0, 0, 30, 0, 2, 10000) }, 0, 0, time: 120, kind: ContractKind.HeistFinale, minPlayers: 2),
        },
        new[] { new HeistDef { Id = "heist", Preps = new() { "prep_a", "prep_b" }, Finale = "finale", MinPlayers = 2, MinSharePercent = 15, SplitTimeoutSeconds = 30 } });

    private static (ActivityTown t, Actor lead, Actor mate) Crew()
    {
        var t = new ActivityTown(Heist());
        var lead = t.Player("rook", Vec3d.Zero);
        var mate = t.Player("vale", new Vec3d(1, 0, 0));
        t.Crew("saltline", lead, mate);
        return (t, lead, mate);
    }

    private static void DoPreps(ActivityTown t, Actor lead, Actor mate)
    {
        foreach (var (id, x) in new[] { ("prep_a", 10.0), ("prep_b", 20.0) })
        {
            Assert.True(t.Act.Contracts.Start(lead.Id, id, (ulong)x).Ok);
            lead.Position = new Vec3d(x, 0, 0);
            t.Tick();
            lead.Position = Vec3d.Zero;
            mate.Position = new Vec3d(1, 0, 0);
        }
    }

    /// <summary>Each member carries one bag to the drop.</summary>
    private static void HaulBags(ActivityTown t, Actor lead, Actor mate)
    {
        var bags = t.Act.Carry.Items.ToList();
        Assert.Equal(2, bags.Count);
        lead.Position = bags[0].Position; mate.Position = bags[1].Position;
        t.Press(lead); t.Press(mate);
        t.Tick();
        lead.Position = mate.Position = new Vec3d(30, 0, 0);
        t.Tick(2);
    }

    [Fact]
    public void Finale_Is_Gated_By_Crew_Leader_And_Preps()
    {
        var (t, lead, mate) = Crew();
        var solo = t.Player("knox", new Vec3d(-50, 0, 0));
        Assert.Equal("crew_required", t.Act.Contracts.Start(solo.Id, "prep_a", 1).Error);
        Assert.Equal("leader_only", t.Act.Contracts.Start(mate.Id, "finale", 1).Error);
        Assert.Equal("preps_incomplete", t.Act.Contracts.Start(lead.Id, "finale", 1).Error);
        DoPreps(t, lead, mate);
        Assert.Equal(2, t.Events.OfType<PrepCompleted>().Count());
        Assert.Equal("prep_done", t.Act.Contracts.Start(lead.Id, "prep_a", 9).Error);
        mate.Position = new Vec3d(500, 0, 0);
        Assert.Equal("not_enough_players", t.Act.Contracts.Start(lead.Id, "finale", 2).Error);
        mate.Position = new Vec3d(1, 0, 0);
        Assert.True(t.Act.Contracts.Start(lead.Id, "finale", 3).Ok);
    }

    [Fact]
    public void Bags_Go_To_Escrow_And_Leader_Splits_The_Take()
    {
        var (t, lead, mate) = Crew();
        DoPreps(t, lead, mate);
        var before = (t.Wallet(lead), t.Wallet(mate));
        t.Act.Contracts.Start(lead.Id, "finale", 1);
        var runId = t.Events.OfType<ContractStarted>().Last().RunId;
        HaulBags(t, lead, mate);

        var pending = t.Events.OfType<HeistSplitPending>().Single();
        Assert.Equal(20000, pending.Take);
        Assert.Equal(new[] { "rook", "vale" }, pending.Members);
        Assert.Equal(20000, t.Econ.Ledger.Balance(AccountId.Escrow(ContractBoard.EscrowKey(runId)), Currency.Dirty));
        Assert.Empty(t.Act.Contracts.PrepsDone(t.P(lead).CrewId)); // preps consumed

        Assert.Equal("leader_only", t.Act.Contracts.SetSplit(mate.Id, runId, new[] { 50, 50 }).Error);
        Assert.Equal("invalid_split", t.Act.Contracts.SetSplit(lead.Id, runId, new[] { 90, 10 }).Error); // below 15 %
        Assert.Equal("invalid_split", t.Act.Contracts.SetSplit(lead.Id, runId, new[] { 60, 30 }).Error); // not 100
        Assert.True(t.Act.Contracts.SetSplit(lead.Id, runId, new[] { 70, 30 }).Ok);

        Assert.Equal(14000, t.Wallet(lead) - before.Item1);
        Assert.Equal(6000, t.Wallet(mate) - before.Item2);
        Assert.Equal(0, t.Econ.Ledger.Balance(AccountId.Escrow(ContractBoard.EscrowKey(runId)), Currency.Dirty));
        Assert.Equal("no_split", t.Act.Contracts.SetSplit(lead.Id, runId, new[] { 70, 30 }).Error);
        t.AssertConserved();
    }

    [Fact]
    public void Split_Defaults_To_Even_After_Timeout()
    {
        var (t, lead, mate) = Crew();
        DoPreps(t, lead, mate);
        var before = t.Wallet(mate);
        t.Act.Contracts.Start(lead.Id, "finale", 1);
        HaulBags(t, lead, mate);
        t.Wait(31);
        Assert.Equal(10000, t.Wallet(mate) - before);
        Assert.Single(t.Events.OfType<HeistPaid>());
    }

    [Fact]
    public void Failed_Finale_Forfeits_Escrow_And_Loses_A_Prep()
    {
        var (t, lead, mate) = Crew();
        DoPreps(t, lead, mate);
        t.Act.Contracts.Start(lead.Id, "finale", 1);
        var runId = t.Events.OfType<ContractStarted>().Last().RunId;
        var bag = t.Act.Carry.Items.First();
        lead.Position = bag.Position;
        t.Press(lead);
        t.Tick();
        lead.Position = new Vec3d(30, 0, 0);
        t.Tick(2); // one of two bags delivered
        Assert.Equal(10000, t.Econ.Ledger.Balance(AccountId.Escrow(ContractBoard.EscrowKey(runId)), Currency.Dirty));
        t.Wait(121);
        Assert.False(t.Events.OfType<ContractEnded>().Last().Success);
        Assert.Equal(0, t.Econ.Ledger.Balance(AccountId.Escrow(ContractBoard.EscrowKey(runId)), Currency.Dirty));
        Assert.Single(t.Events.OfType<PrepLost>());
        Assert.Single(t.Act.Contracts.PrepsDone(t.P(lead).CrewId));
        Assert.Empty(t.Act.Carry.Items); // leftover bags removed
        t.AssertConserved();
    }

    [Fact]
    public void Percent_Packing_Roundtrips()
    {
        var p = new[] { 40, 25, 20, 15 };
        Assert.Equal(p, ContractBoard.UnpackPercents(ContractBoard.PackPercents(p), 4));
    }
}
