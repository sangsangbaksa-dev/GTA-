using System;
using Godot;
using Harbor.Game.Player;

namespace Harbor.Game.UI;

public partial class HarborRoster : CenterContainer
{
    private static readonly Color Paper = new("#f4f0e8");
    private static readonly Color Gray = new("#aac0c1");
    private const string CharacterRoot = "res://assets/characters/";

    private sealed record Role(string DisplayName, string Job, string Description, string Model, string Fallback);

    private static readonly Role[] People =
    {
        new("Rook", "Courier", "Rook takes the jobs that move between the port and the city.",
            "chr_harbor_rook", "chr_player_rook"),
        new("Mara Vale", "Port Officer", "Vale works patrol across the quays and bridge approaches.",
            "chr_harbor_officer", "chr_enemy_enforcer"),
        new("Niko Voss", "Rival Crew", "Voss runs his crew between the yards and the market streets.",
            "chr_harbor_crew", "chr_enemy_gunman"),
        new("Amara Cole", "Dock electrician", "Cole keeps cranes and shore power running from the loading apron to the basin.",
            "chr_harbor_dockhand", "chr_npc_civilian"),
    };

    private Camera3D _camera = null!;
    private Label _name = null!;
    private Label _role = null!;
    private Label _description = null!;
    private Node3D _turntable = null!;
    private Node3D? _person;
    private Button _framing = null!;
    private int _index;
    private bool _face;
    private float _height = 1.75f;

    public void Build(Font? display, Font? detail, Action back)
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(840, 516) };
        panel.AddThemeStyleboxOverride("panel", HarborHud.Box(new Color("#102430fa"), 11, 22));
        AddChild(panel);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 22);
        panel.AddChild(columns);
        CreateViewport(columns);
        var summary = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        summary.AddThemeConstantOverride("separation", 12);
        columns.AddChild(summary);

        HarborHud.Text(summary, "PEOPLE OF PORT CALLOWAY", 13, HarborHud.Seafoam, detail);
        _name = HarborHud.Text(summary, "", 54, Paper, display);
        _role = HarborHud.Text(summary, "", 18, HarborHud.Amber, detail);
        _description = HarborHud.Text(summary, "", 17, Gray, null, true);
        HarborHud.Text(summary, "Drag the model to rotate it.", 14, Gray, null);
        var controls = new HBoxContainer();
        controls.AddThemeConstantOverride("separation", 7);
        summary.AddChild(controls);
        var previous = HarborHud.NewButton("Previous", display);
        previous.CustomMinimumSize = new Vector2(102, 44);
        previous.Pressed += () => ShowPerson(_index - 1);
        controls.AddChild(previous);
        var next = HarborHud.NewButton("Next", display);
        next.CustomMinimumSize = new Vector2(74, 44);
        next.Pressed += () => ShowPerson(_index + 1);
        controls.AddChild(next);
        _framing = HarborHud.NewButton("Face detail", display);
        _framing.Pressed += () =>
        {
            _face = !_face;
            FramePerson();
        };
        summary.AddChild(_framing);
        var returnButton = HarborHud.NewButton("Back to title", display, true);
        returnButton.Pressed += back;
        summary.AddChild(returnButton);
        next.CallDeferred(Control.MethodName.GrabFocus);
        ShowPerson(0);
    }

    private void CreateViewport(Control parent)
    {
        var container = new SubViewportContainer { Stretch = true, CustomMinimumSize = new Vector2(388, 474),
            MouseFilter = MouseFilterEnum.Stop };
        parent.AddChild(container);
        var viewport = new SubViewport { OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
            Msaa3D = Viewport.Msaa.Msaa2X, Size = new Vector2I(388, 474) };
        container.AddChild(viewport);
        container.GuiInput += @event =>
        {
            if (@event is InputEventMouseMotion motion && (motion.ButtonMask & MouseButtonMask.Left) != 0)
                _turntable.RotateY(motion.Relative.X * 0.012f);
        };
        var stage = new Node3D();
        viewport.AddChild(stage);
        stage.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color("#193945"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color("#cbdde7"),
                AmbientLightEnergy = 0.52f
            }
        });
        stage.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-32, -31, 0),
            LightColor = new Color("#ffe7cc"), LightEnergy = 1.04f, ShadowEnabled = true });
        stage.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-22, 150, 0),
            LightColor = new Color("#a1d7dc"), LightEnergy = 0.40f });
        var ground = new CylinderMesh { TopRadius = 0.91f, BottomRadius = 1.05f, Height = 0.10f, RadialSegments = 48,
            Material = new StandardMaterial3D { AlbedoColor = new Color("#294b56"), Roughness = 0.85f } };
        stage.AddChild(new MeshInstance3D { Mesh = ground, Position = new Vector3(0, -0.07f, 0) });
        _turntable = new Node3D();
        stage.AddChild(_turntable);
        _camera = new Camera3D { Fov = 35, Near = 0.03f, Far = 18, Current = true };
        stage.AddChild(_camera);
    }

    private void ShowPerson(int index)
    {
        _index = (index + People.Length) % People.Length;
        var role = People[_index];
        _name.Text = role.DisplayName;
        _role.Text = role.Job;
        _description.Text = role.Description;
        if (_person != null)
        {
            _turntable.RemoveChild(_person);
            _person.QueueFree();
        }
        var path = CharacterRoot + role.Model + ".glb";
        if (!ResourceLoader.Exists(path)) path = CharacterRoot + role.Fallback + ".glb";
        _person = ResourceLoader.Load<PackedScene>(path)?.Instantiate<Node3D>();
        _height = 1.75f;
        if (_person != null)
        {
            _turntable.AddChild(_person);
            RookGait.Create(_person)?.Update(0, 0, false, 1);
            foreach (var child in _person.FindChildren("*", "MeshInstance3D", true, false))
            {
                if (child is not MeshInstance3D mesh) continue;
                var local = _person.ToLocal(mesh.ToGlobal(mesh.GetAabb().End));
                _height = Mathf.Max(_height, local.Y);
            }
        }
        _turntable.RotationDegrees = new Vector3(0, -12, 0);
        FramePerson();
    }

    private void FramePerson()
    {
        var target = Vector3.Up * _height * (_face ? 0.91f : 0.51f);
        _camera.Position = target + new Vector3(0, _face ? 0.023f : 0.1f, _face ? 0.63f : _height * 1.78f);
        _camera.LookAt(target);
        _framing.Text = _face ? "Full figure" : "Face detail";
    }
}
