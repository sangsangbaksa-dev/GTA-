using Harbor.Core.Combat;
using Harbor.Core.Data;
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

    public GameServer(GameData data, ITransport transport, ILog log, StaticWorld? staticWorld = null, ServerConfig? config = null,
        MovementConfig? movement = null)
    {
        Data = data;
        Static = staticWorld ?? new StaticWorld();
        World = new SimWorld(log);
        Session = World.AddSystem(new ServerSession(transport, config, movement, Static));
        Combat = World.AddSystem(new CombatSystem(data.Combat, data.Materials, data.Weapons, Static, Session.History));
        foreach (var b in Static.All)
            if (b.Tag == "explosive") Combat.RegisterExplosive(b.Id);

        var ev = World.Events;
        ev.Subscribe<PlayerJoined>(e => Combat.Register(World.Actors.Get(e.ActorId)));
        ev.Subscribe<PlayerLeft>(e => Combat.Unregister(e.ActorId));
        ev.Subscribe<PlayerInputApplied>(OnInput);

        ev.Subscribe<ActorDamaged>(e => ToActors(WireEventType.Damaged, WireEvents.Damaged(e), e.VictimId, e.AttackerId));
        ev.Subscribe<ActorKilled>(e => Session.Broadcast(WireEventType.Killed, WireEvents.Killed(e)));
        ev.Subscribe<ActorDowned>(e => Session.Broadcast(WireEventType.Downed, WireEvents.ActorPair(e.VictimId, e.AttackerId)));
        ev.Subscribe<ActorRevived>(e => Session.Broadcast(WireEventType.Revived, WireEvents.ActorPair(e.VictimId, e.ReviverId)));
        ev.Subscribe<ActorRespawned>(e => ToActors(WireEventType.Respawned, WireEvents.Point(e.ActorId, e.Position), e.ActorId));
        ev.Subscribe<ExplosiveDetonated>(e => Session.Broadcast(WireEventType.Explosion, WireEvents.Point(e.BoxId, e.Position)));
    }

    public void Update(double frameSeconds) => World.Update(frameSeconds);

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
