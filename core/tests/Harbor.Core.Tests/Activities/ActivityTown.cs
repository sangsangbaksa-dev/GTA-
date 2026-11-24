using Harbor.Core.Activities;
using Harbor.Core.Economy;
using Harbor.Core.Heat;
using Harbor.Core.Math;
using Harbor.Core.Missions;
using Harbor.Core.Sim;
using Xunit;

namespace Harbor.Core.Tests.Activities;

/// <summary>Rule-level harness: economy + heat + activities on a bare SimWorld (no network, no combat).
/// Players are plain actors; tests move them, press interact and kill them by hand.</summary>
internal sealed class ActivityTown
{
    public readonly SimWorld World = new(new NullLog());
    public readonly EconomySystem Econ;
    public readonly HeatSystem Heat;
    public readonly ActivitySystem Act;
    public readonly List<object> Events = new();
    public readonly List<(int Actor, string Item)> Granted = new();
    public double Dt => World.Loop.StepSeconds;

    private sealed class Receiver(ActivityTown t) : IItemReceiver
    {
        public string? CanReceive(int actorId, ShopItem item) => null;
        public void Receive(int actorId, ShopItem item) => t.Granted.Add((actorId, item.Id));
    }

    public ActivityTown(ActivityData? data = null, HeatConfig? heat = null)
    {
        Econ = World.AddSystem(new EconomySystem(Repo.Data.Economy) { Items = new Receiver(this) });
        Heat = World.AddSystem(new HeatSystem(heat ?? Repo.Data.Heat));
        Act = World.AddSystem(new ActivitySystem(data ?? Repo.Data.Activities, Econ, Heat));
        void Rec<T>() where T : Events.ISimEvent => World.Events.Subscribe<T>(e => Events.Add(e!));
        Rec<ContractStarted>(); Rec<ContractEnded>(); Rec<ObjectiveStarted>(); Rec<MissionEnded>(); Rec<ItemDelivered>();
        Rec<PrepCompleted>(); Rec<PrepLost>(); Rec<HeistSplitPending>(); Rec<HeistPaid>();
        Rec<TurfAttackScheduled>(); Rec<TurfAttackStarted>(); Rec<TurfCaptured>(); Rec<TurfDefended>(); Rec<TurfIncome>();
        Rec<SupplyDropAnnounced>(); Rec<SupplyDropLanded>(); Rec<SupplyDropOpened>(); Rec<SupplyDropExpired>();
        Rec<BountyPlaced>(); Rec<BountyClaimed>(); Rec<BountyCleared>(); Rec<WantedLevelChanged>(); Rec<CrimeCommitted>();
        Rec<ItemPickedUp>(); Rec<ItemDropped>();
    }

    public Actor Player(string id, Vec3d pos, long dirty = 0)
    {
        var a = World.Actors.Spawn(ActorKind.Player, pos);
        var p = Econ.GetOrCreateProfile(id, id);
        Econ.BindActor(a.Id, p);
        a.Vitals.SecondsSinceDamage = 100;
        if (dirty > 0) Econ.Move($"seed:{id}", AccountId.Faucet("seed"), AccountId.Wallet(id), dirty, Currency.Dirty, "seed");
        return a;
    }

    public Crew Crew(string name, params Actor[] members)
    {
        var leader = Econ.ProfileOf(members[0].Id)!;
        var c = Econ.Crews.Create(leader, name);
        foreach (var m in members.Skip(1)) Econ.Crews.Join(Econ.ProfileOf(m.Id)!, c.Id);
        foreach (var m in members) m.Team = c.Id;
        return c;
    }

    public PlayerProfile P(Actor a) => Econ.ProfileOf(a.Id)!;
    public long Wallet(Actor a) => Econ.Wallet(P(a).Id);

    public void Tick(int n = 1)
    {
        for (var i = 0; i < n; i++)
        {
            World.Update(Dt);
            foreach (var a in World.Actors.All) a.Vitals.SecondsSinceDamage += Dt; // stand-in for CombatSystem regen clock
        }
    }

    public void Wait(double seconds) => Tick((int)System.Math.Ceiling(seconds / Dt - 1e-9));

    /// <summary>Tick until <paramref name="cond"/> holds (or fail after <paramref name="maxSeconds"/>).</summary>
    public void Until(Func<bool> cond, double maxSeconds, string what)
    {
        for (var t = 0.0; t < maxSeconds; t += Dt)
        {
            if (cond()) return;
            Tick();
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    public void Press(Actor a) { Act.Input.Set(a.Id, false); Act.Input.Set(a.Id, true); }
    public void Release(Actor a) => Act.Input.Set(a.Id, false);

    public void Kill(Actor victim, Actor? killer)
    {
        victim.Status |= ActorFlags.Dead;
        Act.OnActorKilled(victim.Id, killer?.Id ?? -1);
        Heat.Clear(victim.Id);
    }

    public void AssertConserved()
    {
        Assert.Equal(0, Econ.Ledger.Total(Currency.Dirty));
        Assert.Equal(0, Econ.Ledger.Total(Currency.Clean));
    }

    // ------------------------------------------------------------------ data builders

    public static ObjectiveDef Reach(double x, double z, double r = 3, bool all = false) =>
        new() { Type = ObjectiveType.Reach, Label = "reach", At = new[] { x, 0, z }, Radius = r, All = all };
    public static ObjectiveDef Interact(double x, double z, double s, bool reset = false, string crime = "") =>
        new() { Type = ObjectiveType.Interact, Label = "interact", At = new[] { x, 0, z }, Radius = 2, Seconds = s, ResetOnInterrupt = reset, Crime = crime };
    public static ObjectiveDef Hold(double x, double z, double s, double r = 10) =>
        new() { Type = ObjectiveType.Hold, Label = "hold", At = new[] { x, 0, z }, Radius = r, Seconds = s };
    public static ObjectiveDef Escape(double x, double z, double d) =>
        new() { Type = ObjectiveType.Escape, Label = "escape", At = new[] { x, 0, z }, Radius = 5, Distance = d };
    public static ObjectiveDef Deliver(double x, double z, double tx, double tz, int count, long value) =>
        new() { Type = ObjectiveType.Deliver, Label = "deliver", Item = "prop_cash_bag", At = new[] { x, 0, z }, To = new[] { tx, 0, tz }, Count = count, ValueEach = value, Radius = 4 };

    public static ContractDef Contract(string id, IEnumerable<ObjectiveDef> objectives, long min = 1000, long max = 2000,
        double time = 300, double cooldown = 0, ContractKind kind = ContractKind.Contract, int minPlayers = 1) =>
        new() { Id = id, Kind = kind, Objectives = objectives.ToList(), Reward = new RewardDef(Currency.Dirty, min, max, 100),
            TimeLimitSeconds = time, CooldownSeconds = cooldown, MinPlayers = minPlayers };

    public static ActivityData Acts(IEnumerable<ContractDef> contracts, IEnumerable<HeistDef>? heists = null,
        TurfConfig? turf = null, SupplyDropConfig? supply = null) =>
        new(contracts.ToDictionary(c => c.Id), (heists ?? Array.Empty<HeistDef>()).ToDictionary(h => h.Id),
            turf ?? new TurfConfig(), supply ?? new SupplyDropConfig());
}
