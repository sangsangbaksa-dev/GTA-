namespace Harbor.Core.Economy;

/// <summary>Double-entry ledger (ADR 0005). Money only moves between accounts, so the sum over all
/// accounts per currency is always zero; faucets go negative by what was created.
/// Every transfer is idempotent by TxId; batches are all-or-nothing.</summary>
public sealed class Ledger
{
    private readonly Dictionary<(AccountId, Currency), long> _balances = new();
    private readonly Dictionary<string, TxStatus> _applied = new();
    private readonly List<Transfer> _log = new();

    /// <summary>Called after every applied transfer (economy relays to clients / metrics).</summary>
    public event Action<Transfer>? Posted;

    public IReadOnlyList<Transfer> Log => _log;

    public long Balance(AccountId account, Currency currency) => _balances.GetValueOrDefault((account, currency));

    public TxResult Post(Transfer t) => PostBatch(t.TxId, new[] { t });

    /// <summary>Applies all transfers or none. The batch is identified by <paramref name="batchId"/>.</summary>
    public TxResult PostBatch(string batchId, IReadOnlyList<Transfer> transfers)
    {
        if (_applied.ContainsKey(batchId)) return new TxResult(TxStatus.Duplicate, batchId);
        if (transfers.Count == 0 || transfers.Any(t => t.Amount <= 0 || t.From == t.To))
            return new TxResult(TxStatus.Invalid, batchId);

        // check funds with the batch's own effects applied in order
        var pending = new Dictionary<(AccountId, Currency), long>();
        foreach (var t in transfers)
        {
            var key = (t.From, t.Currency);
            var bal = pending.TryGetValue(key, out var p) ? p : Balance(t.From, t.Currency);
            if (t.From.Kind != AccountKind.Faucet && bal < t.Amount) return new TxResult(TxStatus.InsufficientFunds, batchId);
            pending[key] = bal - t.Amount;
            var to = (t.To, t.Currency);
            pending[to] = (pending.TryGetValue(to, out var q) ? q : Balance(t.To, t.Currency)) + t.Amount;
        }
        foreach (var (k, v) in pending) _balances[k] = v;
        _applied[batchId] = TxStatus.Applied;
        foreach (var t in transfers)
        {
            _log.Add(t);
            Posted?.Invoke(t);
        }
        return new TxResult(TxStatus.Applied, batchId);
    }

    public bool WasApplied(string txId) => _applied.ContainsKey(txId);

    /// <summary>Sum of all balances in a currency — must be 0 (conservation).</summary>
    public long Total(Currency c) => _balances.Where(kv => kv.Key.Item2 == c).Sum(kv => kv.Value);

    /// <summary>Money created by faucets minus money destroyed in sinks = money held by players/world.</summary>
    public long Circulating(Currency c) => _balances.Where(kv => kv.Key.Item2 == c && !kv.Key.Item1.IsSystem).Sum(kv => kv.Value);

    public IEnumerable<(AccountId Account, Currency Currency, long Balance)> Balances
        => _balances.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value));

    /// <summary>Restore a persisted balance (load only; bypasses double entry by design of the save format).</summary>
    internal void Restore(AccountId a, Currency c, long v) => _balances[(a, c)] = v;
}
