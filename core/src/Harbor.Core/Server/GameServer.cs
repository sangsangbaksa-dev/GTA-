using Harbor.Core.Activities;
using Harbor.Core.Combat;
using Harbor.Core.Data;
using Harbor.Core.Economy;
using Harbor.Core.Geometry;
using Harbor.Core.Heat;
using Harbor.Core.Missions;
using Harbor.Core.Net;
using Harbor.Core.Ports;
using Harbor.Core.Sim;

namespace Harbor.Core.Server;

/// <summary>Composition root of the authoritative server: wires Net → rules (input → combat, requests →
/// economy/activities) and relays rule events back to clients. Hosted by the dedicated server process,
/// by Godot for local play, and by tests. Rule modules never reference Net; only this layer does.</summary>
public sealed class GameServer
{
    public GameData Data { get; }
    public SimWorld World { get; }
    public ServerSession Session { get; }
    public CombatSystem Combat { get; }
    public StaticWorld Static { get; }
    public EconomySystem Economy { get; }
    public HeatSystem Heat { get; }
    public ActivitySystem Activities { get; }
    /// <summary>Seconds between passive-mode toggles (CONTENT_DESIGN §6.6).</summary>
    public double PassiveCooldownSeconds { get; set; } = 300;

    private readonly Dictionary<int, double> _passiveToggledAt = new();

    public GameServer(GameData data, ITransport transport, ILog log, StaticWorld? staticWorld = null, ServerConfig? config = null,
        MovementConfig? movement = null)
    {
        Data = data;
        Static = staticWorld ?? new StaticWorld();
        World = new SimWorld(log);
        Session = World.AddSystem(new ServerSession(transport, config, movement, Static));
        Combat = World.AddSystem(new CombatSystem(data.Combat, data.Materials, data.Weapons, Static, Session.History));
        Economy = World.AddSystem(new EconomySystem(data.Economy) { Items = new CombatItemReceiver(this) });
        Heat = World.AddSystem(new HeatSystem(data.Heat));
        Activities = World.AddSystem(new ActivitySystem(data.Activities, Economy, Heat));
        ActivityBlocksPassive = Activities.IsBusy;
        Combat.SpawnPointFor = SpawnPointFor;
        foreach (var b in Static.All)
            if (b.Tag == "explosive") Combat.RegisterExplosive(b.Id);

        var ev = World.Events;
        ev.Subscribe<PlayerJoined>(OnJoined);
        ev.Subscribe<PlayerLeft>(OnLeft);
        ev.Subscribe<PlayerInputApplied>(OnInput);
        ev.Subscribe<ClientRequest>(OnRequest);
        ev.Subscribe<ActorKilled>(OnKilled);
        ev.Subscribe<ShotFired>(e => { if (IsPlayer(e.ShooterId)) Heat.Report(e.ShooterId, "shots_fired"); });
        ev.Subscribe<RepeatKillDetected>(e =>
        {
            // harassment (§6.6): +2 wanted levels and an immediate bounty
            Heat.AddLevels(e.KillerId, 2);
            Activities.Bounties.Place(e.KillerId, "harassment");
        });
        ev.Subscribe<BalanceChanged>(e => SendBalance(e.ProfileId));
        ev.Subscribe<CashDropped>(e => Session.Broadcast(WireEventType.CashDropped, WireEvents.Point(e.LootActorId, e.Position)));
        ev.Subscribe<PurchaseCompleted>(e => ToActors(WireEventType.Purchase, WireEvents.Text(e.ItemId), e.ActorId));

        ev.Subscribe<ActorDamaged>(e => ToActors(WireEventType.Damaged, WireEvents.Damaged(e), e.VictimId, e.AttackerId));
        ev.Subscribe<ActorKilled>(e => Session.Broadcast(WireEventType.Killed, WireEvents.Killed(e)));
        ev.Subscribe<ActorDowned>(e => Session.Broadcast(WireEventType.Downed, WireEvents.ActorPair(e.VictimId, e.AttackerId)));
        ev.Subscribe<ActorRevived>(e => Session.Broadcast(WireEventType.Revived, WireEvents.ActorPair(e.VictimId, e.ReviverId)));
        ev.Subscribe<ActorRespawned>(e => ToActors(WireEventType.Respawned, WireEvents.Point(e.ActorId, e.Position), e.ActorId));
        ev.Subscribe<ExplosiveDetonated>(e => Session.Broadcast(WireEventType.Explosion, WireEvents.Point(e.BoxId, e.Position)));
        WireActivities(ev);
    }

    private bool IsPlayer(int actorId) => World.Actors.TryGet(actorId, out var a) && a.Kind == ActorKind.Player;

    private void OnKilled(ActorKilled e)
    {
        if (!IsPlayer(e.VictimId)) return;
        Economy.DropOnDeath(e.VictimId, e.KillerId);
        Activities.OnActorKilled(e.VictimId, e.KillerId); // bounty claim before the victim's heat is cleared
        Heat.Clear(e.VictimId);
        if (e.KillerId != e.VictimId && IsPlayer(e.KillerId)) Heat.Report(e.KillerId, "murder");
    }

    private void WireActivities(Events.EventBus ev)
    {
        ev.Subscribe<ObjectiveStarted>(e => ToRun(e.RunId, WireEventType.ActivityUpdate, WireEvents.Objective(e.RunId, e.Index, e.Label, e.Position, 0)));
        ev.Subscribe<ObjectiveProgressed>(e =>
        {
            var o = Activities.Missions.Get(e.RunId)?.Current;
            if (o != null) ToRun(e.RunId, WireEventType.ActivityUpdate, WireEvents.Objective(e.RunId, e.Index, o.Label, o.AtPos, e.Fraction));
        });
        ev.Subscribe<ContractEnded>(e => ToActors(WireEventType.ActivityResult, WireEvents.Result(e.RunId, e.ContractId, e.Success, e.Reason, e.TotalPaid), e.Party.ToArray()));
        ev.Subscribe<HeistSplitPending>(e => ToActors(WireEventType.HeistSplit, WireEvents.Split(e.RunId, e.HeistId, e.Take, e.SecondsLeft, e.Members), e.MemberActors.ToArray()));
        ev.Subscribe<HeistPaid>(e => Notice("heist.paid", e.HeistId, Math.Vec3d.Zero, e.Shares.Values.Sum()));
        ev.Subscribe<PrepCompleted>(e => ToCrew(e.CrewId, WireEventType.Notice, WireEvents.Notice("heist.prep_done", e.PrepId, Math.Vec3d.Zero, 0)));
        ev.Subscribe<PrepLost>(e => ToCrew(e.CrewId, WireEventType.Notice, WireEvents.Notice("heist.prep_lost", e.PrepId, Math.Vec3d.Zero, 0)));

        ev.Subscribe<SupplyDropAnnounced>(e => Notice("supply.announced", e.SiteId, e.Position, (long)e.LandsInSeconds));
        ev.Subscribe<SupplyDropLanded>(e => Notice("supply.landed", e.DropId.ToString(System.Globalization.CultureInfo.InvariantCulture), e.Position, e.CrateActorId));
        ev.Subscribe<SupplyDropOpened>(e => Notice("supply.opened", e.DropId.ToString(System.Globalization.CultureInfo.InvariantCulture), Math.Vec3d.Zero, e.ActorId));

        Math.Vec3d ZonePos(string id) => Activities.Turf.Get(id)?.Def.Center ?? Math.Vec3d.Zero;
        ev.Subscribe<TurfAttackScheduled>(e => Notice("turf.attack_soon", e.ZoneId, ZonePos(e.ZoneId), (long)e.StartsInSeconds));
        ev.Subscribe<TurfAttackStarted>(e => Notice("turf.attack_open", e.ZoneId, ZonePos(e.ZoneId), (long)e.WindowSeconds));
        ev.Subscribe<TurfCaptured>(e => Notice("turf.captured", e.ZoneId, ZonePos(e.ZoneId), e.NewCrew));
        ev.Subscribe<TurfDefended>(e => Notice("turf.defended", e.ZoneId, ZonePos(e.ZoneId), e.OwnerCrew));

        ev.Subscribe<BountyPlaced>(e => Notice("bounty.placed", e.TargetActorId.ToString(System.Globalization.CultureInfo.InvariantCulture), Math.Vec3d.Zero, e.Amount));
        ev.Subscribe<BountyClaimed>(e => Notice("bounty.claimed", e.TargetActorId.ToString(System.Globalization.CultureInfo.InvariantCulture), Math.Vec3d.Zero, e.Amount));
        ev.Subscribe<BountyCleared>(e => Notice("bounty.cleared", e.TargetActorId.ToString(System.Globalization.CultureInfo.InvariantCulture), Math.Vec3d.Zero, 0));
        ev.Subscribe<WantedLevelChanged>(e => ToActors(WireEventType.Wanted, new byte[] { (byte)e.NewLevel }, e.ActorId));
    }

    private void Notice(string kind, string id, Math.Vec3d pos, long value) =>
        Session.Broadcast(WireEventType.Notice, WireEvents.Notice(kind, id, pos, value));

    private void ToRun(int runId, ushort type, byte[] payload)
    {
        if (Activities.Missions.Get(runId) is { } run) ToActors(type, payload, run.Party.ToArray());
    }

    private void ToCrew(int crewId, ushort type, byte[] payload)
    {
        if (Economy.Crews.Get(crewId) is not { } crew) return;
        ToActors(type, payload, crew.Members.Select(m => Economy.ActorOf(m) ?? -1).ToArray());
    }

    public void Update(double frameSeconds) => World.Update(frameSeconds);

    private void OnJoined(PlayerJoined e)
    {
        var actor = World.Actors.Get(e.ActorId);
        // TODO(S1): authenticate and use an account id instead of the display name
        var profile = Economy.GetOrCreateProfile(e.Name.ToLowerInvariant(), e.Name);
        Economy.BindActor(actor.Id, profile);
        actor.Team = profile.CrewId;
        Combat.Register(actor);
        foreach (var w in profile.OwnedWeapons) Combat.GiveWeapon(actor.Id, w);
        actor.Position = SpawnPointFor(actor);
        SendBalance(profile.Id);
    }

    private void OnLeft(PlayerLeft e)
    {
        var p = Economy.ProfileOf(e.ActorId);
        if (p != null && World.Actors.TryGet(e.ActorId, out var a) && Economy.SafehouseAt(p, a.Position) == null)
            Economy.Penalize(p.Id, Data.Economy.Config.LogoutOutsideSafehouseTax, "logout_tax");
        Activities.OnPlayerLeft(e.ActorId);
        Heat.Clear(e.ActorId);
        Economy.UnbindActor(e.ActorId);
        Combat.Unregister(e.ActorId);
        _passiveToggledAt.Remove(e.ActorId);
    }

    /// <summary>Nearest owned safehouse, else the session spawn point.</summary>
    private Math.Vec3d SpawnPointFor(Actor a)
    {
        var p = Economy.ProfileOf(a.Id);
        var sh = p == null ? null : Data.Economy.Safehouses.Where(s => p.OwnedSafehouses.Contains(s.Id))
            .OrderBy(s => (s.Center - a.Position).Length).FirstOrDefault();
        var spread = new Math.Vec3d((a.Id % 8) * 1.2, 0, (a.Id / 8 % 4) * 1.2); // don't stack players
        return (sh?.Center ?? Session.Config.SpawnPoint) + spread;
    }

    private void SendBalance(string profileId)
    {
        if (Economy.ActorOf(profileId) is not { } actorId) return;
        ToActors(WireEventType.Balance, RequestCodec.Balance(Economy.Wallet(profileId), Economy.Bank(profileId), Economy.Stash(profileId)), actorId);
    }

    private void OnRequest(ClientRequest e)
    {
        RequestBody body;
        try { body = RequestBody.Decode(e.Payload); }
        catch (NetFormatException) { return; }
        var result = e.Type switch
        {
            RequestType.Buy => Economy.Buy(e.ActorId, body.A, RequestCodec.ParseCurrency(body.X), body.Nonce),
            RequestType.LaunderInstant => Economy.LaunderInstant(e.ActorId, body.X, body.Nonce),
            RequestType.LaunderAtBusiness => Economy.LaunderAtBusiness(e.ActorId, body.A, body.X, body.Nonce),
            RequestType.BuyBusiness => Economy.BuyBusiness(e.ActorId, body.A, body.Nonce),
            RequestType.StashDeposit => Economy.StashDeposit(e.ActorId, body.X, body.Nonce),
            RequestType.StashWithdraw => Economy.StashWithdraw(e.ActorId, body.X, body.Nonce),
            RequestType.CrewCreate => CrewCreate(e.ActorId, body.A),
            RequestType.CrewJoin => CrewJoin(e.ActorId, (int)body.X),
            RequestType.CrewDeposit => Economy.CrewDeposit(e.ActorId, RequestCodec.ParseCurrency(body.Y), body.X, body.Nonce),
            RequestType.CrewWithdraw => Economy.CrewWithdraw(e.ActorId, body.A, RequestCodec.ParseCurrency(body.Y), body.X, body.Nonce),
            RequestType.TogglePassive => TogglePassive(e.ActorId),
            RequestType.ContractStart => Activities.Contracts.Start(e.ActorId, body.A, body.Nonce),
            RequestType.ContractAbandon => Activities.Contracts.Abandon(e.ActorId),
            RequestType.HeistSplit => HeistSplit(e.ActorId, (int)body.Y, body.X),
            RequestType.CarryDrop => Activities.Carry.Drop(e.ActorId) ? EconResult.Success : EconResult.Fail("not_carrying"),
            _ => ExtraRequest?.Invoke(e, body) ?? EconResult.Fail("unknown_request"),
        };
        if (!result.Ok) ToActors(WireEventType.RequestRejected, RequestCodec.Rejected(e.Type, body.Nonce, result.Error ?? "error"), e.ActorId);
    }

    private EconResult HeistSplit(int actorId, int runId, long packed)
    {
        var members = Activities.Contracts.SplitMembers(runId);
        if (members == null) return EconResult.Fail("no_split");
        return Activities.Contracts.SetSplit(actorId, runId, ContractBoard.UnpackPercents(packed, members.Count));
    }

    /// <summary>Hook for request types handled by mods or later modules.</summary>
    public Func<ClientRequest, RequestBody, EconResult>? ExtraRequest { get; set; }

    private EconResult CrewCreate(int actorId, string name)
    {
        var p = Economy.ProfileOf(actorId);
        if (p == null || name.Length is < 2 or > 24) return EconResult.Fail("invalid");
        var crew = Economy.Crews.Create(p, name);
        World.Actors.Get(actorId).Team = crew.Id;
        return EconResult.Success;
    }

    private EconResult CrewJoin(int actorId, int crewId)
    {
        // TODO: invitations; for now any open crew can be joined
        var p = Economy.ProfileOf(actorId);
        if (p == null || !Economy.Crews.Join(p, crewId)) return EconResult.Fail("cannot_join");
        World.Actors.Get(actorId).Team = crewId;
        return EconResult.Success;
    }

    private EconResult TogglePassive(int actorId)
    {
        var p = Economy.ProfileOf(actorId);
        if (p == null || !World.Actors.TryGet(actorId, out var a)) return EconResult.Fail("no_profile");
        if (Economy.SafehouseAt(p, a.Position) == null) return EconResult.Fail("not_in_safehouse");
        var now = Economy.Now;
        if (_passiveToggledAt.TryGetValue(actorId, out var last) && now - last < PassiveCooldownSeconds) return EconResult.Fail("cooldown");
        if (ActivityBlocksPassive?.Invoke(actorId) == true) return EconResult.Fail("in_activity");
        _passiveToggledAt[actorId] = now;
        a.Status ^= ActorFlags.Passive;
        return EconResult.Success;
    }

    /// <summary>Passive mode cannot be enabled during a contract or heist, or while carrying (defaults to Activities.IsBusy).</summary>
    public Func<int, bool>? ActivityBlocksPassive { get; set; }

    private sealed class CombatItemReceiver(GameServer s) : IItemReceiver
    {
        public string? CanReceive(int actorId, ShopItem item)
        {
            var l = s.Combat.LoadoutOf(actorId);
            if (l == null || !s.World.Actors.TryGet(actorId, out var a)) return "unavailable";
            return item.Kind switch
            {
                ShopItemKind.Weapon when l.Has(item.Ref) => "already_owned",
                ShopItemKind.Weapon when l.Slots.Count >= Loadout.MaxSlots => "no_free_slot",
                ShopItemKind.Armor when a.Vitals.Armor >= s.Data.Combat.Armor.Max => "armor_full",
                ShopItemKind.Medkit when a.Vitals.Health >= a.Vitals.MaxHealth => "health_full",
                ShopItemKind.Ammo when !Enum.TryParse<WeaponClass>(item.Ref, true, out _) => "invalid",
                ShopItemKind.Mod => "not_available",
                _ => null,
            };
        }

        public void Receive(int actorId, ShopItem item)
        {
            var a = s.World.Actors.Get(actorId);
            switch (item.Kind)
            {
                case ShopItemKind.Weapon: s.Combat.GiveWeapon(actorId, item.Ref); break;
                case ShopItemKind.Ammo: s.Combat.AddReserveAmmo(actorId, Enum.Parse<WeaponClass>(item.Ref, true), item.Amount); break;
                case ShopItemKind.Armor: s.Combat.AddArmor(a, item.Amount); break;
                case ShopItemKind.Medkit: s.Combat.Heal(a, item.Amount); break;
            }
        }
    }

    /// <summary>Send a reliable event to the owners of the given actors (ignores NPCs / unknown ids, de-duplicated).</summary>
    public void ToActors(ushort type, byte[] payload, params int[] actorIds)
    {
        foreach (var id in actorIds.Distinct())
            if (id >= 0 && World.Actors.TryGet(id, out var a) && a.OwnerPeer >= 0)
                Session.SendEvent(a.OwnerPeer, type, payload);
    }

    private void OnInput(PlayerInputApplied e)
    {
        var c = e.Cmd;
        var input = new CombatInput(c.Has(InputButtons.Fire), c.Has(InputButtons.Aim), c.Has(InputButtons.Reload),
            c.Has(InputButtons.Interact), c.WeaponSlot, c.YawRadians, c.PitchRadians, e.ViewTick);
        Activities.Input.Set(e.ActorId, input.Interact);
        var before = Combat.LoadoutOf(e.ActorId)?.Current?.InMagazine;
        Combat.ProcessInput(e.ActorId, input, World.Loop.StepSeconds);
        var l = Combat.LoadoutOf(e.ActorId);
        if (l?.Current is { } s && (before != s.InMagazine || input.Reload))
            ToActors(WireEventType.Ammo, WireEvents.Ammo(s.Def.Id, s.InMagazine, l.ReserveOf(s.Def.Class)), e.ActorId);
    }
}
