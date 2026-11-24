using Harbor.Core.Activities;
using Harbor.Core.Economy;
using Harbor.Core.Heat;
using Harbor.Core.Math;
using Harbor.Core.Missions;
using Harbor.Core.Sim;
using Xunit;
using static Harbor.Core.Tests.Activities.ActivityTown;

namespace Harbor.Core.Tests.Activities;

public class ContractTests
{
    private static ActivityData Robbery(double cooldown = 0, double time = 300) => Acts(new[]
    {
        Contract("ct_test", new[] { Reach(50, 0), Interact(52, 0, 4, crime: "robbery"), Hold(50, 0, 10), Escape(50, 0, 100) },
            min: 1000, max: 2000, time: time, cooldown: cooldown),
    });

    /// <summary>Walks the party through the robbery contract.</summary>
    private static void RunRobbery(ActivityTown t, params Actor[] party)
    {
        foreach (var a in party) a.Position = new Vec3d(50, 0, 0);
        t.Tick();
        party[0].Position = new Vec3d(52, 0, 0);
        t.Act.Input.Set(party[0].Id, true);
        t.Wait(4.1);
        t.Release(party[0]);
        t.Wait(10.1);
        foreach (var a in party) a.Position = new Vec3d(200, 0, 0);
        t.Tick(2);
    }

    [Fact]
    public void Contract_Runs_End_To_End_And_Splits_Reward()
    {
        var t = new ActivityTown(Robbery());
        var a = t.Player("rook", Vec3d.Zero);
        var b = t.Player("vale", new Vec3d(5, 0, 0));
        t.Crew("saltline", a, b);

        Assert.True(t.Act.Contracts.Start(a.Id, "ct_test", 1).Ok);
        var started = t.Events.OfType<ContractStarted>().Single();
        Assert.Equal(new[] { a.Id, b.Id }, started.Party);
        Assert.True(t.Act.IsBusy(b.Id));

        RunRobbery(t, a, b);
        var end = t.Events.OfType<ContractEnded>().Single();
        Assert.True(end.Success, end.Reason);
        // reward scales with time left: ~16 s of 300 s used → close to max
        Assert.InRange(end.TotalPaid, 1900, 2000);
        Assert.Equal(t.Wallet(a), t.Wallet(b));
        Assert.Equal(end.TotalPaid, t.Wallet(a) + t.Wallet(b));
        Assert.Equal(4, t.Events.OfType<ObjectiveStarted>().Count());
        Assert.False(t.Act.IsBusy(a.Id));
        Assert.Contains(t.Events.OfType<CrimeCommitted>(), c => c.ActorId == a.Id && c.Crime == "robbery");
        t.AssertConserved();
    }

    [Fact]
    public void Interaction_Pauses_When_Hit_And_Resets_When_Configured()
    {
        foreach (var reset in new[] { false, true })
        {
            var t = new ActivityTown(Acts(new[] { Contract("ct", new[] { Interact(0, 0, 3, reset), Reach(100, 0) }) }));
            var a = t.Player("rook", Vec3d.Zero);
            t.Act.Contracts.Start(a.Id, "ct", 1);
            t.Act.Input.Set(a.Id, true);
            t.Wait(2);
            a.Vitals.SecondsSinceDamage = 0; // shot
            t.Tick();
            t.Wait(1.2); // 0.5 s interruption, then resumes
            var run = t.Act.Missions.RunOf(a.Id)!;
            Assert.Equal(0, run.Index); // not done yet either way
            t.Wait(reset ? 2.5 : 0.6);
            Assert.Equal(1, t.Act.Missions.RunOf(a.Id)!.Index);
        }
    }

    [Fact]
    public void Timeout_Fails_Without_Payout_And_Starts_Cooldown()
    {
        var t = new ActivityTown(Robbery(cooldown: 60, time: 5));
        var a = t.Player("rook", Vec3d.Zero);
        t.Act.Contracts.Start(a.Id, "ct_test", 1);
        t.Wait(5.5);
        var end = t.Events.OfType<ContractEnded>().Single();
        Assert.False(end.Success);
        Assert.Equal("timeout", end.Reason);
        Assert.Equal(0, t.Wallet(a));
        Assert.Equal("cooldown", t.Act.Contracts.Start(a.Id, "ct_test", 2).Error);
        t.Wait(60);
        Assert.True(t.Act.Contracts.Start(a.Id, "ct_test", 3).Ok);
    }

    [Fact]
    public void Party_Wipe_Fails_Contract()
    {
        var t = new ActivityTown(Robbery());
        var a = t.Player("rook", Vec3d.Zero);
        t.Act.Contracts.Start(a.Id, "ct_test", 1);
        a.Status |= ActorFlags.Dead;
        t.Tick();
        Assert.Equal("wiped", t.Events.OfType<ContractEnded>().Single().Reason);
    }

    [Fact]
    public void Leaving_Abandons_Solo_Run()
    {
        var t = new ActivityTown(Robbery());
        var a = t.Player("rook", Vec3d.Zero);
        t.Act.Contracts.Start(a.Id, "ct_test", 1);
        t.Act.OnPlayerLeft(a.Id);
        Assert.Equal("abandoned", t.Events.OfType<ContractEnded>().Single().Reason);
    }

    [Fact]
    public void Start_Validations()
    {
        var t = new ActivityTown(Robbery());
        var a = t.Player("rook", Vec3d.Zero);
        Assert.Equal("unknown_contract", t.Act.Contracts.Start(a.Id, "nope", 1).Error);
        a.Status |= ActorFlags.Passive;
        Assert.Equal("passive_mode", t.Act.Contracts.Start(a.Id, "ct_test", 2).Error);
        a.Status &= ~ActorFlags.Passive;
        Assert.True(t.Act.Contracts.Start(a.Id, "ct_test", 3).Ok);
        Assert.True(t.Act.Contracts.Start(a.Id, "ct_test", 3).Ok); // resend of the same request is a no-op
        Assert.Single(t.Act.Missions.Active);
        Assert.Equal("already_in_activity", t.Act.Contracts.Start(a.Id, "ct_test", 4).Error);
    }

    [Fact]
    public void Repeating_The_Same_Contract_Pays_Less()
    {
        var t = new ActivityTown(Robbery());
        var a = t.Player("rook", Vec3d.Zero);
        var paid = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(t.Act.Contracts.Start(a.Id, "ct_test", (ulong)(10 + i)).Ok);
            RunRobbery(t, a);
            paid.Add(t.Events.OfType<ContractEnded>().Last().TotalPaid);
        }
        Assert.Equal(paid[0], paid[1], 20.0);
        Assert.True(paid[2] < paid[1] * 0.85, string.Join(",", paid));
        Assert.True(paid[3] < paid[2], string.Join(",", paid));
    }

    [Fact]
    public void Carried_Item_Delivery_Can_Be_Stolen_By_Rival()
    {
        var t = new ActivityTown(Acts(new[] { Contract("ct_case", new[] { Deliver(0, 0, 40, 0, 1, 0) }) }));
        var a = t.Player("rook", Vec3d.Zero);
        var rival = t.Player("knox", new Vec3d(0.5, 0, 0.5));
        t.Act.Contracts.Start(a.Id, "ct_case", 1);
        t.Press(a);
        t.Tick();
        var item = t.Act.Carry.CarriedBy(a.Id)!;
        Assert.True((a.Status & ActorFlags.Carrying) != 0);

        a.Status |= ActorFlags.Downed; // dropped when downed
        t.Tick();
        Assert.Null(t.Act.Carry.CarriedBy(a.Id));
        Assert.True((a.Status & ActorFlags.Carrying) == 0);
        rival.Position = item.Position;
        t.Press(rival);
        t.Tick();
        Assert.Equal(rival.Id, t.Act.Carry.Get(item.Id)!.CarrierId);
        rival.Position = new Vec3d(40, 0, 0); // rival can't complete someone else's delivery
        t.Tick(3);
        Assert.Empty(t.Events.OfType<ItemDelivered>());
    }
}
