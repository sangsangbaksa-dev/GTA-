using Harbor.Core.Events;
using Harbor.Core.Math;

namespace Harbor.Core.Economy;

public sealed record BalanceChanged(string ProfileId, AccountId Account, Currency Currency, long Balance, long Delta, string Reason) : ISimEvent;
public sealed record PurchaseCompleted(int ActorId, string ItemId, Currency Currency, long Price) : ISimEvent;
public sealed record CashDropped(int DropId, int LootActorId, long Amount, Vec3d Position) : ISimEvent;
public sealed record CashPickedUp(int DropId, int ActorId, long Amount) : ISimEvent;
public sealed record LaunderingProgress(string BusinessId, string ProfileId, long CleanCredited, long Remaining) : ISimEvent;
public sealed record PayoutGranted(string ProfileId, Currency Currency, long Amount, string Activity, double Multiplier) : ISimEvent;

public readonly record struct EconResult(bool Ok, string? Error = null)
{
    public static readonly EconResult Success = new(true);
    public static EconResult Fail(string error) => new(false, error);
}

/// <summary>Implemented by the server composition (combat inventory) so Economy can hand out goods
/// without depending on Combat.</summary>
public interface IItemReceiver
{
    /// <returns>null if the actor can take the item, otherwise a reason.</returns>
    string? CanReceive(int actorId, ShopItem item);
    void Receive(int actorId, ShopItem item);
}
