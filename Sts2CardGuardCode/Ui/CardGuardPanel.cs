using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Sts2CardGuard.Ui;

/// <summary>
/// Custom settings panel for the character-select screen. A "Card Guard" button is inserted
/// directly below the screen's checkbox (NTickbox); clicking it opens a full-screen panel:
///
///   top    — master toggles: Enable / Allow colorless / Block other mods' cards
///   left   — pick the character you are configuring FOR
///   right  — source-level allow list for that character: other base characters, then each
///            detected card-adding mod, as whole-source checkboxes
///
/// Settings persist to a JSON file via <see cref="CardGuardConfig"/> on every change. No
/// ModConfig dependency.
/// </summary>
public static class CardGuardPanel
{
    private const string ButtonName = "Sts2CardGuardButton";

    private static readonly Color GOLD = new(0.93f, 0.77f, 0.40f);
    private static readonly Color WHITE = new(0.93f, 0.94f, 0.97f);
    private static readonly Color GRAY = new(0.60f, 0.63f, 0.72f);

    private static Control? _root;
    private static VBoxContainer? _rightList;
    private static VBoxContainer? _leftList;
    private static string _selected = CardGuardService.CharacterTitles[0];

    // ── Character-select screen button ──────────────────────
    public static void Attach(Node screen) =>
        Callable.From(() => DoAttach(screen)).CallDeferred();

    private static void DoAttach(Node screen)
    {
        try
        {
            if (screen == null || !GodotObject.IsInstanceValid(screen)) return;
            if (FindDescendantByName(screen, ButtonName) != null) return; // already attached

            var host = screen as Control;

            var btn = new Button
            {
                Name = ButtonName,
                Text = Loc.T("button"),
                CustomMinimumSize = new Vector2(150, 52),
            };
            btn.AddThemeFontSizeOverride("font_size", 18);
            btn.Pressed += ShowPanel;

            if (host != null)
            {
                host.AddChild(btn);
                PlaceBottomRight(btn);
                // Reassert after layout in case the viewport size wasn't ready yet.
                var tree = screen.GetTree();
                if (tree != null) { var t = tree.CreateTimer(0.3); t.Timeout += () => PlaceBottomRight(btn); }
            }
            else
            {
                screen.AddChild(btn);
            }

            // Put the panel on its own high CanvasLayer so it draws above ALL other panels,
            // like a separate full-screen canvas.
            _root = BuildPanel();
            _root.Visible = false;
            var canvas = new CanvasLayer { Name = ButtonName + "Canvas", Layer = 128 };
            canvas.AddChild(_root);
            screen.AddChild(canvas);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] character-select button add failed: {ex.Message}");
        }
    }

    /// <summary>Pin the button to the bottom-right corner of the screen (fixed, viewport-relative).</summary>
    private static void PlaceBottomRight(Control btn)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(btn)) return;
            btn.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            var vp = btn.GetViewportRect().Size;
            var size = btn.Size.X > 1f ? btn.Size : btn.CustomMinimumSize;
            btn.Position = new Vector2(vp.X - size.X - 40f, vp.Y - size.Y - 40f);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] place failed: {ex.Message}");
        }
    }

    private static Node? FindDescendantByName(Node root, string name)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is not Node n) continue;
            if (n.Name == name) return n;
            var found = FindDescendantByName(n, name);
            if (found != null) return found;
        }
        return null;
    }

    private static void ShowPanel()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root)) return;
        _root.Visible = true;
        RebuildLeft();
        RebuildRight();
    }

    private static void Hide()
    {
        if (_root != null && GodotObject.IsInstanceValid(_root)) _root.Visible = false;
    }

    // ── Panel skeleton ──────────────────────────────────────
    private static Control BuildPanel()
    {
        var root = new Control { Name = "Sts2CardGuardPanel", MouseFilter = Control.MouseFilterEnum.Stop };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var panel = new PanelContainer();
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.OffsetLeft = 80; panel.OffsetTop = 56;
        panel.OffsetRight = -80; panel.OffsetBottom = -56;
        var sb = new StyleBoxFlat { BgColor = new Color(0.075f, 0.086f, 0.125f, 1.0f) };
        sb.SetBorderWidthAll(2);
        sb.BorderColor = new Color(0.30f, 0.34f, 0.46f, 1.0f);
        sb.SetCornerRadiusAll(10);
        panel.AddThemeStyleboxOverride("panel", sb);
        root.AddChild(panel);

        var margin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(s, 20);
        panel.AddChild(margin);

        var vbox = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        margin.AddChild(vbox);

        // Header: [Title ....] [X]
        var header = new HBoxContainer();
        var title = new Label { Text = Loc.T("title"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 26);
        title.AddThemeColorOverride("font_color", GOLD);
        header.AddChild(title);
        var x = new Button { Text = "X", CustomMinimumSize = new Vector2(40, 40) };
        x.AddThemeFontSizeOverride("font_size", 20);
        x.Pressed += Hide;
        header.AddChild(x);
        vbox.AddChild(header);

        var hint = new Label
        {
            Text = Loc.T("hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 14);
        hint.AddThemeColorOverride("font_color", GRAY);
        vbox.AddChild(hint);

        vbox.AddChild(new HSeparator());

        // Body: left character list | right allow list.
        var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 16);
        vbox.AddChild(body);

        _leftList = new VBoxContainer { CustomMinimumSize = new Vector2(240, 0) };
        var leftLbl = new Label { Text = Loc.T("configure") };
        leftLbl.AddThemeFontSizeOverride("font_size", 16);
        leftLbl.AddThemeColorOverride("font_color", GRAY);
        _leftList.AddChild(leftLbl);
        body.AddChild(_leftList);

        body.AddChild(new VSeparator());

        var rightScroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _rightList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rightScroll.AddChild(_rightList);
        body.AddChild(rightScroll);

        return root;
    }

    // ── Left: character selector ────────────────────────────
    private static void RebuildLeft()
    {
        if (_leftList == null) return;
        // Keep the header (index 0), drop the rest.
        for (int i = _leftList.GetChildCount() - 1; i >= 1; i--)
            _leftList.GetChild(i).QueueFree();

        foreach (var ci in CardGuardService.GetAllCharacters())
        {
            string t = ci.Title;
            bool sel = string.Equals(t, _selected, StringComparison.OrdinalIgnoreCase);
            var b = new Button
            {
                Text = (sel ? "▸ " : "   ") + ci.Display,
                Alignment = HorizontalAlignment.Left,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 38),
            };
            b.AddThemeFontSizeOverride("font_size", 18);
            b.AddThemeColorOverride("font_color", sel ? GOLD : WHITE);
            b.Pressed += () => { _selected = t; RebuildLeft(); RebuildRight(); };
            _leftList.AddChild(b);
        }
    }

    // ── Right: source allow list for the selected character ──
    private static void RebuildRight()
    {
        if (_rightList == null) return;
        foreach (var c in _rightList.GetChildren()) c.QueueFree();

        _rightList.AddChild(SectionLabel(Loc.T("sec_char")));

        // The selected character's own pack is always applied — checked and locked so applied
        // packs are visibly ticked.
        var ownRow = MakeCheck(
            $"{Display(_selected)}  —  {CountOf(_selected)} " + Loc.T("cards")
            + Loc.T("own_suffix"),
            true, _ => { });
        ownRow.Disabled = true;
        ownRow.SetPressedNoSignal(true);
        _rightList.AddChild(ownRow);

        foreach (var ci in CardGuardService.GetAllCharacters())
        {
            if (string.Equals(ci.Title, _selected, StringComparison.OrdinalIgnoreCase)) continue;
            string to = ci.Title, from = _selected;
            _rightList.AddChild(MakeCheck(
                $"{ci.Display}  —  {ci.Count} " + Loc.T("cards"),
                CardGuardService.GetCrossAllowed(from, to),
                v => { CardGuardService.SetCrossAllowed(from, to, v); CardGuardConfig.Save(); }));
        }

        _rightList.AddChild(SectionLabel(Loc.T("sec_mod")));
        // Custom-character mods are shown in the character section above, not here.
        var mods = CardGuardService.GetModCardPacks()
            .Where(kv => !CardGuardService.IsCharacterMod(kv.Key))
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mods.Count == 0)
        {
            var none = new Label { Text = Loc.T("no_mods") };
            none.AddThemeColorOverride("font_color", GRAY);
            _rightList.AddChild(none);
        }
        else
        {
            foreach (var kv in mods)
            {
                string m = kv.Key, from = _selected;
                var mp = kv.Value;
                string colorless = mp.Colorless > 0
                    ? string.Format(Loc.T("mod_colorless_fmt"), mp.Colorless)
                    : "";
                string lbl = $"{m}  —  {mp.Total} " + Loc.T("cards") + colorless;
                _rightList.AddChild(MakeCheck(
                    lbl,
                    CardGuardService.GetModAllowed(from, m),
                    v => { CardGuardService.SetModAllowed(from, m, v); CardGuardConfig.Save(); }));
            }
        }
    }

    private static string Display(string title)
    {
        foreach (var c in CardGuardService.GetAllCharacters())
            if (string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase)) return c.Display;
        return title;
    }

    private static int CountOf(string title)
    {
        foreach (var c in CardGuardService.GetAllCharacters())
            if (string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase)) return c.Count;
        return 0;
    }

    // ── Small widget helpers ────────────────────────────────
    private static Label SectionLabel(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 15);
        l.AddThemeColorOverride("font_color", GOLD);
        return l;
    }

    private static CheckBox MakeCheck(string text, bool initial, Action<bool> onChanged)
    {
        var cb = new CheckBox { Text = text, CustomMinimumSize = new Vector2(0, 34) };
        cb.SetPressedNoSignal(initial);
        cb.AddThemeFontSizeOverride("font_size", 16);
        cb.Toggled += v => onChanged(v);
        return cb;
    }
}
