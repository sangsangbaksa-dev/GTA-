using Harbor.Core.Events;
using Harbor.Core.Math;
using Harbor.Core.Sim;

namespace Harbor.Core.Missions;

/// <summary>A physical item that must be carried by hand (heist cash bag, bond case). While carried the carrier
/// moves slower, can't sprint and may only fire pistols (<see cref="ActorFlags.Carrying"/>). Anyone can pick up a
/// dropped item — including rivals.</summary>
public sealed class CarryItem
{
    public int Id { get; init; }
    /// <summary>World actor that represents the item in snapshots.</summary>
    public int ActorId { get; init; }
    public string Kind { get; init; } = "";
    public long Value { get; init; }
    /// <summary>Owner key, e.g. the mission run that spawned it.</summary>
    public string Tag { get; init; } = "";
    public int? CarrierId { get; internal set; }
    public Vec3d Position { get; internal set; }
}

public sealed record ItemPickedUp(int ItemId, int ActorId, string Kind) : ISimEvent;
public sealed record ItemDropped(int ItemId, int ActorId, Vec3d Position) : ISimEvent;

public sealed class CarrySystem(SimWorld world, InteractInput input)
{
    public double PickupRadius { get; init; } = 1.6;
    private readonly Dictionary<int, CarryItem> _items = new();
    private int _next = 1;

    public IEnumerable<CarryItem> Items => _items.Values;
    public CarryItem? Get(int id) => _items.GetValueOrDefault(id);
    public CarryItem? CarriedBy(int actorId) => _items.Values.FirstOrDefault(i => i.CarrierId == actorId);
    public IEnumerable<CarryItem> WithTag(string tag) => _items.Values.Where(i => i.Tag == tag);

    public CarryItem Spawn(string kind, Vec3d position, long value, string tag)
    {
        var actor = world.Actors.Spawn(ActorKind.Pickup, position);
        actor.Name = kind;
        var item = new CarryItem { Id = _next++, ActorId = actor.Id, Kind = kind, Value = value, Tag = tag, Position = position };
        _items[item.Id] = item;
        return item;
    }

    public void Remove(int itemId)
    {
        if (!_items.Remove(itemId, out var item)) return;
        if (item.CarrierId is { } c && world.Actors.TryGet(c, out var carrier)) carrier.Status &= ~ActorFlags.Carrying;
        world.Actors.Remove(item.ActorId);
    }

    public bool Drop(int actorId)
    {
        var item = CarriedBy(actorId);
        if (item == null) return false;
        var pos = world.Actors.TryGet(actorId, out var a) ? a.Position : item.Position;
        if (a != null) a.Status &= ~ActorFlags.Carrying;
        item.CarrierId = null;
        Place(item, pos);
        world.Events.Publish(new ItemDropped(item.Id, actorId, pos));
        return true;
    }

    public void Step()
    {
        foreach (var item in _items.Values.ToList())
        {
            if (item.CarrierId is { } c)
            {
                if (!world.Actors.TryGet(c, out var carrier) || !carrier.IsActive()) { Drop(c); continue; }
                Place(item, carrier.Position);
                continue;
            }
            // first press wins; ties broken by actor id so the result is deterministic
            var taker = world.Actors.All
                .Where(a => a.Kind == ActorKind.Player && a.IsActive() && (a.Status & ActorFlags.Carrying) == 0
                            && input.WasPressed(a.Id) && ActorQueries.FlatDistance(a.Position, item.Position) <= PickupRadius)
                .OrderBy(a => a.Id).FirstOrDefault();
            if (taker == null) continue;
            item.CarrierId = taker.Id;
            taker.Status |= ActorFlags.Carrying;
            world.Events.Publish(new ItemPickedUp(item.Id, taker.Id, item.Kind));
        }
    }

    private void Place(CarryItem item, Vec3d pos)
    {
        item.Position = pos;
        if (world.Actors.TryGet(item.ActorId, out var a)) a.Position = pos;
    }
}
