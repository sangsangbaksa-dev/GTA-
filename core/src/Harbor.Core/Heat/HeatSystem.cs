using Harbor.Core.Data;
using Harbor.Core.Events;
using Harbor.Core.Sim;

namespace Harbor.Core.Heat;

/// <summary>data/ai/heat.json.</summary>
public sealed record HeatConfig
{
    public double DecayPerSecond { get; init; } = 0.5;
    /// <summary>Heat only starts to decay after this long without a new crime.</summary>
    public double DecayDelaySeconds { get; init; } = 15;
    /// <summary>The same crime by the same actor adds heat at most once per this interval (e.g. a full SMG mag is one "shots_fired").</summary>
    public double RepeatCrimeSeconds { get; init; } = 5;
    public Dictionary<string, double> CrimeHeat { get; init; } = new();
    public List<HeatLevel> Levels { get; init; } = new() { new HeatLevel(0, 0, 0, 0) };

    public void Validate()
    {
        Json.Require(Levels.Count > 0 && Levels[0].MinHeat == 0, "heat.levels must start at minHeat 0");
        for (var i = 1; i < Levels.Count; i++)
            Json.Require(Levels[i].MinHeat > Levels[i - 1].MinHeat && Levels[i].Level == Levels[i - 1].Level + 1,
                "heat.levels must be consecutive with increasing minHeat");
    }

    public int LevelFor(double heat)
    {
        var level = 0;
        foreach (var l in Levels) if (heat >= l.MinHeat) level = l.Level;
        return level;
    }

    public int MaxLevel => Levels[^1].Level;
}

public sealed record HeatLevel(int Level, double MinHeat, int Units, double ResponseSeconds);

public sealed record CrimeCommitted(int ActorId, string Crime, double HeatAdded) : ISimEvent;
public sealed record WantedLevelChanged(int ActorId, int OldLevel, int NewLevel) : ISimEvent;

/// <summary>Per-actor wanted level (0–5). Crimes are reported by the server composition (combat events) and by
/// mission objectives; the police response itself (Agents) reads <see cref="LevelOf"/>.</summary>
public sealed class HeatSystem(HeatConfig config) : ISimSystem
{
    public HeatConfig Config { get; } = config;
    private readonly Dictionary<int, State> _states = new();
    private SimWorld _world = null!;
    private double _now;

    private sealed class State
    {
        public double Heat;
        public double LastCrimeAt = double.NegativeInfinity;
        public int Level;
        public double LevelSince;
        public readonly Dictionary<string, double> LastByCrime = new();
    }

    public void Initialize(SimWorld world) => _world = world;

    public int LevelOf(int actorId) => _states.TryGetValue(actorId, out var s) ? s.Level : 0;
    public double HeatOf(int actorId) => _states.TryGetValue(actorId, out var s) ? s.Heat : 0;
    /// <summary>Seconds the actor has been at its current level (0 if not wanted).</summary>
    public double SecondsAtLevel(int actorId) => _states.TryGetValue(actorId, out var s) && s.Level > 0 ? _now - s.LevelSince : 0;

    /// <returns>Heat actually added (0 for unknown crimes or throttled repeats).</returns>
    public double Report(int actorId, string crime)
    {
        if (!Config.CrimeHeat.TryGetValue(crime, out var amount) || amount <= 0) return 0;
        var s = StateOf(actorId);
        if (s.LastByCrime.TryGetValue(crime, out var last) && _now - last < Config.RepeatCrimeSeconds) { s.LastCrimeAt = _now; return 0; }
        s.LastByCrime[crime] = _now;
        s.LastCrimeAt = _now;
        s.Heat += amount;
        _world.Events.Publish(new CrimeCommitted(actorId, crime, amount));
        Recompute(actorId, s);
        return amount;
    }

    /// <summary>Raise the wanted level by whole levels (harassment penalty).</summary>
    public void AddLevels(int actorId, int levels)
    {
        var s = StateOf(actorId);
        var target = System.Math.Min(Config.MaxLevel, s.Level + levels);
        var min = Config.Levels.First(l => l.Level == target).MinHeat;
        s.Heat = System.Math.Max(s.Heat, min);
        s.LastCrimeAt = _now;
        Recompute(actorId, s);
    }

    /// <summary>Death, arrest or leaving clears the wanted level.</summary>
    public void Clear(int actorId)
    {
        if (!_states.TryGetValue(actorId, out var s)) return;
        s.Heat = 0;
        Recompute(actorId, s);
        _states.Remove(actorId);
    }

    public void Step(double dt)
    {
        _now += dt;
        foreach (var (id, s) in _states.ToList())
        {
            if (s.Heat <= 0) { if (s.Level == 0) _states.Remove(id); continue; }
            if (_now - s.LastCrimeAt < Config.DecayDelaySeconds) continue;
            s.Heat = System.Math.Max(0, s.Heat - Config.DecayPerSecond * dt);
            Recompute(id, s);
        }
    }

    private State StateOf(int actorId)
    {
        if (!_states.TryGetValue(actorId, out var s)) _states[actorId] = s = new State();
        return s;
    }

    private void Recompute(int actorId, State s)
    {
        var level = Config.LevelFor(s.Heat);
        if (level == s.Level) return;
        var old = s.Level;
        s.Level = level;
        s.LevelSince = _now;
        _world.Events.Publish(new WantedLevelChanged(actorId, old, level));
    }
}
