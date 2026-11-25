namespace Harbor.Core.Economy;

/// <summary>Dirty money comes from crime and can only be spent on the black market or laundered;
/// clean money buys weapons, property and businesses (CONTENT_DESIGN §5.1).</summary>
public enum Currency : byte { Dirty, Clean }

public enum AccountKind : byte
{
    Wallet,     // dirty cash carried by a player — dropped on death
    Bank,       // clean money
    Stash,      // safehouse safe: dirty, safe from death drops
    Crew,       // crew vault (both currencies)
    Business,   // laundering queue of a business
    Drop,       // cash bag lying in the world
    Faucet,     // system source (payouts) — may go negative
    Sink,       // system sink (fees, purchases)
    Escrow,     // money held by an activity (heist take, bounty) until it is paid out or forfeited
}

/// <summary>Ledger account address, e.g. <c>wallet:rook</c>, <c>crew:7</c>, <c>sys:faucet:contract</c>.</summary>
public readonly record struct AccountId(AccountKind Kind, string Key)
{
    public static AccountId Wallet(string profile) => new(AccountKind.Wallet, profile);
    public static AccountId Bank(string profile) => new(AccountKind.Bank, profile);
    public static AccountId Stash(string profile) => new(AccountKind.Stash, profile);
    public static AccountId Crew(int crewId) => new(AccountKind.Crew, crewId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static AccountId Business(string businessId) => new(AccountKind.Business, businessId);
    public static AccountId Drop(int dropId) => new(AccountKind.Drop, dropId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static AccountId Faucet(string reason) => new(AccountKind.Faucet, reason);
    public static AccountId Sink(string reason) => new(AccountKind.Sink, reason);
    public static AccountId Escrow(string key) => new(AccountKind.Escrow, key);

    public bool IsSystem => Kind is AccountKind.Faucet or AccountKind.Sink;
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Key}";
}

/// <summary>One movement of money. <see cref="TxId"/> makes it idempotent.</summary>
public sealed record Transfer(string TxId, AccountId From, AccountId To, long Amount, Currency Currency, string Reason);

public enum TxStatus { Applied, Duplicate, InsufficientFunds, Invalid }

public readonly record struct TxResult(TxStatus Status, string TxId)
{
    public bool Ok => Status is TxStatus.Applied or TxStatus.Duplicate;
}
