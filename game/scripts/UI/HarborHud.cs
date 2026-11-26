using System;
using System.Linq;
using Godot;
using Harbor.Core.Combat;
using Harbor.Core.Server;
using Harbor.Game.Bridge;
using Harbor.Game.Player;

namespace Harbor.Game.UI;

public partial class HarborHud : CanvasLayer
{
    private const string DesignFont = "res://assets/fonts/BarlowSemiCondensed-Bold.ttf";
    private const string DetailFont = "res://assets/fonts/IBMPlexMono-Regular.ttf";
    internal static readonly Color Amber = new("#f7bc70");
    internal static readonly Color Seafoam = new("#88ded5");
    internal static readonly Color Ink = new("#101c24");
    private static readonly Color White = new("#f4f0e8");
    private static readonly Color Muted = new("#aac0c1");
    private static readonly Color Red = new("#ff8071");

    private enum Screen { Title, Loading, Play, Pause, Controls, Settings, Map, Roster }

    private CoreHost _host = null!;
    private ThirdPersonController _player = null!;
    private Font? _design;
    private Font? _detail;
    private Control _root = null!;
    private Control _game = null!;
    private Control _menu = null!;
    private Camera3D _intro = null!;
    private DockMap _map = null!;
    private DockMap? _largeMap;
    private Label _district = null!;
    private Label _wallet = null!;
    private Label _prompt = null!;
    private Label _wanted = null!;
    private Label _weapon = null!;
    private Label _ammo = null!;
    private Label _vitals = null!;
    private ProgressBar _health = null!;
    private ProgressBar _armor = null!;
    private CombatReticle _reticle = null!;
    private Label _notice = null!;
    private PanelContainer _noticeCard = null!;
    private Screen _screen;
    private Screen _previous;
    private int _crimeSequence;
    private float _noticeLeft;

    public override void _Ready()
    {
        _host = GetNode<CoreHost>("../CoreHost");
        _player = GetNode<ThirdPersonController>("../Player");
        _design = ResourceLoader.Load<Font>(DesignFont);
        _detail = ResourceLoader.Load<Font>(DetailFont);
        _player.PauseRequested += OnPauseRequested;
        _host.ActorDamaged += OnDamage;

        _root = Fill(new Control { MouseFilter = Control.MouseFilterEnum.Ignore }, this);
        _game = Fill(new Control { MouseFilter = Control.MouseFilterEnum.Ignore }, _root);
        _menu = Fill(new Control { MouseFilter = Control.MouseFilterEnum.Ignore }, _root);
        _intro = GetNode<Camera3D>("../HarborOverview");
        _intro.LookAt(new Vector3(2, 1.2f, -15));
        BuildGameHud();
        ReadSettings();
        Show(Screen.Title);
    }

    public override void _ExitTree()
    {
        if (IsInstanceValid(_player)) _player.PauseRequested -= OnPauseRequested;
        if (IsInstanceValid(_host)) _host.ActorDamaged -= OnDamage;
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.PhysicalKeycode == Key.M && (_screen is Screen.Play or Screen.Map))
            Show(_screen == Screen.Play ? Screen.Map : Screen.Play);
        else return;
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (_screen == Screen.Loading)
        {
            if (_host.HasPlayer) Show(Screen.Play);
            return;
        }
        if (_screen == Screen.Map) _largeMap?.QueueRedraw();
        if (_screen != Screen.Play || !_host.HasPlayer) return;

        var player = _host.RenderPosition;
        var area = _host.Layout.Districts.FirstOrDefault(where =>
            System.Math.Abs(player.X - where.Center[0]) <= where.Size[0] / 2 &&
            System.Math.Abs(player.Z - where.Center[1]) <= where.Size[1] / 2);
        _district.Text = area?.Name ?? "Port Calloway";
        var crime = _host.StreetCrime;
        _wallet.Text = $"CASH  ${crime.DirtyCash:N0}";
        var input = _player.LastInputWasGamepad ? "X" : "F";
        var movingInCar = _host.IsDriving;
        var action = _host.StreetAction;
        var combat = _host.PlayerCombat;
        var canAct = combat.Health > 0;
        var vehicle = movingInCar ? $"{input}   Exit vehicle" : action.VehicleId >= 0
            ? $"{input}   {(action.VehicleOccupied ? "Take vehicle" : "Enter vehicle")}" : "";
        var theftInput = _player.LastInputWasGamepad ? "LB" : "G";
        var theft = action.CanPickpocket ? $"{theftInput}   Pickpocket" : action.CargoId.Length > 0 ? $"{theftInput}   Take cargo" : "";
        _prompt.Text = !canAct ? "" : vehicle.Length > 0 && theft.Length > 0
            ? vehicle + "    ·    " + theft : vehicle + theft;
        _prompt.Visible = _prompt.Text.Length > 0;
        _wanted.Visible = _host.WantedLevel > 0;
        _wanted.Text = new string('★', _host.WantedLevel) +
            new string('☆', System.Math.Max(0, _host.MaxWantedLevel - _host.WantedLevel));
        _reticle.Armed = !movingInCar && canAct;
        _reticle.Aiming = _player.IsAiming;
        _reticle.HasTarget = _player.AimHasTarget;
        _reticle.Advance((float)delta);

        _health.Value = combat.MaxHealth > 0 ? (double)combat.Health / combat.MaxHealth : 0;
        _armor.Value = combat.MaxArmor > 0 ? (double)combat.Armor / combat.MaxArmor : 0;
        _vitals.Text = $"{combat.Health:00} HEALTH          {combat.Armor:00} ARMOR";
        _weapon.Text = movingInCar ? _host.Vehicle.DisplayName.ToUpperInvariant() : combat.WeaponName.ToUpperInvariant();
        _ammo.Text = movingInCar ? $"{System.Math.Abs(_host.Vehicle.Speed) * 3.6:00} km/h" :
            combat.IsReloading ? "RELOADING" : $"{combat.Magazine:00}  /  {combat.Reserve:00}";
        UpdateNotice((float)delta, crime, canAct);
        _map.QueueRedraw();
    }

    private void UpdateNotice(float elapsed, HarborStreetCrime crime, bool alive)
    {
        if (crime.Sequence != _crimeSequence)
        {
            _crimeSequence = crime.Sequence;
            _noticeLeft = 3.5f;
            _notice.Text = crime.Status switch
            {
                "pickpocketed" => $"Pickpocketed   +${crime.Amount:N0}",
                "cargo_taken" => $"Cargo taken   +${crime.Amount:N0}",
                "spotted" => "You were spotted and reported",
                "vehicle_taken" => "Vehicle theft reported",
                "no_target" => "Nothing within reach",
                "exit_blocked" => "Stop and leave room beside the door",
                _ => "",
            };
        }
        if (!alive)
        {
            _notice.Text = "Returning to the harbor";
            _noticeLeft = 0.5f;
        }
        _noticeLeft = Mathf.Max(0, _noticeLeft - elapsed);
        _noticeCard.Visible = _noticeLeft > 0 && _notice.Text.Length > 0;
    }

    private void BuildGameHud()
    {
        var task = Card(_game, Control.LayoutPreset.TopLeft, new Vector2(26, 26), new Vector2(330, 0));
        Text(task, "PORT CALLOWAY", 13, Seafoam, _detail);
        _district = Text(task, "", 36, White, _design);
        _wallet = Text(task, "", 12, Muted, _detail, true);

        var mapColumn = Card(_game, Control.LayoutPreset.TopRight, new Vector2(-232, 26), new Vector2(206, 0));
        _wanted = Text(mapColumn, "", 18, Amber, _design);
        _map = new DockMap { Host = _host, Player = _player, CustomMinimumSize = new Vector2(175, 152) };
        mapColumn.AddChild(_map);
        Text(mapColumn, "M  MAP", 12, Muted, _detail);

        var status = Card(_game, Control.LayoutPreset.BottomRight, new Vector2(-248, -169), new Vector2(222, 0));
        _weapon = Text(status, "", 15, Muted, _detail);
        _ammo = Text(status, "", 31, White, _design);
        _health = Bar(status, Seafoam);
        _armor = Bar(status, new Color("#78a4d6"));
        _vitals = Text(status, "", 11, Muted, _detail);

        _reticle = Fill(new CombatReticle { MouseFilter = Control.MouseFilterEnum.Ignore }, _game);
        _prompt = Text(_game, "", 21, Amber, _design);
        _prompt.HorizontalAlignment = HorizontalAlignment.Center;
        _prompt.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        _prompt.OffsetLeft = -225;
        _prompt.OffsetRight = 225;
        _prompt.OffsetTop = -100;
        _prompt.OffsetBottom = -70;
        var notice = Card(_game, Control.LayoutPreset.CenterTop, new Vector2(-190, 26), new Vector2(380, 0));
        _notice = Text(notice, "", 22, Seafoam, _design);
        _notice.HorizontalAlignment = HorizontalAlignment.Center;
        _noticeCard = notice.GetParent<PanelContainer>();
        _noticeCard.Hide();
    }

    private void OnPauseRequested()
    {
        switch (_screen)
        {
            case Screen.Play: Show(Screen.Pause); break;
            case Screen.Pause or Screen.Map: Show(Screen.Play); break;
            case Screen.Controls or Screen.Settings: Show(_previous); break;
            case Screen.Roster: Show(Screen.Title); break;
            case Screen.Loading: Show(Screen.Title); break;
        }
    }

    private void OnDamage(ActorDamaged damage)
    {
        if (!_host.HasPlayer) return;
        var actorId = _host.Client.LocalActorId;
        if (damage.VictimId == actorId) _reticle.FlashDamage();
        else if (damage.AttackerId == actorId) _reticle.FlashHit();
    }

    private void Show(Screen screen)
    {
        _screen = screen;
        foreach (var child in _menu.GetChildren())
        {
            _menu.RemoveChild(child);
            child.QueueFree();
        }
        _largeMap = null;
        var playing = screen == Screen.Play;
        _game.Visible = playing;
        _menu.Visible = !playing;
        _player.SetPaused(!playing);
        if (screen is Screen.Title or Screen.Loading or Screen.Roster || screen is (Screen.Controls or Screen.Settings) && _previous == Screen.Title)
            _intro.MakeCurrent();
        else
            _player.Camera.MakeCurrent();
        if (playing) return;

        Shade();
        if (screen == Screen.Map)
        {
            BuildDistrictMap();
            return;
        }
        if (screen == Screen.Roster)
        {
            var gallery = Fill(new HarborRoster(), _menu);
            gallery.Build(_design, _detail, () => Show(Screen.Title));
            return;
        }

        var column = MenuColumn();
        Text(column, "PORT CALLOWAY", 14, Seafoam, _detail);
        switch (screen)
        {
            case Screen.Title: BuildTitle(column); break;
            case Screen.Loading: Text(column, "ENTERING CALLOWAY", 62, White, _design); break;
            case Screen.Pause: BuildPause(column); break;
            case Screen.Controls: BuildControls(column); break;
            case Screen.Settings: BuildSettings(column); break;
        }
    }

    private void BuildTitle(VBoxContainer column)
    {
        Text(column, "PROJECT HARBOR", 82, White, _design);
        Text(column, "A single-player open world across Port Calloway.", 21, White, null);
        Text(column, "Drive through the harbor, downtown, forest, and desert roads at your own pace.", 16, Muted, null, true);
        Space(column, 18);
        Button(column, "Start game", () => Show(_host.HasPlayer ? Screen.Play : Screen.Loading), true);
        Button(column, "Harbor characters", () => Show(Screen.Roster));
        Button(column, "Controls", () => ShowControls(Screen.Title));
        Button(column, "Settings", () => ShowSettings(Screen.Title));
        Button(column, "Quit game", () => GetTree().Quit());
    }

    private void BuildPause(VBoxContainer column)
    {
        Text(column, "GAME MENU", 62, White, _design);
        Space(column, 18);
        Button(column, "Resume", () => Show(Screen.Play), true);
        Button(column, "District map", () => Show(Screen.Map));
        Button(column, "Controls", () => ShowControls(Screen.Pause));
        Button(column, "Settings", () => ShowSettings(Screen.Pause));
        Button(column, "Return to title", ReturnToTitle);
        Button(column, "Quit game", () => GetTree().Quit());
    }

    private void ShowControls(Screen previous)
    {
        _previous = previous;
        Show(Screen.Controls);
    }

    private void ShowSettings(Screen previous)
    {
        _previous = previous;
        Show(Screen.Settings);
    }

    private void BuildControls(VBoxContainer column)
    {
        Text(column, "CONTROLS", 62, White, _design);
        ControlRow(column, "Move / steer", "WASD / arrows", "Left stick");
        ControlRow(column, "Look", "Mouse", "Right stick");
        ControlRow(column, "Aim / fire", "Right / left mouse", "LT / RT");
        ControlRow(column, "Reload / switch weapon", "R / 1 and 2", "RB / Y");
        ControlRow(column, "Accelerate / reverse", "W / S", "RT / LT");
        ControlRow(column, "Sprint / handbrake", "Shift / Space", "L3 / A");
        ControlRow(column, "Crouch", "C", "B");
        ControlRow(column, "Enter / exit / take vehicle", "F", "X");
        ControlRow(column, "Pickpocket / Take cargo", "G", "LB");
        ControlRow(column, "District map", "M", "Game menu");
        ControlRow(column, "Menu", "Esc", "Menu");
        Space(column, 14);
        Button(column, "Back", () => Show(_previous), true);
    }

    private const string SettingsPath = "user://harbor-display.cfg";

    private void BuildSettings(VBoxContainer column)
    {
        Text(column, "SETTINGS", 62, White, _design);
        Text(column, "3D RENDER SCALE", 13, Seafoam, _detail);
        Slider(column, 0.60, 1.0, 0.05, GetViewport().Scaling3DScale, value =>
        {
            GetViewport().Scaling3DScale = (float)value;
            SaveSettings();
        });
        Text(column, "MOUSE SENSITIVITY", 13, Seafoam, _detail);
        Slider(column, 0.001, 0.006, 0.00025, _player.MouseSensitivity, value =>
        {
            _player.MouseSensitivity = (float)value;
            SaveSettings();
        });
        Text(column, "MASTER VOLUME", 13, Seafoam, _detail);
        Slider(column, 0, 1, 0.05, MasterVolume(), value =>
        {
            SetMasterVolume((float)value);
            SaveSettings();
        });
        Space(column, 16);
        Button(column, "Toggle fullscreen", () =>
        {
            var fullscreen = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
            DisplayServer.WindowSetMode(fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
            SaveSettings();
        });
        Button(column, "Back", () => Show(_previous), true);
    }

    private static void Slider(VBoxContainer column, double min, double max, double step, double value, Action<double> change)
    {
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value,
            CustomMinimumSize = new Vector2(285, 24), SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        slider.ValueChanged += next => change(next);
        column.AddChild(slider);
    }

    private void ReadSettings()
    {
        var config = new ConfigFile();
        if (config.Load(SettingsPath) != Error.Ok) return;
        GetViewport().Scaling3DScale = Mathf.Clamp(config.GetValue("video", "scale", 1f).AsSingle(), 0.6f, 1f);
        _player.MouseSensitivity = Mathf.Clamp(config.GetValue("input", "mouse", _player.MouseSensitivity).AsSingle(), 0.001f, 0.006f);
        SetMasterVolume(config.GetValue("audio", "master", 1f).AsSingle());
        if (config.GetValue("video", "fullscreen", false).AsBool()) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
    }

    private void SaveSettings()
    {
        var config = new ConfigFile();
        config.SetValue("video", "scale", GetViewport().Scaling3DScale);
        config.SetValue("video", "fullscreen", DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen);
        config.SetValue("input", "mouse", _player.MouseSensitivity);
        config.SetValue("audio", "master", MasterVolume());
        config.Save(SettingsPath);
    }

    private static float MasterVolume() => AudioServer.IsBusMute(0) ? 0 : Mathf.DbToLinear(AudioServer.GetBusVolumeDb(0));

    private static void SetMasterVolume(float volume)
    {
        AudioServer.SetBusMute(0, volume <= 0);
        AudioServer.SetBusVolumeDb(0, Mathf.LinearToDb(Mathf.Clamp(volume, 0.0001f, 1f)));
    }

    private void ReturnToTitle()
    {
        _crimeSequence = 0;
        _noticeLeft = 0;
        _host.StartLocalSession();
        Show(Screen.Title);
    }

    private void BuildDistrictMap()
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(620, 480) };
        panel.AddThemeStyleboxOverride("panel", Box(new Color("#132832"), 12, 22));
        var center = Fill(new CenterContainer(), _menu);
        center.AddChild(panel);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        panel.AddChild(column);
        Text(column, "PORT CALLOWAY", 34, White, _design);
        _largeMap = new DockMap { Host = _host, Player = _player, EntireDistrict = true, CustomMinimumSize = new Vector2(550, 350) };
        column.AddChild(_largeMap);
        Text(column, "TEAL  VEHICLES     BLUE  POLICE     WHITE  YOU", 12, Muted, _detail);
        Button(column, "Return to game", () => Show(Screen.Play), true);
    }

    private void Shade()
    {
        var gradient = new Gradient
        {
            Offsets = new[] { 0f, 0.36f, 0.70f, 1f },
            Colors = new[] { new Color("#09171ff9"), new Color("#0a1a23f5"), new Color("#0b1a21a8"), new Color("#0b1a2130") }
        };
        var texture = new GradientTexture2D { Gradient = gradient, FillFrom = Vector2.Zero, FillTo = Vector2.Right };
        Fill(new TextureRect { Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = Control.MouseFilterEnum.Ignore }, _menu);
    }

    private VBoxContainer MenuColumn()
    {
        var column = new VBoxContainer { Position = new Vector2(56, 64), Size = new Vector2(544, 0) };
        column.AddThemeConstantOverride("separation", 8);
        _menu.AddChild(column);
        return column;
    }

    private static T Fill<T>(T node, Node parent) where T : Control
    {
        parent.AddChild(node);
        node.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return node;
    }

    private static VBoxContainer Card(Control parent, Control.LayoutPreset anchor, Vector2 offset, Vector2 size)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", Box(new Color("#0b1924ea"), 7, 12));
        parent.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(anchor);
        panel.Position += offset;
        panel.Size = size;
        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 5);
        panel.AddChild(column);
        return column;
    }

    internal static Label Text(Node parent, string value, int size, Color color, Font? font, bool wrap = false)
    {
        var label = new Label { Text = value, MouseFilter = Control.MouseFilterEnum.Ignore };
        if (wrap) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        if (font != null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_shadow_color", new Color("#091219c9"));
        label.AddThemeConstantOverride("shadow_offset_y", 1);
        parent.AddChild(label);
        return label;
    }

    private void ControlRow(VBoxContainer column, string action, string keyboard, string gamepad)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        column.AddChild(row);
        Text(row, action, 16, White, null).CustomMinimumSize = new Vector2(206, 0);
        Text(row, keyboard, 14, Seafoam, _detail).CustomMinimumSize = new Vector2(170, 0);
        Text(row, gamepad, 14, Muted, _detail);
    }

    internal static StyleBoxFlat Box(Color color, int radius, int margin)
    {
        var style = new StyleBoxFlat { BgColor = color, ContentMarginLeft = margin, ContentMarginRight = margin,
            ContentMarginTop = margin, ContentMarginBottom = margin };
        style.SetCornerRadiusAll(radius);
        return style;
    }

    internal static Button NewButton(string title, Font? display, bool primary = false)
    {
        var button = new Button { Text = title, CustomMinimumSize = new Vector2(0, 44), Alignment = HorizontalAlignment.Left };
        button.AddThemeFontSizeOverride("font_size", 20);
        if (display != null) button.AddThemeFontOverride("font", display);
        button.AddThemeColorOverride("font_color", primary ? Ink : White);
        button.AddThemeColorOverride("font_hover_color", Ink);
        button.AddThemeColorOverride("font_pressed_color", Ink);
        button.AddThemeColorOverride("font_focus_color", primary ? Ink : White);
        button.AddThemeStyleboxOverride("normal", Box(primary ? Amber : new Color("#233b43"), 6, 10));
        button.AddThemeStyleboxOverride("hover", Box(new Color("#ffcf8f"), 6, 10));
        button.AddThemeStyleboxOverride("pressed", Box(new Color("#dea661"), 6, 10));
        var focus = Box(new Color(0, 0, 0, 0), 6, 0);
        focus.BorderColor = White;
        focus.SetBorderWidthAll(2);
        button.AddThemeStyleboxOverride("focus", focus);
        return button;
    }

    private void Button(VBoxContainer column, string title, Action pressed, bool primary = false)
    {
        var button = NewButton(title, _design, primary);
        button.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        button.CustomMinimumSize = new Vector2(252, 44);
        button.Pressed += pressed;
        column.AddChild(button);
        if (primary) button.CallDeferred(Control.MethodName.GrabFocus);
    }

    private static ProgressBar Bar(VBoxContainer column, Color color)
    {
        var bar = new ProgressBar { ShowPercentage = false, MaxValue = 1, CustomMinimumSize = new Vector2(170, 5), Step = 0.001 };
        bar.AddThemeStyleboxOverride("background", Box(new Color("#2a424b"), 2, 0));
        bar.AddThemeStyleboxOverride("fill", Box(color, 2, 0));
        column.AddChild(bar);
        return bar;
    }

    private static void Space(VBoxContainer column, float amount) =>
        column.AddChild(new Control { CustomMinimumSize = new Vector2(1, amount), MouseFilter = Control.MouseFilterEnum.Ignore });

}
