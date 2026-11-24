using Harbor.Core.Economy;
using Harbor.Core.Heat;
using Harbor.Core.Missions;
using Harbor.Core.Sim;

namespace Harbor.Core.Activities;

/// <summary>Multiplayer activities (CONTENT_DESIGN §6): contracts, heists, turf, supply drops, bounties.
/// Rule module — it never sees the network; <c>Server/GameServer</c> feeds it input and relays its events.</summary>
public sealed class ActivitySystem(ActivityData data, EconomySystem economy, HeatSystem heat) : ISimSystem
{
    public ActivityData Data { get; } = data;
    public EconomySystem Economy { get; } = economy;
    public HeatSystem Heat { get; } = heat;
    public SimWorld World { get; private set; } = null!;
    public double Now { get; private set; }

    public InteractInput Input { get; } = new();
    public CarrySystem Carry { get; private set; } = null!;
    public MissionRunner Missions { get; private set; } = null!;
    public ContractBoard Contracts { get; private set; } = null!;
    public TurfWar Turf { get; private set; } = null!;
    public SupplyDrops Supply { get; private set; } = null!;
    public Bounties Bounties { get; private set; } = null!;

    public void Initialize(SimWorld world)
    {
        World = world;
        Carry = new CarrySystem(world, Input);
        Missions = new MissionRunner(world, Carry, Input, (actor, crime) => Heat.Report(actor, crime));
        Contracts = new ContractBoard(this);
        Turf = new TurfWar(this);
        Supply = new SupplyDrops(this);
        Bounties = new Bounties(this);
        Economy.PriceModifier = Turf.PriceMultiplier;
    }

    public void Step(double dt)
    {
        Now += dt;
        Carry.Step();
        Missions.Step(dt);
        Contracts.Step();
        Turf.Step(dt);
        Supply.Step(dt);
        Bounties.Step(dt);
        Input.EndTick();
    }

    /// <summary>Busy players can't switch passive mode on (§6.6).</summary>
    public bool IsBusy(int actorId) => Missions.RunOf(actorId) != null || Carry.CarriedBy(actorId) != null;

    public void OnPlayerLeft(int actorId)
    {
        Carry.Drop(actorId);
        Missions.RemoveMember(actorId);
        Bounties.OnLeft(actorId);
        Input.Forget(actorId);
    }

    public void OnActorKilled(int victimId, int killerId) => Bounties.OnKilled(victimId, killerId);

    /// <summary>Online, bound player actors.</summary>
    internal IEnumerable<(Actor Actor, PlayerProfile Profile)> Players()
    {
        foreach (var a in World.Actors.All)
            if (a.Kind == ActorKind.Player && Economy.ProfileOf(a.Id) is { } p) yield return (a, p);
    }
}
