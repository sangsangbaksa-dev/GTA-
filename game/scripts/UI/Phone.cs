using System.Linq;
using Godot;
using Harbor.Core.Economy;
using Harbor.Core.Server;
using Harbor.Game.Bridge;
using Harbor.Game.Player;

namespace Harbor.Game.UI;

/// <summary>In-game phone (Tab): contracts, shop, laundering, stash, taxi, crew. Every button only sends a
/// request; the server validates and answers with balance/notice/rejection events.</summary>
public partial class Phone(GameSession session) : CanvasLayer
{
    private PanelContainer _panel = null!;
    private TabContainer _tabs = null!;
    private Label _status = null!;
    private SpinBox _amount = null!;

    public override void _Ready()
    {
        Layer = 5;
        _panel = new PanelContainer { Visible = false };
        _panel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
        _panel.Position = new Vector2(-520, -330);
        _panel.CustomMinimumSize = new Vector2(480, 660);
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.06f, 0.07f, 0.09f, 0.94f), CornerRadiusTopLeft = 18, CornerRadiusTopRight = 18, CornerRadiusBottomLeft = 18, CornerRadiusBottomRight = 18, ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 12, ContentMarginBottom = 12 });
        AddChild(_panel);
        var v = new VBoxContainer();
        _panel.AddChild(v);
        _status = new Label();
        v.AddChild(_status);
        _tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        v.AddChild(_tabs);

        BuildContracts();
        BuildShop();
        BuildMoney();
        BuildTravel();
        BuildCrew();
        v.AddChild(new Label { Text = "Tab: 닫기", Modulate = new Color(1, 1, 1, 0.5f) });
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!e.IsActionPressed("phone") || session.Client == null) return;
        Toggle();
        GetViewport().SetInputAsHandled();
    }

    private void Toggle()
    {
        _panel.Visible = !_panel.Visible;
        var input = GetParent().GetNodeOrNull<PlayerInput>("PlayerInput");
        if (input != null) input.UiOpen = _panel.Visible;
        Input.MouseMode = _panel.Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }

    public override void _Process(double delta)
    {
        if (!_panel.Visible) return;
        var st = session.State;
        _status.Text = $"현금 ${st.Wallet:N0}   은행 ${st.Bank:N0}   금고 ${st.Stash:N0}" + (st.WantedLevel > 0 ? $"   수배 {st.WantedLevel}" : "");
    }

    private ScrollContainer Page(string title, out VBoxContainer list)
    {
        var scroll = new ScrollContainer { Name = title, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(list);
        _tabs.AddChild(scroll);
        return scroll;
    }

    private static Button Btn(Control parent, string text, System.Action onPress)
    {
        var b = new Button { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = HorizontalAlignment.Left };
        b.Pressed += onPress;
        parent.AddChild(b);
        return b;
    }

    private static void Header(Control parent, string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", new Color(1f, 0.8f, 0.35f));
        parent.AddChild(l);
    }

    private void BuildContracts()
    {
        Page("의뢰", out var list);
        Btn(list, "진행 중인 의뢰 포기", () => session.Request(RequestType.ContractAbandon));
        foreach (var group in session.Data.Activities.Contracts.Values.OrderBy(c => c.Kind).ThenBy(c => c.MinPlayers).GroupBy(c => c.Kind))
        {
            Header(list, group.Key.ToString());
            foreach (var c in group)
            {
                var players = c.MinPlayers > 1 ? $" · {c.MinPlayers}인+" : "";
                var reward = c.Reward.Max > 0 ? $" · ${c.Reward.Min:N0}~{c.Reward.Max:N0}" : "";
                Btn(list, $"{c.DisplayName}{reward}{players}  [{c.District}]", () => session.Request(RequestType.ContractStart, c.Id));
            }
        }
    }

    private void BuildShop()
    {
        Page("상점", out var list);
        foreach (var item in session.Data.Economy.Shop.Values)
        {
            var name = item.Kind == ShopItemKind.Weapon && session.Data.Weapons.TryGetValue(item.Ref, out var w) ? w.DisplayName : item.Id;
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = $"{name}  ${item.Price:N0}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            foreach (var cur in item.Currencies)
            {
                var b = new Button { Text = cur == Currency.Dirty ? "현금" : "은행" };
                b.Pressed += () => session.Request(RequestType.Buy, item.Id, x: cur == Currency.Clean ? 1 : 0);
                row.AddChild(b);
            }
            list.AddChild(row);
        }
    }

    private void BuildMoney()
    {
        Page("세탁·금고", out var list);
        _amount = new SpinBox { MinValue = 100, MaxValue = 1_000_000, Step = 100, Value = 1000, Prefix = "$" };
        list.AddChild(_amount);
        long Amt() => (long)_amount.Value;
        Header(list, "즉시 세탁 (수수료 높음)");
        Btn(list, "현금 → 은행 즉시 세탁", () => session.Request(RequestType.LaunderInstant, x: Amt()));
        Header(list, "사업체 세탁 (느리지만 저렴)");
        foreach (var biz in session.Data.Economy.Businesses.Values)
        {
            Btn(list, $"{biz.DisplayName} 로 세탁 (수수료 {biz.FeeRate:P0})", () => session.Request(RequestType.LaunderAtBusiness, biz.Id, x: Amt()));
            Btn(list, $"   {biz.DisplayName} 인수 ${biz.Price:N0}", () => session.Request(RequestType.BuyBusiness, biz.Id));
        }
        Header(list, "은신처 금고 (사망 시 보존)");
        Btn(list, "금고에 넣기", () => session.Request(RequestType.StashDeposit, x: Amt()));
        Btn(list, "금고에서 꺼내기", () => session.Request(RequestType.StashWithdraw, x: Amt()));
        Btn(list, "패시브 모드 전환 (은신처 안)", () => session.Request(RequestType.TogglePassive));
    }

    private void BuildTravel()
    {
        Page("택시", out var list);
        Header(list, $"지역 간 이동 · 은행 ${session.Data.Travel.Cost:N0} · 수배 중 불가");
        foreach (var p in session.Data.Travel.Points)
            Btn(list, p.DisplayName, () => { session.Request(RequestType.FastTravel, p.Id); Toggle(); });
    }

    private void BuildCrew()
    {
        Page("크루", out var list);
        var name = new LineEdit { PlaceholderText = "크루 이름 (2~24자)" };
        list.AddChild(name);
        Btn(list, "크루 만들기", () => session.Request(RequestType.CrewCreate, name.Text));
        var id = new SpinBox { MinValue = 1, MaxValue = 9999, Prefix = "크루 #" };
        list.AddChild(id);
        Btn(list, "크루 가입", () => session.Request(RequestType.CrewJoin, x: (long)id.Value));
        Btn(list, "운반 중인 전리품 내려놓기 (G)", () => session.Request(RequestType.CarryDrop));
    }
}
