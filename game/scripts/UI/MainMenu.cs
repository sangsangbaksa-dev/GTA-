using Godot;
using Harbor.Game.Bridge;

namespace Harbor.Game.UI;

/// <summary>Title screen: name + offline / host (listen server, up to 16) / join by address.</summary>
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
        var addr = new LineEdit { Text = "127.0.0.1", PlaceholderText = "서버 주소" };
        var port = new SpinBox { MinValue = 1024, MaxValue = 65535, Value = GameSession.DefaultPort, Prefix = "포트" };

        void Add(string text, System.Action a)
        {
            var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 44) };
            b.Pressed += a;
            v.AddChild(b);
        }
        Add("싱글 플레이", () => session.Start(GameSession.Mode.Offline, name.Text, "", (int)port.Value));
        Add("서버 열기 (최대 16명)", () => session.Start(GameSession.Mode.Host, name.Text, "", (int)port.Value));
        v.AddChild(addr);
        v.AddChild(port);
        Add("접속", () => session.Start(GameSession.Mode.Join, name.Text, addr.Text, (int)port.Value));
        Add("종료", () => GetTree().Quit());
        v.AddChild(new Label
        {
            Text = "더러운 돈은 사망 시 일부 떨어집니다. 세탁해서 은행에 넣으세요.\n전용 서버: godot --headless -- --server --port 27960",
            HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.55f),
        });
    }
}
