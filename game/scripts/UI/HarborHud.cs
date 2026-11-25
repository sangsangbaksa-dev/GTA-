using System;
using System.Globalization;
using Godot;
using Harbor.Core.Combat;
using Harbor.Core.Math;
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

    private enum Screen { Title, Connect, Connecting, Play, Pause, Controls, Settings, Map, Result }

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
    private Label _operation = null!;
    private Label _objective = null!;
    private Label _detailText = null!;
    private Label _prompt = null!;
    private Label _waypoint = null!;
    private Label _wanted = null!;
    private Label _weapon = null!;
    private Label _ammo = null!;
    private Label _vitals = null!;
    private ProgressBar _health = null!;
    private ProgressBar _armor = null!;
    private CombatReticle _reticle = null!;
    private Label _connection = null!;
    private Screen _screen;
    private Screen _previous;
    private bool _remote;
    private bool _restarting;
    private string _server = "127.0.0.1";
    private string _port = "7788";

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
        _remote = _host.IsRemote;
        Show(_remote ? Screen.Connecting : Screen.Title);
    }

    public override void _ExitTree()
    {
        if (IsInstanceValid(_player)) _player.PauseRequested -= OnPauseRequested;
        if (IsInstanceValid(_host)) _host.ActorDamaged -= OnDamage;
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.M }) return;
        if (_screen == Screen.Play) Show(Screen.Map);
        else if (_screen == Screen.Map) Show(Screen.Play);
        else return;
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (_screen == Screen.Connecting)
        {
            _connection.Text = _host.ConnectionMessage;
            if (_host.ConnectionStatus == HarborConnectionStatus.Ready && _host.HasPlayer) Show(Screen.Play);
            return;
        }
        if (_screen == Screen.Map) _largeMap?.QueueRedraw();
        if (_remote && _host.ConnectionStatus is HarborConnectionStatus.Disconnected or HarborConnectionStatus.Failed)
        {
            if (_screen is Screen.Play or Screen.Pause or Screen.Map or Screen.Result) Show(Screen.Connecting);
            return;
        }
        if (_screen != Screen.Play || !_host.HasPlayer) return;

        var run = _host.Lockdown;
        if (run.Phase is LockdownPhase.Succeeded or LockdownPhase.Failed)
        {
            if (!_restarting) Show(Screen.Result);
            return;
        }
        _restarting = false;

        var player = _host.RenderPosition;
        var input = _player.LastInputWasGamepad ? "X" : "E";
        var movingInCar = _host.IsDriving;
        _operation.Text = run.Phase == LockdownPhase.FreeRoam ? "PORT CALLOWAY  /  HARBOR FRONT" : "HARBOR LOCKDOWN";
        _objective.Text = run.Phase switch
        {
            LockdownPhase.FirstWave => "Clear the quay",
            LockdownPhase.Reinforcements => "Hold the waterfront",
            LockdownPhase.Extraction => "Reach extraction",
            _ => "Free roam"
        };
        _detailText.Text = run.Phase switch
        {
            LockdownPhase.Reinforcements when run.ReinforcementSeconds > 0 =>
                $"REINFORCEMENTS IN {System.Math.Ceiling(run.ReinforcementSeconds):0} s",
            LockdownPhase.FirstWave or LockdownPhase.Reinforcements =>
                $"WAVE {run.WaveNumber}/{run.WaveCount}     {run.RemainingEnemies} HOSTILE{(run.RemainingEnemies == 1 ? "" : "S")}",
            LockdownPhase.Extraction => "RETURN TO THE EAST GATE",
            _ => "EXPLORE THE DISTRICT OR TAKE A JOB"
        };

        _prompt.Text = movingInCar ? $"{input}   Exit vehicle" : _host.CanEnterVehicle ? $"{input}   Enter vehicle" :
            run.CanStart ? $"{input}   Start Harbor Lockdown" : "";
        _prompt.Visible = _prompt.Text.Length > 0;
        _wanted.Text = _host.WantedLevel > 0 ? "WANTED    " + new string('★', _host.WantedLevel) : "";
        _wanted.Visible = _host.WantedLevel > 0;
        _reticle.Armed = !movingInCar;
        _reticle.Aiming = _player.IsAiming;
        _reticle.HasTarget = _player.AimHasTarget;
        _reticle.Advance((float)delta);

        var combat = _host.PlayerCombat;
        _health.Value = combat.MaxHealth > 0 ? (double)combat.Health / combat.MaxHealth : 0;
        _armor.Value = combat.MaxArmor > 0 ? (double)combat.Armor / combat.MaxArmor : 0;
        _vitals.Text = $"{combat.Health:00} HEALTH          {combat.Armor:00} ARMOR";
        _weapon.Text = movingInCar ? "CALLOWAY COMPACT" : combat.WeaponName.ToUpperInvariant();
        _ammo.Text = movingInCar ? $"{System.Math.Abs(_host.Vehicle.Speed) * 3.6:00} km/h" :
            combat.IsReloading ? "RELOADING" : $"{combat.Magazine:00}  /  {combat.Reserve:00}";
        _map.QueueRedraw();
        UpdateWaypoint(run, player);
    }

    private void UpdateWaypoint(HarborLockdownState run, Vec3d player)
    {
        var active = run.Phase is LockdownPhase.FreeRoam or LockdownPhase.Extraction;
        var target = run.Phase == LockdownPhase.Extraction ? run.ExtractionPosition : run.StartPosition;
        var distance = (target - player).Length;
        var point = CoreHost.ToGodot(target) + Vector3.Up * 2.15f;
        var camera = _player.Camera;
        if (!active || distance < 2.5 || camera.IsPositionBehind(point))
        {
            _waypoint.Hide();
            return;
        }

        var screen = camera.UnprojectPosition(point);
        var area = GetViewport().GetVisibleRect();
        if (!area.Grow(-38).HasPoint(screen) || (screen.X > area.End.X - 290 && screen.Y < 242))
        {
            _waypoint.Hide();
            return;
        }
        _waypoint.Text = $"◆  {(run.Phase == LockdownPhase.Extraction ? "EXTRACTION" : "LOCKDOWN")}  {distance:0} m";
        _waypoint.Position = screen - new Vector2(_waypoint.Size.X * 0.5f, 16);
        _waypoint.Show();
    }

    private void BuildGameHud()
    {
        var task = Card(_game, Control.LayoutPreset.TopLeft, new Vector2(26, 26), new Vector2(330, 0));
        _operation = Text(task, "", 13, Seafoam, _detail);
        _objective = Text(task, "", 36, White, _design);
        _detailText = Text(task, "", 12, Muted, _detail);

        var mapColumn = Card(_game, Control.LayoutPreset.TopRight, new Vector2(-232, 26), new Vector2(206, 0));
        _wanted = Text(mapColumn, "", 18, Amber, _design);
        _map = new DockMap { Host = _host, Player = _player, CustomMinimumSize = new Vector2(175, 152) };
        mapColumn.AddChild(_map);
        Text(mapColumn, "M  DISTRICT MAP", 12, Muted, _detail);

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
        _waypoint = Text(_game, "", 13, Amber, _detail);
    }

    private void OnPauseRequested()
    {
        switch (_screen)
        {
            case Screen.Play: Show(Screen.Pause); break;
            case Screen.Pause or Screen.Map: Show(Screen.Play); break;
            case Screen.Controls or Screen.Settings: Show(_previous); break;
            case Screen.Connect: Show(Screen.Title); break;
            case Screen.Connecting: CancelConnect(); break;
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
        if (screen is Screen.Title or Screen.Connect or Screen.Connecting || screen is (Screen.Controls or Screen.Settings) && _previous == Screen.Title)
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

        var column = MenuColumn();
        Text(column, "PORT CALLOWAY  /  HARBOR FRONT", 14, Seafoam, _detail);
        switch (screen)
        {
            case Screen.Title: BuildTitle(column); break;
            case Screen.Connect: BuildConnect(column); break;
            case Screen.Connecting: BuildConnecting(column); break;
            case Screen.Pause: BuildPause(column); break;
            case Screen.Controls: BuildControls(column); break;
            case Screen.Settings: BuildSettings(column); break;
            case Screen.Result: BuildResult(column); break;
        }
    }

    private void BuildTitle(VBoxContainer column)
    {
        Text(column, "PROJECT HARBOR", 82, White, _design);
        Text(column, "Open-world action on the Calloway waterfront.", 21, White, null);
        Text(column, "Drive the district, take on Harbor Lockdown, or see how long you can stay ahead of the port police.", 16, Muted, null, true);
        Space(column, 18);
        Button(column, "Play locally", () =>
        {
            if (_remote) _host.StartLocalSession();
            _remote = false;
            Show(_host.HasPlayer ? Screen.Play : Screen.Connecting);
        }, true);
        Button(column, "Join a server", () => Show(Screen.Connect));
        Button(column, "City operations", () => GetTree().ChangeSceneToFile(HarborLaunch.CityScene));
        Button(column, "Controls", () => ShowControls(Screen.Title));
        Button(column, "Settings", () => ShowSettings(Screen.Title));
        Button(column, "Quit game", () => GetTree().Quit());
    }

    private void BuildConnect(VBoxContainer column)
    {
        Text(column, "JOIN A SERVER", 62, White, _design);
        Text(column, "Enter the address of a running Project Harbor server.", 17, Muted, null, true);
        Space(column, 12);
        Text(column, "HOST OR IP ADDRESS", 12, Seafoam, _detail);
        var address = Field(column, _server, "127.0.0.1");
        Text(column, "PORT", 12, Seafoam, _detail);
        var port = Field(column, _port, "7788");
        var error = Text(column, "", 14, Red, null, true);
        void Join()
        {
            var host = address.Text.Trim();
            if (host.Length == 0 || !int.TryParse(port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                out var number) || number is < 1 or > 65535)
            {
                error.Text = "Enter a host and a valid port (1–65535).";
                return;
            }
            _server = host;
            _port = number.ToString(CultureInfo.InvariantCulture);
            _remote = true;
            _host.ConnectToServer(host, number);
            Show(Screen.Connecting);
        }
        address.TextSubmitted += _ => Join();
        port.TextSubmitted += _ => Join();
        Space(column, 8);
        Button(column, "Connect", Join, true);
        Button(column, "Back", () => Show(Screen.Title));
        address.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void BuildConnecting(VBoxContainer column)
    {
        Text(column, _remote ? "HARBOR SERVER" : "ENTERING CALLOWAY", 62, White, _design);
        _connection = Text(column, _host.ConnectionMessage, 17, Muted, null, true);
        Space(column, 14);
        Button(column, "Back", CancelConnect, true);
    }

    private void CancelConnect()
    {
        if (_remote)
        {
            _host.StartLocalSession();
            _remote = false;
            Show(Screen.Connect);
        }
        else Show(Screen.Title);
    }

    private void BuildPause(VBoxContainer column)
    {
        Text(column, "GAME MENU", 62, White, _design);
        if (_remote) Text(column, "The server continues while this menu is open.", 16, Muted, null);
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
        ControlRow(column, "Interact / enter vehicle", "E", "X");
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

    private void BuildResult(VBoxContainer column)
    {
        var complete = _host.Lockdown.Phase == LockdownPhase.Succeeded;
        Text(column, complete ? "LOCKDOWN CLEARED" : "RUN ENDED", 62, White, _design);
        Text(column, complete ? $"Extraction reached in {FormatTime(_host.Lockdown.Elapsed)}." : "Rook was taken down in the harbor.", 18, Muted, null, true);
        if (complete && _host.BestTime is { } best)
            Text(column, $"BEST  {FormatTime(best)}", 16, Amber, _detail);
        Space(column, 18);
        Button(column, "Return to the district", () =>
        {
            _restarting = true;
            _host.RestartLockdown();
            Show(Screen.Play);
        }, true);
        Button(column, "Return to title", ReturnToTitle);
    }

    private void ReturnToTitle()
    {
        _restarting = false;
        _host.StartLocalSession();
        _remote = false;
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
        Text(column, "AMBER  LOCKDOWN     TEAL  VEHICLE / EXTRACTION     WHITE  YOU", 12, Muted, _detail);
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

    private static Label Text(Node parent, string value, int size, Color color, Font? font, bool wrap = false)
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

    private static StyleBoxFlat Box(Color color, int radius, int margin)
    {
        var style = new StyleBoxFlat { BgColor = color, ContentMarginLeft = margin, ContentMarginRight = margin,
            ContentMarginTop = margin, ContentMarginBottom = margin };
        style.SetCornerRadiusAll(radius);
        return style;
    }

    private void Button(VBoxContainer column, string title, Action pressed, bool primary = false)
    {
        var button = new Button { Text = title, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            CustomMinimumSize = new Vector2(252, 44), Alignment = HorizontalAlignment.Left };
        button.AddThemeFontSizeOverride("font_size", 20);
        if (_design != null) button.AddThemeFontOverride("font", _design);
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
        button.Pressed += pressed;
        column.AddChild(button);
        if (primary) button.CallDeferred(Control.MethodName.GrabFocus);
    }

    private static LineEdit Field(VBoxContainer column, string value, string placeholder)
    {
        var field = new LineEdit { Text = value, PlaceholderText = placeholder,
            CustomMinimumSize = new Vector2(310, 39), SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        field.AddThemeStyleboxOverride("normal", Box(new Color("#243b44"), 5, 8));
        column.AddChild(field);
        return field;
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

    private static string FormatTime(double seconds) => $"{(int)(seconds / 60):00}:{(int)(seconds % 60):00}";
}
