using Harbor.Core.Combat;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Server;
using Harbor.Core.Sim;

namespace Harbor.Core.Client;

/// <summary>One-shot things the presentation layer should show (tracers, hit markers, kill feed...).</summary>
public abstract record ClientFx;
public sealed record ShotFx(int ShooterId, string WeaponId, Vec3d From, Vec3d To) : ClientFx;
public sealed record DamageFx(int VictimId, int AttackerId, int Amount, HitZone Zone, string Source) : ClientFx;
public sealed record KillFx(int VictimId, int KillerId, string Source, bool Headshot) : ClientFx;
public sealed record ExplosionFx(int BoxId, Vec3d Position) : ClientFx;
public sealed record NoticeFx(string Kind, string Id, Vec3d Position, long Value) : ClientFx;
public sealed record RejectedFx(ushort RequestType, string Reason) : ClientFx;
public sealed record ResultFx(string ContractId, bool Success, string Reason, long Paid) : ClientFx;
public sealed record MoneyFx(long WalletDelta, long BankDelta) : ClientFx;
public sealed record TeleportFx(Vec3d Position) : ClientFx;

public sealed record Objective(int RunId, int Index, string Label, Vec3d Target, double Progress);

/// <summary>Client-side model of what the server told us through reliable game events (ADR 0008).
/// Engine-agnostic: Godot's HUD reads it, and so could any other front-end. Fed by <see cref="ClientSession.Events"/>.</summary>
public sealed class ClientGameState
{
    public long Wallet { get; private set; }
    public long Bank { get; private set; }
    public long Stash { get; private set; }
    public bool HasBalance { get; private set; }
    public string WeaponId { get; private set; } = "";
    public int InMagazine { get; private set; }
    public int Reserve { get; private set; }
    public int WantedLevel { get; private set; }
    public Objective? Objective { get; private set; }
    public (int RunId, string HeistId, long Take, int SecondsLeft, List<string> Members)? PendingSplit { get; private set; }
    public IReadOnlyDictionary<int, ActorInfoMsg> Actors => _actors;
    public Queue<ClientFx> Fx { get; } = new();
    /// <summary>Weapons the local player has bought (shop purchases), for the weapon wheel.</summary>
    public HashSet<string> OwnedWeapons { get; } = new() { "kestrel_p10" };

    private readonly Dictionary<int, ActorInfoMsg> _actors = new();

    public string NameOf(int actorId) => _actors.TryGetValue(actorId, out var a) && a.Name.Length > 0 ? a.Name : $"#{actorId}";

    public void Apply(ushort type, byte[] p)
    {
        try { ApplyUnsafe(type, p); }
        catch (NetFormatException) { /* ignore malformed event */ }
    }

    private void ApplyUnsafe(ushort type, byte[] p)
    {
        switch (type)
        {
            case WireEventType.Balance:
            {
                var (w, b, s) = RequestCodec.ReadBalance(p);
                if (HasBalance && (w != Wallet || b != Bank)) Fx.Enqueue(new MoneyFx(w - Wallet, b - Bank));
                Wallet = w; Bank = b; Stash = s; HasBalance = true;
                break;
            }
            case WireEventType.Ammo:
                (WeaponId, InMagazine, Reserve) = WireEvents.ReadAmmo(p);
                break;
            case WireEventType.Wanted:
                WantedLevel = p.Length > 0 ? p[0] : 0;
                break;
            case WireEventType.ActorInfo:
            {
                var info = WireEvents.ReadActorInfo(p);
                _actors[info.Id] = info;
                break;
            }
            case WireEventType.ActorRemoved:
                _actors.Remove(WireEvents.ReadActorPair(p).A);
                break;
            case WireEventType.Shot:
            {
                var s = WireEvents.ReadShot(p);
                Fx.Enqueue(new ShotFx(s.ShooterId, s.WeaponId, s.Origin, s.End));
                break;
            }
            case WireEventType.Damaged:
            {
                var d = WireEvents.ReadDamaged(p);
                Fx.Enqueue(new DamageFx(d.VictimId, d.AttackerId, d.HealthLost + d.ArmorLost, d.Zone, d.Source));
                break;
            }
            case WireEventType.Killed:
            {
                var k = WireEvents.ReadKilled(p);
                Fx.Enqueue(new KillFx(k.VictimId, k.KillerId, k.Source, k.Headshot));
                break;
            }
            case WireEventType.Explosion:
            {
                var (id, pos) = WireEvents.ReadPoint(p);
                Fx.Enqueue(new ExplosionFx(id, pos));
                break;
            }
            case WireEventType.ActivityUpdate:
            {
                var o = WireEvents.ReadObjective(p);
                Objective = new Objective(o.RunId, o.Index, o.Label, o.Target, o.Progress);
                break;
            }
            case WireEventType.ActivityResult:
            {
                var r = WireEvents.ReadResult(p);
                if (Objective?.RunId == r.RunId) Objective = null;
                Fx.Enqueue(new ResultFx(r.ContractId, r.Success, r.Reason, r.Paid));
                break;
            }
            case WireEventType.HeistSplit:
                PendingSplit = WireEvents.ReadSplit(p);
                break;
            case WireEventType.Notice:
            {
                var n = WireEvents.ReadNotice(p);
                if (n.Kind == "heist.paid") PendingSplit = null;
                Fx.Enqueue(new NoticeFx(n.Kind, n.Id, n.Position, n.Value));
                break;
            }
            case WireEventType.RequestRejected:
            {
                var r = RequestCodec.ReadRejected(p);
                Fx.Enqueue(new RejectedFx(r.Type, r.Reason));
                break;
            }
            case WireEventType.Purchase:
            {
                var item = WireEvents.ReadText(p);
                if (item.StartsWith("wpn_", StringComparison.Ordinal)) OwnedWeapons.Add(item[4..]);
                break;
            }
            case WireEventType.Travelled:
                Fx.Enqueue(new TeleportFx(WireEvents.ReadPoint(p).Pos));
                break;
        }
    }
}
