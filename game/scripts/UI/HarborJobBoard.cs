using System;
using Godot;
using Harbor.Game.Bridge;

namespace Harbor.Game.UI;

public partial class HarborJobBoard : CenterContainer
{
    private static readonly Color Paper = new("#f4f0e8");
    private static readonly Color Gray = new("#aac0c1");
    private Label _title = null!;
    private Label _brief = null!;
    private Label _stats = null!;
    private Button _track = null!;
    private string? _selected;

    public void Build(CoreHost host, string? tracked, Font? display, Font? detail, Action<string?> track, Action back)
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(760, 490) };
        panel.AddThemeStyleboxOverride("panel", HarborHud.Box(new Color("#11252ff4"), 10, 25));
        AddChild(panel);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 11);
        panel.AddChild(body);
        var header = new HBoxContainer();
        body.AddChild(header);
        var name = HarborHud.Text(header, "CITY JOBS", 39, Paper, display);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        HarborHud.Text(header, $"CREDITS  ${host.Jobs.Credits:N0}", 15, HarborHud.Seafoam, detail);
        HarborHud.Text(body, "Choose an offer to mark its start on the map.", 15, Gray, null);

        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 20);
        body.AddChild(columns);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(310, 310),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        columns.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);
        var summary = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(340, 0) };
        summary.AddThemeConstantOverride("separation", 12);
        columns.AddChild(summary);
        _title = HarborHud.Text(summary, "", 31, Paper, display);
        _brief = HarborHud.Text(summary, "", 17, Gray, null, true);
        _stats = HarborHud.Text(summary, "", 14, HarborHud.Seafoam, detail, true);
        _track = NewButton("Track on map", display, true);
        _track.Pressed += () => track(_selected);
        summary.AddChild(_track);

        Button? initiallySelected = null;
        foreach (var offer in host.Jobs.Available)
        {
            var button = NewButton(offer.Title, display);
            list.AddChild(button);
            var distance = (offer.StartPosition - host.RenderPosition).Length;
            var payout = $"{offer.Kind} · ${offer.Reward:N0} · {distance:0} m away";
            button.Pressed += () => Select(offer.Id, offer.Title, offer.Briefing, payout, offer.Id == tracked);
            if (offer.Id != tracked) continue;
            initiallySelected = button;
            Select(offer.Id, offer.Title, offer.Briefing, payout, true);
        }

        var lockdown = NewButton("Harbor Lockdown", display);
        list.AddChild(lockdown);
        var lockdownDistance = (host.Lockdown.StartPosition - host.RenderPosition).Length;
        void SelectLockdown() => Select(null, "Harbor Lockdown",
            "Clear the quay, hold for reinforcements, then reach the east gate for extraction.",
            $"Combat · {lockdownDistance:0} m away", tracked == null);
        lockdown.Pressed += SelectLockdown;
        if (initiallySelected == null)
        {
            SelectLockdown();
            initiallySelected = lockdown;
        }
        var returnButton = NewButton("Back", display);
        returnButton.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        returnButton.CustomMinimumSize = new Vector2(128, 43);
        returnButton.Pressed += back;
        body.AddChild(returnButton);
        initiallySelected.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void Select(string? id, string title, string briefing, string details, bool tracked)
    {
        _selected = id;
        _title.Text = title;
        _brief.Text = briefing;
        _stats.Text = details;
        _track.Text = tracked ? "Return to tracked job" : "Track on map";
    }

    internal static Button NewButton(string caption, Font? display, bool primary = false)
    {
        var button = new Button { Text = caption, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 44) };
        button.AddThemeFontSizeOverride("font_size", 20);
        if (display != null) button.AddThemeFontOverride("font", display);
        button.AddThemeColorOverride("font_color", primary ? HarborHud.Ink : Paper);
        button.AddThemeColorOverride("font_hover_color", HarborHud.Ink);
        button.AddThemeColorOverride("font_pressed_color", HarborHud.Ink);
        button.AddThemeColorOverride("font_focus_color", primary ? HarborHud.Ink : Paper);
        button.AddThemeStyleboxOverride("normal", HarborHud.Box(primary ? HarborHud.Amber : new Color("#28404a"), 5, 9));
        button.AddThemeStyleboxOverride("hover", HarborHud.Box(new Color("#ffc983"), 5, 9));
        button.AddThemeStyleboxOverride("pressed", HarborHud.Box(new Color("#e6a75f"), 5, 9));
        return button;
    }
}
