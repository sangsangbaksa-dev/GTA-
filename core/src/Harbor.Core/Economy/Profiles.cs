namespace Harbor.Core.Economy;

/// <summary>Persistent player record (server-side). Balances live in the ledger, keyed by <see cref="Id"/>.</summary>
public sealed class PlayerProfile(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; set; } = name;
    public int Rep { get; set; }
    public long RepXp { get; set; }
    public int CrewId { get; set; }
    public HashSet<string> OwnedWeapons { get; } = new();
    public HashSet<string> OwnedSafehouses { get; } = new() { "sh_mill_warehouse" };

    /// <summary>XP needed to go from level n to n+1 (gentle quadratic curve, 50 levels).</summary>
    public static long XpForLevel(int level) => 1000 + 350L * level * level;

    /// <returns>Levels gained.</returns>
    public int AddXp(long xp)
    {
        RepXp += xp;
        var gained = 0;
        while (Rep < 50 && RepXp >= XpForLevel(Rep))
        {
            RepXp -= XpForLevel(Rep);
            Rep++;
            gained++;
        }
        return gained;
    }
}

/// <summary>Crew = 2–4 players sharing a vault, reputation and turf. Crew id doubles as combat team id.</summary>
public sealed class Crew(int id, string name, string leaderProfile)
{
    public const int MaxMembers = 4;
    public int Id { get; } = id;
    public string Name { get; } = name;
    public string Leader { get; set; } = leaderProfile;
    public List<string> Members { get; } = new() { leaderProfile };
    public int Rep { get; set; }
}

public sealed class CrewRegistry
{
    private readonly Dictionary<int, Crew> _crews = new();
    private int _next = 1;

    public Crew? Get(int id) => _crews.GetValueOrDefault(id);
    public IEnumerable<Crew> All => _crews.Values;

    public Crew Create(PlayerProfile leader, string name)
    {
        Leave(leader);
        var c = new Crew(_next++, name, leader.Id);
        _crews[c.Id] = c;
        leader.CrewId = c.Id;
        return c;
    }

    public bool Join(PlayerProfile p, int crewId)
    {
        if (!_crews.TryGetValue(crewId, out var c) || c.Members.Count >= Crew.MaxMembers) return false;
        Leave(p);
        c.Members.Add(p.Id);
        p.CrewId = c.Id;
        return true;
    }

    public void Leave(PlayerProfile p)
    {
        if (p.CrewId == 0 || !_crews.TryGetValue(p.CrewId, out var c)) { p.CrewId = 0; return; }
        c.Members.Remove(p.Id);
        if (c.Members.Count == 0) _crews.Remove(c.Id);
        else if (c.Leader == p.Id) c.Leader = c.Members[0];
        p.CrewId = 0;
    }
}
