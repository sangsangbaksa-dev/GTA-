using Harbor.Core.Combat;
using Harbor.Core.Economy;
using Harbor.Core.Geometry;
using Harbor.Core.Math;
using Harbor.Core.Sim;

namespace Harbor.Core.Server;

public sealed record HarborServiceState(string Id = "", string Label = "", int Price = 0);

internal sealed class HarborStreetServices
{
    private sealed record Station(string Id, string Label, int Price, Vec3d Position);
    private readonly Station[] _stations;
    private readonly CombatSystem _combat;
    private readonly StaticWorld _level;
    private readonly Ledger _ledger;
    private const double Radius = 2.4;

    public HarborStreetServices(HarborLayout layout, CombatSystem combat, StaticWorld level, Ledger ledger)
    {
        _combat = combat;
        _level = level;
        _ledger = ledger;
        _stations = layout.Landmarks.Where(l => l.Id is "customs_exchange_cafe" or "atlantic_repair" or "alderwash_lodge")
            .Select(l =>
            {
                var p = HarborLockdownDef.Point(l.Position);
                return l.Id switch
                {
                    "customs_exchange_cafe" => new Station("health", "회복", 20, p),
                    "atlantic_repair" => new Station("ammo", "탄약 보충", 30, p),
                    _ => new Station("armor", "방어구 보충", 40, p)
                };
            }).ToArray();
    }

    private Station? Nearby(Actor actor)
    {
        if (!actor.IsAlive || (actor.Status & ActorFlags.Downed) != 0) return null;
        return _stations.Where(s => Vec3d.Distance(actor.Position, s.Position) <= Radius &&
            !_level.Raycast(actor.Position + Vec3d.Up, s.Position - actor.Position, Vec3d.Distance(actor.Position, s.Position), out _))
            .MinBy(s => Vec3d.Distance(actor.Position, s.Position));
    }

    public HarborServiceState Describe(Actor actor)
    {
        var station = Nearby(actor);
        return station == null ? new HarborServiceState() : new HarborServiceState(station.Id, station.Label, station.Price);
    }

    public string Use(Actor actor, AccountId wallet, string txId, int wanted, out int cost)
    {
        cost = 0;
        var station = Nearby(actor);
        if (station == null) return "service_unavailable";
        if (wanted > 0) return "service_wanted";
        var loadout = _combat.LoadoutOf(actor.Id);
        var weapon = loadout?.Current?.Def;
        var reserve = weapon == null || loadout == null ? 0 : loadout.ReserveOf(weapon.Class);
        var rounds = weapon == null ? 0 : System.Math.Max(0, System.Math.Min(weapon.MagazineSize * 2, weapon.MagazineSize * 8 - reserve));
        var needed = station.Id switch
        {
            "health" => actor.Vitals.Health < actor.Vitals.MaxHealth,
            "armor" => actor.Vitals.Armor < _combat.Config.Armor.Max,
            "ammo" => rounds > 0,
            _ => false
        };
        if (!needed) return "service_full";
        var result = _ledger.Post(new Transfer(txId, wallet, AccountId.Sink("street_service"), station.Price, Currency.Dirty, station.Id));
        if (result.Status != TxStatus.Applied) return "service_funds";
        cost = station.Price;
        switch (station.Id)
        {
            case "health": _combat.Heal(actor, 60); break;
            case "armor": _combat.AddArmor(actor, 50); break;
            case "ammo": _combat.AddReserveAmmo(actor.Id, weapon!.Class, rounds); break;
        }
        return "service_" + station.Id;
    }
}
