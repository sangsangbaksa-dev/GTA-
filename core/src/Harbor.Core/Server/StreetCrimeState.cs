using Harbor.Core.Net;

namespace Harbor.Core.Server;

public sealed record CrimeStatus(int Cash = 0, string LastAction = "");

/// <summary>Authoritative per-player street interaction cooldown and cash, separate from mission rewards.</summary>
internal sealed class StreetCrimeState
{
    private readonly Dictionary<int, CrimeStatus> _status = new();
    private readonly Dictionary<int, long> _last = new();
    private readonly Dictionary<int, InputButtons> _held = new();

    public CrimeStatus Get(int id) => _status.GetValueOrDefault(id) ?? new();
    public bool TryAct(int id, InputButtons action, long tick, double step)
    {
        var previous = _held.GetValueOrDefault(id);
        _held[id] = action;
        if (action == InputButtons.None || previous != InputButtons.None) return false;
        if (_last.TryGetValue(id, out var last) && (tick - last) * step < 1.0) return false;
        _last[id] = tick;
        return true;
    }
    public void Record(int id, string text, int cash) => _status[id] = new(Get(id).Cash + cash, text);
    public void Remove(int id) { _status.Remove(id); _last.Remove(id); _held.Remove(id); }
    public void Clear() { _status.Clear(); _last.Clear(); _held.Clear(); }
}
