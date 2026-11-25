using System.Collections.Generic;
using System.Linq;
using Godot;
using Harbor.Core.Client;
using Harbor.Core.Math;
using Harbor.Core.Net;
using Harbor.Core.Sim;
using Harbor.Game.Bridge;

namespace Harbor.Game.Views;

/// <summary>Spawns/updates/removes a node per replicated actor and plays one-shot effects (tracers, muzzle flash,
/// impacts, explosions). Local player = predicted state; everyone else = interpolated snapshots.</summary>
public partial class ActorViews(GameSession session) : Node3D
{
    private readonly Dictionary<int, CharacterView> _chars = new();
    private readonly Dictionary<int, Node3D> _items = new();
    private readonly List<(Node3D Node, double Left, double Total)> _fx = new();
    private readonly Dictionary<string, PackedScene> _props = new();
    private StandardMaterial3D _tracerMat = null!, _flashMat = null!, _bloodMat = null!, _fireMat = null!;

    public int ViewCount => _chars.Count + _items.Count;

    public override void _Ready()
    {
        _tracerMat = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.85f, 0.45f), EmissionEnabled = true, Emission = new Color(1f, 0.75f, 0.3f), EmissionEnergyMultiplier = 3, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _flashMat = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.8f, 0.4f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
        _bloodMat = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.04f, 0.04f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _fireMat = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.55f, 0.15f, 0.85f), EmissionEnabled = true, Emission = new Color(1f, 0.45f, 0.1f), EmissionEnergyMultiplier = 4, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        session.FxRaised += OnFx;
    }

    public override void _ExitTree() => session.FxRaised -= OnFx;

    public bool TryGetView(int actorId, out CharacterView view) => _chars.TryGetValue(actorId, out view!);

    public override void _Process(double delta)
    {
        var client = session.Client;
        if (client == null || !client.IsJoined) return;
        var dt = (float)delta;
        var seen = new HashSet<int>();

        // local player (predicted)
        var me = client.LocalActorId;
        {
            var a = session.PrevPredicted;
            var b = session.Predicted;
            var pos = Vec3d.Lerp(a.Position, b.Position, session.Alpha);
            var flags = b.Flags | (client.LocalServerState.Flags & (ActorFlags.Dead | ActorFlags.Downed | ActorFlags.Carrying));
            var v = Char(me, ActorKind.Player);
            SetLocalWeapon(v);
            v.Animate(V(pos), V(b.Velocity), (float)b.Yaw, (float)b.Pitch, flags, dt);
            seen.Add(me);
        }

        var renderTick = client.RenderTick - 1 + session.Alpha;
        foreach (var id in client.Remote.Ids.ToList())
        {
            if (!client.Remote.TrySample(id, renderTick, out var s)) continue;
            seen.Add(id);
            switch (s.Kind)
            {
                case ActorKind.Player:
                case ActorKind.Npc:
                    var v = Char(id, s.Kind);
                    v.Animate(V(s.Position), V(s.Velocity), (float)s.Yaw, (float)s.Pitch, s.Flags, dt);
                    break;
                default:
                    var item = Item(id, s);
                    item.Position = V(s.Position) + new Vector3(0, 0.05f + 0.06f * Mathf.Sin((float)Time.GetTicksMsec() / 300f), 0);
                    item.RotateY(dt * 1.2f);
                    break;
            }
        }

        foreach (var id in _chars.Keys.Where(k => !seen.Contains(k)).ToList()) { _chars[id].QueueFree(); _chars.Remove(id); }
        foreach (var id in _items.Keys.Where(k => !seen.Contains(k)).ToList()) { _items[id].QueueFree(); _items.Remove(id); }

        for (var i = _fx.Count - 1; i >= 0; i--)
        {
            var (node, left, total) = _fx[i];
            left -= delta;
            if (left <= 0) { node.QueueFree(); _fx.RemoveAt(i); continue; }
            var k = (float)(left / total);
            if (node is MeshInstance3D mi && mi.MaterialOverride is StandardMaterial3D m && m.Transparency != BaseMaterial3D.TransparencyEnum.Disabled)
                mi.Transparency = 1 - k;
            if (node is OmniLight3D l) l.LightEnergy = 6 * k;
            _fx[i] = (node, left, total);
        }
    }

    private void SetLocalWeapon(CharacterView v)
    {
        var id = session.State.WeaponId.Length > 0 ? session.State.WeaponId : "kestrel_p10";
        if (session.Data.Weapons.TryGetValue(id, out var w)) v.SetWeapon(id, w.ModelAsset);
    }

    private CharacterView Char(int id, ActorKind kind)
    {
        if (_chars.TryGetValue(id, out var v))
        {
            if (session.State.Actors.TryGetValue(id, out var inf) && inf.Model != v.ModelId && inf.Model.StartsWith("chr_")) v.SetModel(inf.Model);
            return v;
        }
        v = new CharacterView { ActorId = id, Name = $"Actor{id}" };
        AddChild(v);
        var info = session.State.Actors.GetValueOrDefault(id);
        var model = info.Model is { Length: > 0 } m && m.StartsWith("chr_") ? m : kind == ActorKind.Player ? "chr_player_rook" : "chr_enemy_gunman";
        v.SetModel(model);
        if (kind == ActorKind.Player)
        {
            if (id != session.LocalActorId) v.SetTag(info.Name ?? "", new Color(0.85f, 0.95f, 1f));
            if (id != session.LocalActorId) v.SetWeapon("kestrel_p10", session.Data.Weapons["kestrel_p10"].ModelAsset);
        }
        else
        {
            var faction = session.Data.Agents.Factions.Values.FirstOrDefault(f => f.Team == info.Team);
            var color = faction?.Id switch
            {
                "police" => new Color(0.45f, 0.65f, 1f),
                "ironclad" => new Color(0.75f, 0.75f, 0.8f),
                "velvet" => new Color(0.8f, 0.45f, 1f),
                _ => new Color(1f, 0.5f, 0.35f),
            };
            v.SetTag(faction?.DisplayName ?? "", color);
            var weapon = model switch { "chr_enemy_enforcer" => "halberd_ar", "chr_enemy_brawler" => "", _ => "wasp_smg" };
            if (faction?.Id == "police" && model == "chr_enemy_gunman") weapon = "kestrel_p10";
            if (weapon.Length > 0) v.SetWeapon(weapon, session.Data.Weapons[weapon].ModelAsset);
        }
        _chars[id] = v;
        return v;
    }

    private Node3D Item(int id, EntityState s)
    {
        if (_items.TryGetValue(id, out var n)) return n;
        var asset = session.State.Actors.TryGetValue(id, out var inf) && inf.Model.StartsWith("prop_") ? inf.Model
            : s.Kind == ActorKind.Loot ? "prop_cash_bag" : "prop_weapon_crate";
        n = Prop(asset) ?? new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.3f, 0.3f) } };
        var light = new OmniLight3D { LightColor = s.Kind == ActorKind.Loot ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.85f, 0.4f), LightEnergy = 1.2f, OmniRange = 3, Position = Vector3.Up * 0.6f };
        n.AddChild(light);
        AddChild(n);
        _items[id] = n;
        return n;
    }

    private Node3D? Prop(string asset)
    {
        if (!_props.TryGetValue(asset, out var scene))
        {
            var path = $"res://assets/props/{asset}.glb";
            if (!ResourceLoader.Exists(path)) return null;
            _props[asset] = scene = GD.Load<PackedScene>(path);
        }
        return scene.Instantiate<Node3D>();
    }

    // ------------------------------------------------------------------ effects

    private void OnFx(ClientFx fx)
    {
        switch (fx)
        {
            case ShotFx s:
            {
                var from = V(s.From);
                if (_chars.TryGetValue(s.ShooterId, out var shooter))
                {
                    if (s.WeaponId.Length > 0 && s.ShooterId != session.LocalActorId && shooter.WeaponId != s.WeaponId
                        && session.Data.Weapons.TryGetValue(s.WeaponId, out var w)) shooter.SetWeapon(s.WeaponId, w.ModelAsset);
                    from = shooter.MuzzleWorld;
                }
                Tracer(from, V(s.To));
                Flash(from);
                Spark(V(s.To));
                break;
            }
            case DamageFx d when d.Source != "explosion":
                if (_chars.TryGetValue(d.VictimId, out var victim)) Blood(victim.GlobalPosition + Vector3.Up * (d.Zone == Harbor.Core.Combat.HitZone.Head ? 1.65f : 1.15f));
                break;
            case ExplosionFx e:
                Explosion(V(e.Position));
                break;
        }
    }

    private void Add(Node3D n, double life)
    {
        AddChild(n);
        _fx.Add((n, life, life));
    }

    private void Tracer(Vector3 from, Vector3 to)
    {
        var len = from.DistanceTo(to);
        if (len < 0.5f) return;
        var mi = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.02f, 0.02f, len) },
            MaterialOverride = _tracerMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        Add(mi, 0.06);
        mi.GlobalTransform = new Transform3D(Basis.LookingAt((to - from).Normalized(), Vector3.Up), (from + to) / 2);
    }

    private void Flash(Vector3 at)
    {
        var l = new OmniLight3D { LightColor = new Color(1f, 0.75f, 0.35f), LightEnergy = 6, OmniRange = 5 };
        Add(l, 0.05);
        l.GlobalPosition = at;
        var m = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f }, MaterialOverride = _flashMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        Add(m, 0.04);
        m.GlobalPosition = at;
    }

    private void Spark(Vector3 at)
    {
        var m = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f }, MaterialOverride = _tracerMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        Add(m, 0.12);
        m.GlobalPosition = at;
    }

    private void Blood(Vector3 at)
    {
        for (var i = 0; i < 3; i++)
        {
            var m = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.06f, Height = 0.12f }, MaterialOverride = _bloodMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            Add(m, 0.25);
            m.GlobalPosition = at + new Vector3(GD.Randf() - 0.5f, GD.Randf() - 0.5f, GD.Randf() - 0.5f) * 0.25f;
        }
    }

    private void Explosion(Vector3 at)
    {
        var ball = new MeshInstance3D { Mesh = new SphereMesh { Radius = 2.2f, Height = 4.4f }, MaterialOverride = _fireMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        Add(ball, 0.6);
        ball.GlobalPosition = at + Vector3.Up;
        var l = new OmniLight3D { LightColor = new Color(1f, 0.6f, 0.25f), LightEnergy = 6, OmniRange = 18 };
        Add(l, 0.7);
        l.GlobalPosition = at + Vector3.Up * 2;
    }

    public static Vector3 V(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
