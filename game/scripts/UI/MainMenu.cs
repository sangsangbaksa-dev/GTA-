using Godot;
using Harbor.Game.Bridge;

namespace Harbor.Game.UI;

/// <summary>Single-player entry point for City Operations.</summary>
public partial class MainMenu(GameSession session) : CanvasLayer
{
    public override void _Ready()
    {
        Layer = 10;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        var bg = new ColorRect { Color = new Color(0.04f, 0.05f, 0.07f, 0.82f) };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        v.AddThemeConstantOverride("separation", 10);
        center.AddChild(v);

        var title = new Label { Text = "PROJECT HARBOR", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 54);
        title.AddThemeColorOverride("font_color", new Color(1f, 0.8f, 0.35f));
        v.AddChild(title);
        v.AddChild(new Label { Text = "Port Calloway", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.6f) });

        var name = new LineEdit { Text = "rook", PlaceholderText = "이름" };
        v.AddChild(name);

        void Add(string text, System.Action a)
        {
            var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 44) };
            b.Pressed += a;
            v.AddChild(b);
        }
        Add("게임 시작", () => session.Start(name.Text));
        Add("항만 플레이테스트", () => GetTree().ChangeSceneToFile(HarborLaunch.PlaytestScene));
        Add("종료", () => GetTree().Quit());
        v.AddChild(new Label
        {
            Text = "더러운 돈은 사망 시 일부 떨어집니다. 세탁해서 은행에 넣으세요.",
            HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.55f),
        });
    }
}
