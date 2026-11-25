using Harbor.Core.Combat;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Sim;

namespace Harbor.Core.Agents;

/// <summary>Server-side NPC brains (ADR 0007). Each NPC is a normal <see cref="Actor"/> registered with
/// <see cref="CombatSystem"/>; the brain turns perception into the same inputs a player would send
/// (an <see cref="InputCmd"/> for <see cref="Movement.Step"/> and a <see cref="CombatInput"/>), so NPCs obey
/// exactly the same movement, weapon and damage rules as players. Deterministic for a given seed.</summary>
public sealed class AgentSystem : ISimSystem
{
    public AgentData Data { get; }
    public IReadOnlyDictionary<int, Agent> Agents => _agents;
    /// <summary>Wanted level of a player (wired to Heat by the server composition; Agents must not depend on Heat).</summary>
    public Func<int, int> WantedLevel { get; set; } = _ => 0;
    public PoliceDispatch Police { get; }

    private readonly CombatSystem _combat;
    private readonly StaticWorld _static;
    private readonly MovementConfig _move;
    private readonly Random _rng;
    private readonly Dictionary<int, Agent> _agents = new();
    private readonly Dictionary<string, EncounterState> _encounters = new();
    private SimWorld _world = null!;
    private double _now;
    private double _thinkAccum = 1; // think on the first tick

    public sealed class Agent(Actor actor, ArchetypeDef def, FactionDef faction)
    {
        public Actor Actor { get; } = actor;
        public ArchetypeDef Def { get; } = def;
        public FactionDef Faction { get; } = faction;
        public string? EncounterId { get; init; }
        public Vec3d Home { get; set; }
        public double LeashRadius { get; set; } = 60;
        public int Target { get; internal set; } = -1;
        /// <summary>Police: the wanted player this unit was dispatched for.</summary>
        public int AssignedTo { get; internal set; } = -1;
        public Vec3d LastKnown { get; internal set; }
        public double LastSeenAt { get; internal set; } = double.NegativeInfinity;
        public double ReactionLeft { get; internal set; }
        public Vec3d? Alert { get; internal set; }
        public double AlertUntil { get; internal set; }
        public Vec3d? CoverSpot { get; internal set; }
        public double CoverUntil { get; internal set; }
        public double DeadFor { get; internal set; }
        internal double BurstLeft, PauseLeft, MeleeCooldown, StrafeSign = 1, StrafeSwitchAt, StuckFor, UnstickUntil;
        internal Vec3d? Wander;
        internal double WanderAt;
        internal Vec3d LastPos;
        internal double AimErrYaw, AimErrPitch;
        internal bool TriggerToggle;
        internal uint Seq;
        public bool IsPolice => AssignedTo >= 0 || Faction.Players == PlayerStance.Wanted;
    }

    private sealed class EncounterState(EncounterDef def)
    {
        public EncounterDef Def { get; } = def;
        public bool Active;
        public double RespawnAt = double.NegativeInfinity;
        public double EmptySince = double.NegativeInfinity;
        public readonly List<int> Members = new();
    }

    public AgentSystem(AgentData data, CombatSystem combat, StaticWorld staticWorld, MovementConfig? movement = null, int seed = 7)
    {
        Data = data;
        _combat = combat;
        _static = staticWorld;
        _move = movement ?? new MovementConfig();
        _rng = new Random(seed);
        Police = new PoliceDispatch(this);
        foreach (var e in data.Encounters) _encounters[e.Id] = new EncounterState(e);
        _combat.IncomingDamageScale = ShieldScale;
    }

    public void Initialize(SimWorld world)
    {
        _world = world;
        world.Events.Subscribe<ActorDamaged>(OnDamaged);
        world.Events.Subscribe<ShotFired>(OnShot);
        world.Events.Subscribe<ActorKilled>(OnKilled);
    }

    public double Now => _now;
    public StaticWorld StaticWorld => _static;
    internal SimWorld World => _world;
    internal Random Rng => _rng;

    // ------------------------------------------------------------------ spawning

    public Agent Spawn(string archetypeId, Vec3d position, string? encounterId = null, double leash = 60)
    {
        var def = Data.Archetypes[archetypeId];
        var faction = Data.Factions[def.Faction];
        var a = _world.Actors.Spawn(ActorKind.Npc, position);
        a.Name = def.Id;
        a.Team = faction.Team;
        var l = _combat.Register(a);
        l.Slots.Clear();
        l.Reserve.Clear();
        if (def.Weapon.Length > 0 && _combat.Weapons.TryGetValue(def.Weapon, out var w))
        {
            l.Add(w);
            l.Reserve[w.Class] = 10_000; // NPCs never run dry, they still reload
        }
        a.Vitals = Vitals.Full(def.Health);
        a.Vitals.Armor = def.Armor;
        a.Vitals.SecondsSinceDamage = 1000;
        a.Move.Yaw = _rng.NextDouble() * 2 * System.Math.PI;
        var agent = new Agent(a, def, faction) { EncounterId = encounterId, Home = position, LeashRadius = leash, LastPos = position };
        agent.StrafeSign = _rng.Next(2) == 0 ? -1 : 1;
        agent.WanderAt = _now + 1 + _rng.NextDouble() * 3;
        _agents[a.Id] = agent;
        _world.Events.Publish(new NpcSpawned(a.Id, def.Id, faction.Id, def.Model));
        return agent;
    }

    public void Despawn(int actorId)
    {
        if (!_agents.Remove(actorId, out var ag)) return;
        _combat.Unregister(actorId);
        _world.Actors.Remove(actorId);
        if (ag.EncounterId != null && _encounters.TryGetValue(ag.EncounterId, out var es)) es.Members.Remove(actorId);
        Police.Forget(actorId);
        _world.Events.Publish(new NpcDespawned(actorId));
    }

    public bool IsAgent(int actorId) => _agents.ContainsKey(actorId);
    public Agent? Get(int actorId) => _agents.GetValueOrDefault(actorId);
    public bool IsEncounterActive(string id) => _encounters.TryGetValue(id, out var e) && e.Active;

    // ------------------------------------------------------------------ events

    private double ShieldScale(Actor victim, Actor? attacker, string source)
    {
        if (attacker == null || source == "explosion" || !_agents.TryGetValue(victim.Id, out var ag) || ag.Def.ShieldFrontScale >= 1) return 1;
        var toAttacker = (attacker.Position - victim.Position).WithY(0).Normalized();
        var facing = Vec3d.FromYawPitch(victim.Move.Yaw, 0);
        var cos = toAttacker.Dot(facing);
        return cos >= System.Math.Cos(ag.Def.ShieldConeDeg * System.Math.PI / 360) ? ag.Def.ShieldFrontScale : 1;
    }

    private void OnDamaged(ActorDamaged e)
    {
        if (!_agents.TryGetValue(e.VictimId, out var ag) || e.AttackerId < 0 || !_world.Actors.TryGet(e.AttackerId, out var attacker)) return;
        if (attacker.Team == ag.Actor.Team) return;
        Provoke(ag, attacker);
        // the whole group reacts
        foreach (var other in _agents.Values)
            if (other != ag && other.Actor.Team == ag.Actor.Team && other.Target < 0 && Vec3d.Distance(other.Actor.Position, ag.Actor.Position) < 30)
                Provoke(other, attacker);
    }

    private void Provoke(Agent ag, Actor attacker)
    {
        if (ag.Target < 0) ag.ReactionLeft = ag.Def.ReactionSeconds * 0.5;
        ag.Target = attacker.Id;
        ag.LastKnown = attacker.Position;
        ag.LastSeenAt = _now;
        _provokedBy.Add((ag.Actor.Id, attacker.Id));
    }

    /// <summary>(npc, attacker) pairs: police and Never-stance factions fight back against whoever shot them.</summary>
    private readonly HashSet<(int Npc, int Attacker)> _provokedBy = new();

    private void OnShot(ShotFired e)
    {
        if (!_world.Actors.TryGet(e.ShooterId, out var shooter)) return;
        foreach (var ag in _agents.Values)
        {
            if (ag.Target >= 0 || ag.Actor.Team == shooter.Team || !ag.Actor.IsAlive) continue;
            if (Vec3d.Distance(ag.Actor.Position, e.Origin) > e.NoiseRadius) continue;
            ag.Alert = e.Origin;
            ag.AlertUntil = _now + 8;
        }
    }

    private void OnKilled(ActorKilled e)
    {
        foreach (var k in _provokedBy.Where(p => p.Npc == e.VictimId || p.Attacker == e.VictimId).ToList()) _provokedBy.Remove(k);
        if (!_agents.TryGetValue(e.VictimId, out var ag)) return;
        ag.Target = -1;
        _world.Events.Publish(new NpcKilled(e.VictimId, ag.Def.Id, ag.Faction.Id, e.KillerId, ag.Actor.Position));
    }

    // ------------------------------------------------------------------ hostility & perception

    public bool IsHostile(Agent ag, Actor other)
    {
        if (!other.IsAlive || other.Id == ag.Actor.Id || other.Team == ag.Actor.Team) return false;
        if ((other.Status & ActorFlags.Passive) != 0) return false;
        if (_provokedBy.Contains((ag.Actor.Id, other.Id))) return true;
        if (other.Kind == ActorKind.Player)
            return ag.Faction.Players switch
            {
                PlayerStance.Always => true,
                PlayerStance.Wanted => WantedLevel(other.Id) > 0,
                _ => false,
            };
        if (other.Kind == ActorKind.Npc && _agents.TryGetValue(other.Id, out var o))
            return ag.Faction.Enemies.Contains(o.Faction.Id) || o.Faction.Enemies.Contains(ag.Faction.Id);
        return false;
    }

    private static Vec3d Eye(Actor a) => a.Position + new Vec3d(0, a.Move.Crouched ? 1.1 : 1.6, 0);
    private static Vec3d Chest(Actor a) => a.Position + new Vec3d(0, a.Move.Crouched ? 0.7 : 1.2, 0);

    /// <summary>Line of sight between two actors' eyes and chest, blocked by any static box.</summary>
    public bool CanSee(Actor from, Actor to)
    {
        var a = Eye(from);
        var b = Chest(to);
        var d = b - a;
        var len = d.Length;
        return len < 1e-6 || !_static.Raycast(a, d, len, out _);
    }

    private Actor? Perceive(Agent ag)
    {
        var me = ag.Actor;
        var cosFov = System.Math.Cos(ag.Def.FovDeg * System.Math.PI / 360);
        var facing = Vec3d.FromYawPitch(me.Move.Yaw, 0);
        var alerted = ag.Target >= 0 || _now < ag.AlertUntil;
        Actor? best = null;
        var bestD = double.MaxValue;
        foreach (var o in _world.Actors.All)
        {
            if (o.Kind is not (ActorKind.Player or ActorKind.Npc) || !IsHostile(ag, o)) continue;
            var to = o.Position - me.Position;
            var d = to.Length;
            var range = ag.Def.SightRange * (o.Id == ag.Target ? 1.25 : 1) * (o.Move.Crouched && !alerted ? 0.6 : 1);
            if (d > range) continue;
            if (!alerted && d > 4 && to.WithY(0).Normalized().Dot(facing) < cosFov) continue;
            if (!CanSee(me, o)) continue;
            var score = d * (o.Id == ag.Target ? 0.7 : 1) * (o.Kind == ActorKind.Player ? 0.9 : 1);
            if (score < bestD) { bestD = score; best = o; }
        }
        return best;
    }

    // ------------------------------------------------------------------ tick

    public void Step(double dt)
    {
        _now += dt;
        StepEncounters();
        Police.Step(dt);

        _thinkAccum += dt * Data.ThinkHz;
        var think = _thinkAccum >= 1;
        if (think) _thinkAccum -= System.Math.Floor(_thinkAccum);

        foreach (var ag in _agents.Values.ToList())
        {
            var a = ag.Actor;
            if (!a.IsAlive)
            {
                ag.DeadFor += dt;
                if (ag.DeadFor >= Data.CorpseSeconds) Despawn(a.Id);
                continue;
            }
            if (think) Think(ag);
            Act(ag, dt);
        }
    }

    private void Think(Agent ag)
    {
        var seen = Perceive(ag);
        if (seen != null)
        {
            if (ag.Target != seen.Id && ag.Target < 0) ag.ReactionLeft = ag.Def.ReactionSeconds;
            ag.Target = seen.Id;
            ag.LastKnown = seen.Position;
            ag.LastSeenAt = _now;
            ag.Alert = null;
        }
        else if (ag.Target >= 0)
        {
            var gone = !_world.Actors.TryGet(ag.Target, out var t) || !IsHostile(ag, t);
            if (gone || _now - ag.LastSeenAt > 6) { ag.Alert = gone ? null : ag.LastKnown; ag.AlertUntil = _now + 6; ag.Target = -1; }
        }
        // police always know roughly where their suspect is (radio)
        if (ag.Target < 0 && ag.AssignedTo >= 0 && _world.Actors.TryGet(ag.AssignedTo, out var suspect) && IsHostile(ag, suspect))
        {
            ag.Alert = suspect.Position;
            ag.AlertUntil = _now + 2;
        }
        // leash: gang members don't chase across the city
        if (ag.AssignedTo < 0 && Vec3d.Distance(ag.Actor.Position, ag.Home) > ag.LeashRadius)
        {
            ag.Target = -1;
            ag.Alert = null;
            ag.AlertUntil = 0;
        }
    }

    private void Act(Agent ag, double dt)
    {
        var a = ag.Actor;
        var l = _combat.LoadoutOf(a.Id);
        var wish = Vec3d.Zero;
        var sprint = false;
        var crouch = false;
        var fire = false;
        var aimYaw = a.Move.Yaw;
        var aimPitch = 0.0;
        ag.MeleeCooldown -= dt;
        ag.ReactionLeft -= dt;

        Actor? target = null;
        if (ag.Target >= 0 && _world.Actors.TryGet(ag.Target, out var t) && t.IsAlive) target = t;

        if (target != null)
        {
            var to = target.Position - a.Position;
            var flat = to.WithY(0);
            var d = flat.Length;
            var dir = d > 1e-6 ? flat / d : Vec3d.FromYawPitch(a.Move.Yaw, 0);
            (aimYaw, aimPitch) = AimAt(a, target);
            var visible = _now - ag.LastSeenAt < 0.35;

            switch (ag.Def.Style)
            {
                case AgentStyle.Charge:
                    wish = dir;
                    sprint = d > 4;
                    if (d <= ag.Def.MeleeRange && ag.MeleeCooldown <= 0 && ag.ReactionLeft <= 0)
                    {
                        ag.MeleeCooldown = ag.Def.MeleeCooldown;
                        _combat.ApplyDamage(target, a, ag.Def.MeleeDamage, HitZone.Torso, false, "melee");
                    }
                    if (d <= ag.Def.MeleeRange * 0.8) wish = Vec3d.Zero;
                    break;

                case AgentStyle.Cover:
                {
                    var hurt = a.Vitals.Health < a.Vitals.MaxHealth * 0.5;
                    var suppressed = _combat.SuppressionOf(a.Id) > 0.5;
                    var reloading = l?.IsReloading == true;
                    if ((hurt || suppressed || reloading) && ag.CoverSpot == null && _now >= ag.CoverUntil)
                    {
                        ag.CoverSpot = FindCover(a.Position, target.Position, 14);
                        ag.CoverUntil = _now + 2.5 + _rng.NextDouble() * 2;
                    }
                    if (ag.CoverSpot is { } spot && _now < ag.CoverUntil)
                    {
                        var toSpot = (spot - a.Position).WithY(0);
                        if (toSpot.Length > 0.6) { wish = toSpot.Normalized(); sprint = toSpot.Length > 3; }
                        else crouch = true;
                        // peek and shoot from cover between pauses
                        fire = visible && !reloading && toSpot.Length <= 0.6 && ag.PauseLeft <= 0;
                        if (fire) crouch = false;
                    }
                    else
                    {
                        ag.CoverSpot = null;
                        wish = KeepRange(ag, dir, d);
                        fire = visible;
                    }
                    break;
                }

                case AgentStyle.Advance:
                    wish = d > ag.Def.PreferredRange ? dir : Vec3d.Zero;
                    fire = visible;
                    break;
            }

            if (fire && l?.Current != null && d <= ag.Def.AttackRange && ag.ReactionLeft <= 0) fire = Burst(ag, dt);
            else fire = false;
        }
        else
        {
            ag.CoverSpot = null;
            Vec3d? goal = null;
            if (ag.Alert is { } alert && _now < ag.AlertUntil) goal = alert;
            else if (Vec3d.Distance(a.Position, ag.Home) > 4) goal = ag.Home;
            else
            {
                if (_now >= ag.WanderAt)
                {
                    var ang = _rng.NextDouble() * 2 * System.Math.PI;
                    ag.Wander = _rng.NextDouble() < 0.5 ? null : ag.Home + new Vec3d(System.Math.Cos(ang), 0, System.Math.Sin(ang)) * (2 + _rng.NextDouble() * 5);
                    ag.WanderAt = _now + 4 + _rng.NextDouble() * 6;
                }
                goal = ag.Wander;
            }
            if (goal is { } g)
            {
                var to = (g - a.Position).WithY(0);
                if (to.Length > 1.0)
                {
                    wish = to.Normalized() * (ag.Alert != null ? 1 : 0.45);
                    sprint = ag.AssignedTo >= 0 && to.Length > 15;
                    aimYaw = System.Math.Atan2(-to.X, -to.Z);
                }
            }
        }

        wish = Unstick(ag, wish, dt);
        Drive(ag, wish, aimYaw, aimPitch, sprint, crouch, fire, l, dt);
    }

    private Vec3d KeepRange(Agent ag, Vec3d dir, double d)
    {
        var side = new Vec3d(-dir.Z, 0, dir.X) * ag.StrafeSign;
        if (_now >= ag.StrafeSwitchAt)
        {
            ag.StrafeSign = _rng.NextDouble() < 0.5 ? -1 : 1;
            ag.StrafeSwitchAt = _now + 1.5 + _rng.NextDouble() * 2.5;
        }
        var pref = ag.Def.PreferredRange;
        var radial = d > pref + 4 ? dir : d < pref - 4 ? -dir : Vec3d.Zero;
        return (radial + side * 0.7).Normalized();
    }

    /// <summary>Burst fire: hold the trigger for BurstSeconds, pause for BurstPauseSeconds. Semi/pump weapons need
    /// the trigger released between shots, so it is toggled every tick.</summary>
    private bool Burst(Agent ag, double dt)
    {
        if (ag.PauseLeft > 0) { ag.PauseLeft -= dt; return false; }
        if (ag.BurstLeft <= 0)
        {
            ag.BurstLeft = ag.Def.BurstSeconds * (0.7 + _rng.NextDouble() * 0.6);
            var err = ag.Def.AimErrorDeg * System.Math.PI / 180;
            ag.AimErrYaw = (_rng.NextDouble() * 2 - 1) * err;
            ag.AimErrPitch = (_rng.NextDouble() * 2 - 1) * err * 0.6;
        }
        // the first rounds of a burst miss by the initial error, then fire walks onto the target
        ag.AimErrYaw *= 0.82;
        ag.AimErrPitch *= 0.82;
        ag.BurstLeft -= dt;
        if (ag.BurstLeft <= 0) ag.PauseLeft = ag.Def.BurstPauseSeconds * (0.7 + _rng.NextDouble() * 0.6);
        var auto = _combat.LoadoutOf(ag.Actor.Id)?.Current?.Def.FireMode == FireMode.Auto;
        if (auto) return true;
        ag.TriggerToggle = !ag.TriggerToggle;
        return ag.TriggerToggle;
    }

    private static (double Yaw, double Pitch) AimAt(Actor from, Actor to)
    {
        var d = Chest(to) - Eye(from);
        var h = System.Math.Sqrt(d.X * d.X + d.Z * d.Z);
        return (System.Math.Atan2(-d.X, -d.Z), System.Math.Atan2(d.Y, System.Math.Max(h, 1e-6)));
    }

    /// <summary>A spot behind a nearby static box, on the side away from the threat, that the threat cannot see.</summary>
    public Vec3d? FindCover(Vec3d from, Vec3d threat, double searchRadius)
    {
        Vec3d? best = null;
        var bestScore = double.MaxValue;
        var q = new Aabb(from - new Vec3d(searchRadius, 0, searchRadius), from + new Vec3d(searchRadius, 3, searchRadius));
        foreach (var b in _static.Overlapping(q))
        {
            var size = b.Box.Max - b.Box.Min;
            if (size.Y < 0.7 || b.Tag == "explosive") continue;
            var c = (b.Box.Min + b.Box.Max) / 2;
            var away = (c - threat).WithY(0).Normalized();
            if (away.LengthSquared < 1e-6) continue;
            // project to the box face plus clearance
            var half = System.Math.Abs(away.X) * size.X / 2 + System.Math.Abs(away.Z) * size.Z / 2;
            var spot = (c + away * (half + _move.Radius + 0.35)).WithY(from.Y);
            var probe = new Aabb(spot - new Vec3d(_move.Radius, -0.1, _move.Radius), spot + new Vec3d(_move.Radius, 1.7, _move.Radius));
            if (_static.Overlapping(probe).Any()) continue;
            var eye = threat + new Vec3d(0, 1.5, 0);
            var target = spot + new Vec3d(0, 0.7, 0);
            var dv = target - eye;
            if (!_static.Raycast(eye, dv, dv.Length, out _)) continue; // not actually hidden when crouched
            var score = Vec3d.Distance(from, spot) + (Vec3d.Distance(spot, threat) < 6 ? 20 : 0);
            if (score < bestScore) { bestScore = score; best = spot; }
        }
        return best;
    }

    /// <summary>Very small local avoidance: if the agent tries to move but barely moves, side-step for a moment.</summary>
    private Vec3d Unstick(Agent ag, Vec3d wish, double dt)
    {
        var moved = Vec3d.Distance(ag.Actor.Position, ag.LastPos);
        ag.LastPos = ag.Actor.Position;
        if (_now < ag.UnstickUntil && wish.LengthSquared > 0.01)
        {
            var side = new Vec3d(-wish.Z, 0, wish.X) * ag.StrafeSign;
            return (side * 0.9 + wish * 0.3).Normalized();
        }
        if (wish.LengthSquared > 0.04 && moved < _move.WalkSpeed * dt * 0.25) ag.StuckFor += dt; else ag.StuckFor = 0;
        if (ag.StuckFor > 0.4)
        {
            ag.StuckFor = 0;
            ag.StrafeSign = -ag.StrafeSign;
            ag.UnstickUntil = _now + 0.8 + _rng.NextDouble() * 0.6;
        }
        return wish;
    }

    private void Drive(Agent ag, Vec3d wish, double yaw, double pitch, bool sprint, bool crouch, bool fire, Loadout? l, double dt)
    {
        var a = ag.Actor;
        var fwd = Vec3d.FromYawPitch(yaw, 0);
        var right = new Vec3d(-fwd.Z, 0, fwd.X);
        var buttons = InputButtons.None;
        if (sprint) buttons |= InputButtons.Sprint;
        if (crouch) buttons |= InputButtons.Crouch;
        if (fire) buttons |= InputButtons.Aim | InputButtons.Fire;
        // sprinting needs forward input: face the move direction while running
        if (sprint && wish.LengthSquared > 0.01 && !fire)
        {
            yaw = System.Math.Atan2(-wish.X, -wish.Z);
            fwd = Vec3d.FromYawPitch(yaw, 0);
            right = new Vec3d(-fwd.Z, 0, fwd.X);
        }
        var cmd = InputCmd.Create(++ag.Seq, 0, wish.Dot(right), wish.Dot(fwd), yaw, pitch, buttons);
        var mods = Movement.ModifiersFor(a.Status, _move);
        mods = mods with { SpeedMultiplier = mods.SpeedMultiplier * ag.Def.SpeedMultiplier };
        Movement.Step(ref a.Move, cmd, dt, _move, mods, _static);

        if (l?.Current == null) return;
        var reload = l.Current.InMagazine == 0 || (!fire && l.Current.InMagazine < l.Current.Def.MagazineSize / 3);
        var input = new CombatInput(fire, fire, reload, false, 0, cmd.YawRadians + ag.AimErrYaw, cmd.PitchRadians + ag.AimErrPitch, _world.Loop.Tick);
        _combat.ProcessInput(a.Id, input, dt);
    }

    // ------------------------------------------------------------------ encounters

    private void StepEncounters()
    {
        var players = _world.Actors.All.Where(p => p.Kind == ActorKind.Player).Select(p => p.Position).ToList();
        foreach (var es in _encounters.Values)
        {
            var c = es.Def.Center;
            var nearest = players.Count == 0 ? double.MaxValue : players.Min(p => Vec3d.Distance(p, c));
            if (!es.Active)
            {
                if (nearest <= Data.ActivationRadius && _now >= es.RespawnAt) Activate(es);
                continue;
            }
            es.Members.RemoveAll(id => !_agents.ContainsKey(id));
            var anyAlive = es.Members.Any(id => _world.Actors.TryGet(id, out var m) && m.IsAlive);
            if (!anyAlive)
            {
                // wiped out: corpses linger, the group comes back later
                if (es.Members.Count == 0) { es.Active = false; es.RespawnAt = _now + es.Def.RespawnSeconds; }
                continue;
            }
            if (nearest > Data.DeactivationRadius)
            {
                if (es.EmptySince < 0) es.EmptySince = _now;
                if (_now - es.EmptySince > 30)
                {
                    foreach (var id in es.Members.ToList()) Despawn(id);
                    es.Active = false;
                    es.RespawnAt = _now;
                }
            }
            else es.EmptySince = double.NegativeInfinity;
        }
    }

    private void Activate(EncounterState es)
    {
        es.Active = true;
        es.EmptySince = double.NegativeInfinity;
        var i = 0;
        foreach (var m in es.Def.Members)
            for (var n = 0; n < m.Count; n++, i++)
            {
                var pos = FreeSpotNear(es.Def.Center, es.Def.Radius, i);
                es.Members.Add(Spawn(m.Archetype, pos, es.Def.Id, es.Def.LeashRadius).Actor.Id);
            }
    }

    /// <summary>A point around <paramref name="center"/> that is not inside a static box (falls back to the centre).</summary>
    public Vec3d FreeSpotNear(Vec3d center, double radius, int salt)
    {
        for (var tries = 0; tries < 12; tries++)
        {
            var ang = (salt * 2.399963 + tries * 0.7) % (2 * System.Math.PI);
            var r = radius * (0.35 + 0.65 * ((salt * 37 + tries * 11) % 10) / 10.0);
            var p = center + new Vec3d(System.Math.Cos(ang) * r, 0, System.Math.Sin(ang) * r);
            var probe = new Aabb(p - new Vec3d(0.5, -0.1, 0.5), p + new Vec3d(0.5, 1.7, 0.5));
            if (!_static.Overlapping(probe).Any()) return p;
        }
        return center;
    }
}
