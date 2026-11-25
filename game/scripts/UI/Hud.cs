using System.Collections.Generic;
using System.Linq;
using Godot;
using Harbor.Core.Client;
using Harbor.Core.Sim;
using Harbor.Game.Bridge;
using Harbor.Game.Views;

namespace Harbor.Game.UI;

/// <summary>In-game HUD built in code: vitals, money, wanted level, ammo, objective + waypoint, crosshair with
/// hit marker, feed of notices/kills, damage vignette, death/downed banner.</summary>
public partial class Hud(GameSession session) : CanvasLayer
{
    private ProgressBar _health = null!, _armor = null!, _objProgress = null!;
    private Label _wallet = null!, _bank = null!, _wanted = null!, _ammo = null!, _weapon = null!, _objective = null!, _banner = null!, _area = null!, _help = null!;
    private VBoxContainer _feed = null!;
    private Control _crosshair = null!, _hitmarker = null!, _waypoint = null!;
    private ColorRect _vignette = null!;
    private Label _waypointLabel = null!;
    private readonly List<(Label Label, double Until)> _feedItems = new();
    private double _hitUntil, _hurt;
    private string _lastArea = "";

    public override void _Ready()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        _vignette = new ColorRect { Color = new Color(0.6f, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _vignette.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_vignette);

        // vitals (bottom-left)
        var vit = Box(root, Control.LayoutPreset.BottomLeft, new Vector2(24, -96), new Vector2(300, 70));
        _health = Bar(vit, new Color(0.85f, 0.2f, 0.2f));
        _armor = Bar(vit, new Color(0.3f, 0.6f, 1f));
        _area = Text(vit, 18, new Color(1, 1, 1, 0.8f));

        // money + wanted (top-right)
        var money = Box(root, Control.LayoutPreset.TopRight, new Vector2(-330, 20), new Vector2(310, 110));
        _wallet = Text(money, 26, new Color(0.55f, 1f, 0.55f), HorizontalAlignment.Right);
        _bank = Text(money, 20, Colors.White, HorizontalAlignment.Right);
        _wanted = Text(money, 30, new Color(1f, 0.85f, 0.3f), HorizontalAlignment.Right);

        // weapon (bottom-right)
        var wpn = Box(root, Control.LayoutPreset.BottomRight, new Vector2(-330, -96), new Vector2(310, 76));
        _weapon = Text(wpn, 18, new Color(1, 1, 1, 0.85f), HorizontalAlignment.Right);
        _ammo = Text(wpn, 34, Colors.White, HorizontalAlignment.Right);

        // objective (top-centre)
        var obj = Box(root, Control.LayoutPreset.CenterTop, new Vector2(-260, 18), new Vector2(520, 70));
        _objective = Text(obj, 22, Colors.White, HorizontalAlignment.Center);
        _objProgress = Bar(obj, new Color(1f, 0.8f, 0.3f));
        _objProgress.CustomMinimumSize = new Vector2(0, 8);

        // feed (left)
        _feed = new VBoxContainer { Position = new Vector2(24, 140), CustomMinimumSize = new Vector2(460, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.AddChild(_feed);

        _banner = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visible = false };
        _banner.SetAnchorsPreset(Control.LayoutPreset.Center);
        _banner.AddThemeFontSizeOverride("font_size", 46);
        _banner.AddThemeColorOverride("font_outline_color", Colors.Black);
        _banner.AddThemeConstantOverride("outline_size", 12);
        _banner.Position = new Vector2(-400, -140);
        _banner.Size = new Vector2(800, 120);
        root.AddChild(_banner);

        _crosshair = new Crosshair { MouseFilter = Control.MouseFilterEnum.Ignore };
        _crosshair.SetAnchorsPreset(Control.LayoutPreset.Center);
        root.AddChild(_crosshair);
        _hitmarker = new Crosshair { Hit = true, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _hitmarker.SetAnchorsPreset(Control.LayoutPreset.Center);
        root.AddChild(_hitmarker);

        _waypoint = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _waypointLabel = new Label { Text = "◆", Position = new Vector2(-10, -18) };
        _waypointLabel.AddThemeFontSizeOverride("font_size", 22);
        _waypointLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f));
        _waypointLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _waypointLabel.AddThemeConstantOverride("outline_size", 6);
        _waypoint.AddChild(_waypointLabel);
        root.AddChild(_waypoint);

        _help = new Label { Text = "WASD 이동 · Shift 달리기 · C 앉기 · 우클릭 조준 · 좌클릭 사격 · R 장전 · 1-4 무기 · E 상호작용 · Q 어깨 전환 · Tab 폰", Position = new Vector2(24, 8) };
        _help.AddThemeFontSizeOverride("font_size", 14);
        _help.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.55f));
        root.AddChild(_help);

        session.FxRaised += OnFx;
        Visible = false;
        session.Started += () => Visible = session.Client != null;
    }

    public override void _ExitTree() => session.FxRaised -= OnFx;

    private static VBoxContainer Box(Control parent, Control.LayoutPreset anchor, Vector2 offset, Vector2 size)
    {
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.SetAnchorsPreset(anchor);
        v.Position += offset;
        v.Size = size;
        v.CustomMinimumSize = size;
        parent.AddChild(v);
        return v;
    }

    private static ProgressBar Bar(Control parent, Color c)
    {
        var bar = new ProgressBar { MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 14), MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = c });
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.45f) });
        parent.AddChild(bar);
        return bar;
    }

    private static Label Text(Control parent, int size, Color c, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var l = new Label { HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", c);
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 6);
        parent.AddChild(l);
        return l;
    }

    public void Feed(string text, Color color, double seconds = 6)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 18);
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 5);
        _feed.AddChild(l);
        _feedItems.Add((l, Time.GetTicksMsec() / 1000.0 + seconds));
        while (_feedItems.Count > 7) { _feedItems[0].Label.QueueFree(); _feedItems.RemoveAt(0); }
    }

    private static string Money(long v) => "$" + v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private void OnFx(ClientFx fx)
    {
        var me = session.LocalActorId;
        var st = session.State;
        switch (fx)
        {
            case DamageFx d when d.AttackerId == me && d.VictimId != me:
                _hitUntil = Time.GetTicksMsec() / 1000.0 + 0.15;
                break;
            case DamageFx d when d.VictimId == me:
                _hurt = Mathf.Min(1, _hurt + d.Amount / 60.0);
                break;
            case KillFx k:
                var how = k.Headshot ? " (헤드샷)" : k.Source == "melee" ? " (근접)" : k.Source == "explosion" ? " (폭발)" : "";
                var killer = k.KillerId < 0 ? "" : st.NameOf(k.KillerId) + " ▸ ";
                var col = k.KillerId == me ? new Color(1f, 0.9f, 0.4f) : k.VictimId == me ? new Color(1f, 0.4f, 0.4f) : new Color(1, 1, 1, 0.85f);
                Feed($"{killer}{st.NameOf(k.VictimId)}{how}", col);
                break;
            case NoticeFx n:
                Feed(Locale.T("notice", n.Kind) + (n.Value > 1000 ? $"  {Money(n.Value)}" : ""), new Color(0.6f, 0.85f, 1f), 8);
                break;
            case RejectedFx r:
                Feed(Locale.T("reject", r.Reason), new Color(1f, 0.55f, 0.4f));
                break;
            case ResultFx r:
                Feed(r.Success ? $"의뢰 완료  +{Money(r.Paid)}" : $"의뢰 실패 ({r.Reason})", r.Success ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.4f), 10);
                break;
            case MoneyFx m:
                if (m.WalletDelta != 0) Feed((m.WalletDelta > 0 ? "+" : "") + Money(m.WalletDelta) + " 현금", m.WalletDelta > 0 ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.7f, 0.5f), 4);
                if (m.BankDelta != 0) Feed((m.BankDelta > 0 ? "+" : "") + Money(m.BankDelta) + " 은행", new Color(0.85f, 0.95f, 1f), 4);
                break;
        }
    }

    public override void _Process(double delta)
    {
        var client = session.Client;
        if (client == null || !client.IsJoined) return;
        var st = session.State;
        var self = client.LocalServerState;
        _health.Value = self.Health;
        _armor.Value = self.Armor;
        _wallet.Text = Money(st.Wallet);
        _bank.Text = "은행 " + Money(st.Bank) + (st.Stash > 0 ? $"   금고 {Money(st.Stash)}" : "");
        _wanted.Text = st.WantedLevel > 0 ? new string('★', st.WantedLevel) + new string('☆', 5 - st.WantedLevel) : "";
        var wdef = session.Data.Weapons.GetValueOrDefault(st.WeaponId.Length > 0 ? st.WeaponId : "kestrel_p10");
        _weapon.Text = wdef?.DisplayName ?? "";
        _ammo.Text = st.WeaponId.Length > 0 ? $"{st.InMagazine} / {st.Reserve}" : "";

        var area = session.Data.Map.AreaAt(session.Predicted.Position)?.District ?? "";
        var areaName = session.Data.Economy.Safehouses.Count > 0 ? AreaName(area) : area;
        if (area != _lastArea && area.Length > 0) Feed(areaName, new Color(1, 1, 1, 0.9f), 3);
        _lastArea = area;
        _area.Text = areaName;

        var o = st.Objective;
        _objective.Text = o == null ? "" : Locale.T(o.Label);
        _objProgress.Visible = o != null && o.Progress > 0;
        _objProgress.Value = (o?.Progress ?? 0) * 100;
        UpdateWaypoint(o);

        var now = Time.GetTicksMsec() / 1000.0;
        _hitmarker.Visible = now < _hitUntil;
        _hurt = Mathf.Max(0, _hurt - delta * 0.8);
        var lowHp = self.Health < 35 ? 0.18f : 0f;
        _vignette.Color = new Color(0.6f, 0, 0, Mathf.Clamp((float)_hurt * 0.35f + lowHp, 0, 0.5f));

        var dead = (self.Flags & ActorFlags.Dead) != 0;
        var downed = (self.Flags & ActorFlags.Downed) != 0;
        _banner.Visible = dead || downed;
        _banner.Text = dead ? "사망 — " + Locale.T("ui.respawn") : downed ? Locale.T("ui.downed") : "";
        _banner.AddThemeColorOverride("font_color", dead ? new Color(0.9f, 0.25f, 0.2f) : new Color(1f, 0.8f, 0.3f));
        _crosshair.Visible = !dead;

        for (var i = _feedItems.Count - 1; i >= 0; i--)
            if (now > _feedItems[i].Until) { _feedItems[i].Label.QueueFree(); _feedItems.RemoveAt(i); }
    }

    private static string AreaName(string district) => district switch
    {
        "old_mill" => "Old Mill",
        "harbor_front" => "Harbor Front",
        "glass_heights" => "Glass Heights",
        _ => "",
    };

    private void UpdateWaypoint(Objective? o)
    {
        var cam = GetViewport().GetCamera3D();
        if (o == null || cam == null) { _waypoint.Visible = false; return; }
        var target = ActorViews.V(o.Target) + Vector3.Up * 1.5f;
        var size = GetViewport().GetVisibleRect().Size;
        var behind = cam.IsPositionBehind(target);
        var sp = cam.UnprojectPosition(target);
        if (behind) sp = size - sp;
        sp = new Vector2(Mathf.Clamp(sp.X, 30, size.X - 30), Mathf.Clamp(sp.Y, 80, size.Y - 30));
        _waypoint.Visible = true;
        _waypoint.Position = sp;
        var dist = session.Predicted.Position.WithY(0) - o.Target.WithY(0);
        _waypointLabel.Text = $"◆ {dist.Length:0}m";
    }

    private partial class Crosshair : Control
    {
        public bool Hit;
        public override void _Draw()
        {
            var c = Hit ? new Color(1f, 0.3f, 0.25f) : new Color(1, 1, 1, 0.9f);
            if (Hit)
            {
                foreach (var (x, y) in new[] { (1, 1), (-1, 1), (1, -1), (-1, -1) })
                    DrawLine(new Vector2(x * 6, y * 6), new Vector2(x * 13, y * 13), c, 2.5f);
                return;
            }
            DrawCircle(Vector2.Zero, 2.2f, c);
            foreach (var (x, y) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                DrawLine(new Vector2(x * 7, y * 7), new Vector2(x * 14, y * 14), c, 2f);
        }
    }
}
