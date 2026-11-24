using Harbor.Core.Combat;
using Harbor.Core.Data;
using Harbor.Core.Economy;
using Harbor.Core.Geometry;
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
        Combat.SpawnPointFor = SpawnPointFor;
        foreach (var b in Static.All)
            if (b.Tag == "explosive") Combat.RegisterExplosive(b.Id);

        var ev = World.Events;
        ev.Subscribe<PlayerJoined>(OnJoined);
        ev.Subscribe<PlayerLeft>(OnLeft);
        ev.Subscribe<PlayerInputApplied>(OnInput);
        ev.Subscribe<ClientRequest>(OnRequest);
        ev.Subscribe<ActorKilled>(e =>
        {
            if (World.Actors.TryGet(e.VictimId, out var v) && v.Kind == ActorKind.Player) Economy.DropOnDeath(e.VictimId, e.KillerId);
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
            _ => ExtraRequest?.Invoke(e, body) ?? EconResult.Fail("unknown_request"),
        };
        if (!result.Ok) ToActors(WireEventType.RequestRejected, RequestCodec.Rejected(e.Type, body.Nonce, result.Error ?? "error"), e.ActorId);
    }

    /// <summary>Hook for request types handled by later modules (activities).</summary>
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

    /// <summary>Set by Activities: passive mode cannot be enabled during a contract or heist.</summary>
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
        var before = Combat.LoadoutOf(e.ActorId)?.Current?.InMagazine;
        Combat.ProcessInput(e.ActorId, input, World.Loop.StepSeconds);
        var l = Combat.LoadoutOf(e.ActorId);
        if (l?.Current is { } s && (before != s.InMagazine || input.Reload))
            ToActors(WireEventType.Ammo, WireEvents.Ammo(s.Def.Id, s.InMagazine, l.ReserveOf(s.Def.Class)), e.ActorId);
    }
}
