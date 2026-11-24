namespace Harbor.Core.Economy;

/// <summary>Diminishing returns for repeating the same activity (inflation control, §5.4):
/// from the Nth repeat inside the window each payout drops by a step, down to a floor.</summary>
public sealed class PayoutGovernor(EconomyConfig.DiminishingDef cfg)
{
    private readonly Dictionary<(string Profile, string Activity), List<double>> _history = new();

    public double Multiplier(string profile, string activity, double now)
    {
        if (!_history.TryGetValue((profile, activity), out var list)) return 1.0;
        list.RemoveAll(t => now - t > cfg.WindowSeconds);
        var repeat = list.Count + 1; // this payout's index
        if (repeat < cfg.StartAtRepeat) return 1.0;
        return System.Math.Max(cfg.Floor, 1.0 - cfg.StepPenalty * (repeat - cfg.StartAtRepeat + 1));
    }

    public void Record(string profile, string activity, double now)
    {
        if (!_history.TryGetValue((profile, activity), out var list)) _history[(profile, activity)] = list = new List<double>();
        list.Add(now);
    }
}
