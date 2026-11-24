using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Sim;

namespace Harbor.Core.Combat;

/// <summary>Server-side combat rules: firing, lag-compensated hits, penetration, armor, downed/revive,
/// regen, respawn, explosive props, suppression. Inputs arrive through <see cref="ProcessInput"/>
/// (the server composition translates network input); results leave as events.</summary>
public sealed class CombatSystem : ISimSystem
{
    public CombatConfig Config { get; }
    public MaterialTable Materials { get; }
    public IReadOnlyDictionary<string, WeaponDef> Weapons { get; }
    public string DefaultWeaponId { get; set; } = "kestrel_p10";
    /// <summary>Where a player respawns (activities/safehouses override).</summary>
    public Func<Actor, Vec3d> SpawnPointFor { get; set; } = _ => Vec3d.Zero;
    /// <summary>Whether a downed state applies (co-op vs PvP rules); default: players downed by non-players.</summary>
    public Func<Actor, Actor?, bool> UsesDownedState { get; set; }

    private readonly StaticWorld _static;
    private readonly LagCompHistory? _history;
    private readonly Dictionary<int, Loadout> _loadouts = new();
    private readonly Dictionary<int, double> _suppression = new();
    private readonly Dictionary<int, double> _respawnTimers = new();
    private readonly Dictionary<int, (int Target, double Progress)> _revives = new();
    private readonly Dictionary<int, ExplosiveState> _explosives = new();
    private readonly List<(double Delay, int BoxId, int By)> _pendingExplosions = new();
    private readonly Dictionary<(int Killer, int Victim), List<double>> _killLog = new();
    private SimWorld _world = null!;
    private double _time;

    private sealed class ExplosiveState(int health) { public int Health = health; }

    public CombatSystem(CombatConfig config, MaterialTable materials, IReadOnlyDictionary<string, WeaponDef> weapons,
        StaticWorld staticWorld, LagCompHistory? history)
    {
        Config = config;
        Materials = materials;
        Weapons = weapons;
        _static = staticWorld;
        _history = history;
        UsesDownedState = (victim, attacker) => victim.Kind == ActorKind.Player && attacker?.Kind != ActorKind.Player;
    }

    public void Initialize(SimWorld world) => _world = world;

    // ------------------------------------------------------------------ setup / inventory

    public Loadout Register(Actor actor)
    {
        actor.Vitals = Vitals.Full(Config.Health.Max);
        var l = new Loadout();
        if (Weapons.TryGetValue(DefaultWeaponId, out var def))
        {
            l.Add(def);
            l.Reserve[def.Class] = def.MagazineSize * 2;
        }
        _loadouts[actor.Id] = l;
        return l;
    }

    public void Unregister(int actorId)
    {
        _loadouts.Remove(actorId);
        _suppression.Remove(actorId);
        _respawnTimers.Remove(actorId);
        _revives.Remove(actorId);
    }

    public Loadout? LoadoutOf(int actorId) => _loadouts.GetValueOrDefault(actorId);

    public bool GiveWeapon(int actorId, string weaponId)
        => _loadouts.TryGetValue(actorId, out var l) && Weapons.TryGetValue(weaponId, out var def) && l.Add(def);

    public void AddReserveAmmo(int actorId, WeaponClass cls, int rounds)
    {
        if (_loadouts.TryGetValue(actorId, out var l)) l.Reserve[cls] = l.ReserveOf(cls) + rounds;
    }

    /// <summary>Adds armor up to the cap; returns how much was actually added.</summary>
    public int AddArmor(Actor a, int amount)
    {
        var before = a.Vitals.Armor;
        a.Vitals.Armor = System.Math.Min(Config.Armor.Max, a.Vitals.Armor + amount);
        return a.Vitals.Armor - before;
    }

    public int Heal(Actor a, int amount)
    {
        if (!a.IsAlive || (a.Status & ActorFlags.Downed) != 0) return 0;
        var before = a.Vitals.Health;
        a.Vitals.Health = System.Math.Min(a.Vitals.MaxHealth, a.Vitals.Health + amount);
        return a.Vitals.Health - before;
    }

    /// <summary>Registers a static box (tag "explosive") as a destructible explosive.</summary>
    public void RegisterExplosive(int boxId) => _explosives[boxId] = new ExplosiveState(Config.Explosive.Health);

    public double SuppressionOf(int actorId) => _suppression.GetValueOrDefault(actorId);

    // ------------------------------------------------------------------ per tick

    public void Step(double dt)
    {
        _time += dt;
        foreach (var l in _loadouts.Values)
        {
            // may go slightly negative so high-RPM weapons keep their exact rate at a 30 Hz tick;
            // floored at -dt so an idle weapon cannot bank a burst
            l.Cooldown = System.Math.Max(-dt, l.Cooldown - dt);
            if (l.ReloadLeft > 0)
            {
                l.ReloadLeft -= dt;
                if (l.ReloadLeft <= 0) FinishReload(l);
            }
        }
        foreach (var id in _suppression.Keys.ToList())
        {
            var v = _suppression[id] - Config.Suppression.DecayPerSecond * dt;
            if (v <= 0) _suppression.Remove(id); else _suppression[id] = v;
        }
        foreach (var a in _world.Actors.All.ToList()) StepVitals(a, dt);
        for (var i = _pendingExplosions.Count - 1; i >= 0; i--)
        {
            var p = _pendingExplosions[i];
            p.Delay -= dt;
            if (p.Delay <= 0) { _pendingExplosions.RemoveAt(i); Detonate(p.BoxId, p.By); }
            else _pendingExplosions[i] = p;
        }
    }

    private void StepVitals(Actor a, double dt)
    {
        if (!_loadouts.ContainsKey(a.Id)) return;
        ref var v = ref a.Vitals;
        if (!a.IsAlive)
        {
            if (a.Kind != ActorKind.Player || !_respawnTimers.ContainsKey(a.Id)) return;
            _respawnTimers[a.Id] -= dt;
            if (_respawnTimers[a.Id] <= 0) Respawn(a);
            return;
        }
        if ((a.Status & ActorFlags.Downed) != 0)
        {
            v.DownedSecondsLeft -= dt;
            if (v.DownedSecondsLeft <= 0) Kill(a, -1, "bleed_out", false);
            return;
        }
        v.SecondsSinceDamage += dt;
        if (v.SecondsSinceDamage >= Config.Health.RegenDelaySeconds && v.Health < Config.Health.RegenCap)
        {
            // integer regen with fractional carry kept in SecondsSinceDamage-independent accumulator
            _regenCarry[a.Id] = _regenCarry.GetValueOrDefault(a.Id) + Config.Health.RegenPerSecond * dt;
            var whole = (int)_regenCarry[a.Id];
            if (whole > 0)
            {
                _regenCarry[a.Id] -= whole;
                v.Health = System.Math.Min(Config.Health.RegenCap, v.Health + whole);
            }
        }
    }

    private readonly Dictionary<int, double> _regenCarry = new();

    // ------------------------------------------------------------------ input

    public void ProcessInput(int actorId, in CombatInput input, double dt)
    {
        if (!_world.Actors.TryGet(actorId, out var actor) || !_loadouts.TryGetValue(actorId, out var l)) return;
        if (!actor.IsAlive || (actor.Status & ActorFlags.Downed) != 0) { l.TriggerWasHeld = input.Fire; return; }

        if (input.Interact) TickRevive(actor, dt); else _revives.Remove(actorId);

        if (input.Slot != l.Active && input.Slot < l.Slots.Count)
        {
            l.Active = input.Slot;
            l.ReloadLeft = 0;
            l.Cooldown = System.Math.Max(l.Cooldown, Loadout.SwapSeconds);
        }
        var slot = l.Current;
        if (slot == null) return;

        if (input.Reload) TryStartReload(l);

        if (input.Fire)
        {
            var needsRelease = slot.Def.FireMode != FireMode.Auto;
            if (!(needsRelease && l.TriggerWasHeld) && l.Cooldown <= 0 && !l.IsReloading)
            {
                if (slot.InMagazine == 0) TryStartReload(l);
                // auto weapons may fire more than once per tick (850 rpm > 30 Hz / 2)
                var shots = 0;
                while (l.Cooldown <= 0 && slot.InMagazine > 0 && shots < 4 && (shots == 0 || !needsRelease))
                {
                    Fire(actor, l, slot, input, shots);
                    shots++;
                }
            }
        }
        l.TriggerWasHeld = input.Fire;
    }

    private void TryStartReload(Loadout l)
    {
        var s = l.Current;
        if (s == null || l.IsReloading || s.InMagazine >= s.Def.MagazineSize || l.ReserveOf(s.Def.Class) <= 0) return;
        l.ReloadLeft = s.Def.ReloadSeconds * (s.Def.FireMode == FireMode.Pump ? s.Def.MagazineSize - s.InMagazine : 1);
    }

    private static void FinishReload(Loadout l)
    {
        l.ReloadLeft = 0;
        var s = l.Current;
        if (s == null) return;
        var need = s.Def.MagazineSize - s.InMagazine;
        var take = System.Math.Min(need, l.ReserveOf(s.Def.Class));
        s.InMagazine += take;
        l.Reserve[s.Def.Class] = l.ReserveOf(s.Def.Class) - take;
    }

    private void Fire(Actor shooter, Loadout l, WeaponSlot slot, in CombatInput input, int shotInTick)
    {
        var w = slot.Def;
        slot.InMagazine--;
        l.Cooldown = System.Math.Max(l.Cooldown, -_world.Loop.StepSeconds) + w.SecondsPerShot;

        var eye = shooter.Position + new Vec3d(0, shooter.Move.Crouched ? Config.EyeHeightCrouched : Config.EyeHeight, 0);
        var targets = PosesAt(input.ViewTick);
        var spreadDeg = (input.Aim ? w.SpreadDeg.Aim : w.SpreadDeg.Hip)
                        * (1 + (Config.Suppression.SpreadMultiplierAtMax - 1) * System.Math.Min(1, SuppressionOf(shooter.Id)));
        var tick = (ulong)_world.Loop.Tick;
        Vec3d firstEnd = eye;
        var damageByVictim = new Dictionary<int, (double Raw, HitZone Zone, bool Head)>();

        for (var p = 0; p < w.Pellets; p++)
        {
            var rng = new SplitMix((ulong)shooter.Id * 0x9E3779B97F4A7C15UL ^ tick * 0xBF58476D1CE4E5B9UL
                                   ^ (ulong)(p + 64 * shotInTick) * 0x94D049BB133111EBUL);
            var dir = Spread(Vec3d.FromYawPitch(input.Yaw, input.Pitch), spreadDeg, rng);
            var tr = Ballistics.Trace(eye, dir, w.MaxRange, shooter.Id, targets, _static, Materials, w.Class);
            if (p == 0) firstEnd = tr.End;
            foreach (var imp in tr.Impacts)
                if (imp.Box.Tag == "explosive") DamageExplosive(imp.Box.Id, (int)System.Math.Round(w.Damage), shooter.Id);
            if (tr.Hit is { } hit)
            {
                var raw = DamageModel.Raw(w, hit.Zone, hit.Distance, hit.PenetrationScale, Config);
                var prev = damageByVictim.GetValueOrDefault(hit.ActorId);
                var worstZone = prev.Raw == 0 || hit.Zone < prev.Zone ? hit.Zone : prev.Zone;
                damageByVictim[hit.ActorId] = (prev.Raw + raw, worstZone, prev.Head || hit.Zone == HitZone.Head);
            }
        }

        _world.Events.Publish(new ShotFired(shooter.Id, w.Id, eye, firstEnd, w.NoiseRadiusM));
        Suppress(shooter.Id, eye, firstEnd, damageByVictim.Keys);
        foreach (var (victimId, d) in damageByVictim)
            if (_world.Actors.TryGet(victimId, out var victim))
                ApplyDamage(victim, shooter, d.Raw, d.Zone, d.Head, w.Id);
    }

    private IReadOnlyDictionary<int, PoseSample> PosesAt(long viewTick)
    {
        var frame = _history?.Frame(viewTick);
        if (frame != null) return frame;
        return _world.Actors.All.Where(a => a.Kind is ActorKind.Player or ActorKind.Npc)
            .ToDictionary(a => a.Id, a => new PoseSample(a.Position, a.Move.Crouched, a.IsAlive));
    }

    private static Vec3d Spread(Vec3d dir, double spreadDeg, SplitMix rng)
    {
        if (spreadDeg <= 0) return dir;
        // uniform in a cone of half-angle spreadDeg
        var maxAngle = spreadDeg * System.Math.PI / 180;
        var cosMax = System.Math.Cos(maxAngle);
        var z = 1 - rng.NextDouble() * (1 - cosMax);
        var phi = rng.NextDouble() * 2 * System.Math.PI;
        var r = System.Math.Sqrt(System.Math.Max(0, 1 - z * z));
        var up = System.Math.Abs(dir.Y) < 0.99 ? Vec3d.Up : new Vec3d(1, 0, 0);
        var u = Cross(up, dir).Normalized();
        var v = Cross(dir, u);
        return (dir * z + u * (r * System.Math.Cos(phi)) + v * (r * System.Math.Sin(phi))).Normalized();
    }

    private static Vec3d Cross(Vec3d a, Vec3d b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private void Suppress(int shooterId, Vec3d from, Vec3d to, IEnumerable<int> hitIds)
    {
        var hit = hitIds.ToHashSet();
        foreach (var a in _world.Actors.All)
        {
            if (a.Id == shooterId || hit.Contains(a.Id) || !a.IsAlive || a.Kind is not (ActorKind.Player or ActorKind.Npc)) continue;
            var center = a.Position + new Vec3d(0, 1.2, 0);
            if (Ballistics.DistanceToSegment(center, from, to) <= Config.Suppression.Radius)
                _suppression[a.Id] = System.Math.Min(1.0, SuppressionOf(a.Id) + Config.Suppression.PerShot);
        }
    }

    // ------------------------------------------------------------------ damage & state

    /// <summary>Applies already-computed raw damage (bullets, explosions, melee, scripted).</summary>
    public void ApplyDamage(Actor victim, Actor? attacker, double raw, HitZone zone, bool headshot, string source)
    {
        if (!victim.IsAlive) return;
        if (IsProtected(victim, attacker)) return;
        var downed = (victim.Status & ActorFlags.Downed) != 0;
        var r = DamageModel.Apply(ref victim.Vitals, raw, bypassArmor: zone == HitZone.Head || downed, Config);
        _world.Events.Publish(new ActorDamaged(victim.Id, attacker?.Id ?? -1, r.HealthLost, r.ArmorLost, zone, source));
        if (!r.ReachedZero) return;

        if (!downed && UsesDownedState(victim, attacker))
        {
            victim.Status |= ActorFlags.Downed;
            victim.Vitals.Health = 1;
            victim.Vitals.DownedSecondsLeft = Config.Downed.Seconds;
            _world.Events.Publish(new ActorDowned(victim.Id, attacker?.Id ?? -1));
            return;
        }
        Kill(victim, attacker?.Id ?? -1, source, headshot);
    }

    private bool IsProtected(Actor victim, Actor? attacker)
    {
        if (attacker == null || attacker.Id == victim.Id) return false;
        if ((victim.Status & ActorFlags.Passive) != 0 || (attacker.Status & ActorFlags.Passive) != 0) return true;
        return victim.Team != 0 && victim.Team == attacker.Team; // no friendly fire inside a crew
    }

    public void Kill(Actor victim, int killerId, string source, bool headshot)
    {
        victim.Vitals.Health = 0;
        victim.Status = (victim.Status | ActorFlags.Dead) & ~ActorFlags.Downed;
        victim.Move.Velocity = Vec3d.Zero;
        if (victim.Kind == ActorKind.Player) _respawnTimers[victim.Id] = Config.RespawnSeconds;
        _world.Events.Publish(new ActorKilled(victim.Id, killerId, source, headshot));
        TrackRepeatKills(killerId, victim);
    }

    private void TrackRepeatKills(int killerId, Actor victim)
    {
        if (killerId < 0 || victim.Kind != ActorKind.Player || !_world.Actors.TryGet(killerId, out var k) || k.Kind != ActorKind.Player) return;
        var key = (killerId, victim.Id);
        if (!_killLog.TryGetValue(key, out var times)) _killLog[key] = times = new List<double>();
        times.Add(_time);
        times.RemoveAll(t => _time - t > Config.RepeatKillWindowSeconds);
        if (times.Count >= Config.RepeatKillLimit)
            _world.Events.Publish(new RepeatKillDetected(killerId, victim.Id, times.Count));
    }

    public void Respawn(Actor a)
    {
        _respawnTimers.Remove(a.Id);
        a.Status &= ~(ActorFlags.Dead | ActorFlags.Downed | ActorFlags.Carrying);
        a.Vitals = Vitals.Full(Config.Health.Max);
        a.Move = new MoveState { Position = SpawnPointFor(a) };
        if (_loadouts.TryGetValue(a.Id, out var l))
        {
            l.ReloadLeft = 0;
            l.Cooldown = 0;
        }
        _world.Events.Publish(new ActorRespawned(a.Id, a.Position));
    }

    private void TickRevive(Actor reviver, double dt)
    {
        var target = _world.Actors.All
            .Where(t => t.Id != reviver.Id && (t.Status & ActorFlags.Downed) != 0 && t.IsAlive
                        && (reviver.Team == 0 ? false : t.Team == reviver.Team)
                        && Vec3d.Distance(t.Position, reviver.Position) <= Config.Downed.ReviveRange)
            .OrderBy(t => Vec3d.Distance(t.Position, reviver.Position)).FirstOrDefault();
        if (target == null) { _revives.Remove(reviver.Id); return; }
        var progress = _revives.TryGetValue(reviver.Id, out var r) && r.Target == target.Id ? r.Progress + dt : dt;
        if (progress + 1e-9 >= Config.Downed.ReviveSeconds)
        {
            _revives.Remove(reviver.Id);
            target.Status &= ~ActorFlags.Downed;
            target.Vitals.Health = Config.Downed.ReviveHealth;
            target.Vitals.DownedSecondsLeft = 0;
            target.Vitals.SecondsSinceDamage = 0;
            _world.Events.Publish(new ActorRevived(target.Id, reviver.Id));
        }
        else _revives[reviver.Id] = (target.Id, progress);
    }

    // ------------------------------------------------------------------ explosives

    private void DamageExplosive(int boxId, int damage, int by)
    {
        if (!_explosives.TryGetValue(boxId, out var e) || e.Health <= 0) return;
        e.Health -= damage;
        if (e.Health <= 0) Detonate(boxId, by);
    }

    private void Detonate(int boxId, int by)
    {
        if (!_explosives.Remove(boxId) || !_static.TryGet(boxId, out var box)) return;
        _static.Remove(boxId);
        var c = (box.Box.Min + box.Box.Max) / 2;
        _world.Events.Publish(new ExplosiveDetonated(boxId, c, by));
        var x = Config.Explosive;
        _world.Actors.TryGet(by, out var attacker);
        foreach (var a in _world.Actors.All.ToList())
        {
            if (!a.IsAlive || a.Kind is not (ActorKind.Player or ActorKind.Npc)) continue;
            var torso = a.Position + new Vec3d(0, 1.0, 0);
            var d = Vec3d.Distance(torso, c);
            if (d > x.Radius) continue;
            // blocked by solid cover between blast and target
            if (_static.Raycast(c, torso - c, d, out var block) && Materials.PassScale(block.Box.Material, WeaponClass.Rifle, 0) <= 0) continue;
            var dmg = x.InnerDamage + (x.OuterDamage - x.InnerDamage) * (d / x.Radius);
            ApplyDamage(a, attacker, dmg, HitZone.Torso, false, "explosion");
        }
        foreach (var other in _explosives.Keys.ToList())
            if (_static.TryGet(other, out var ob) && Vec3d.Distance((ob.Box.Min + ob.Box.Max) / 2, c) <= x.Radius
                && _pendingExplosions.All(p => p.BoxId != other))
                _pendingExplosions.Add((x.ChainDelaySeconds, other, by));
    }

    /// <summary>Deterministic per-shot RNG (same shot → same spread, independent of call order).</summary>
    private sealed class SplitMix(ulong seed)
    {
        private ulong _s = seed;
        public double NextDouble()
        {
            var z = _s += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }
    }
}
