using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

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

    // Tabs: 0 = card packs, 1 = relic packs. Both are per character and share the left character
    // column; only the right-hand allow list differs, so the tab just re-renders the right pane.
    private static int _activeTab;
    private static Label? _hint;
    private static Button? _tabCardsBtn;
    private static Button? _tabRelicsBtn;

    // ── Detail (per-item) drill-down ────────────────────────
    // The right pane has two modes: the pack list, and — after pressing a pack's Detail button —
    // that pack's individual cards/relics. Character packs list every card they hold, base-game
    // included; relic packs stay mod-only (the game has no per-character base relic pool to list).
    private enum Drill { None, ModCards, CharacterCards, ModRelics }
    private static Drill _drill = Drill.None;
    /// <summary>Mod assembly name (ModCards / ModRelics) or card-pool title (CharacterCards).</summary>
    private static string _drillKey = "";
    private static string _drillLabel = "";
    private static string _search = "";
    /// <summary>One-shot message shown above the right pane (currently only the refused-block
    /// notice). Cleared as soon as it has been drawn, so it never lingers into the next view.</summary>
    private static string _notice = "";
    private static VBoxContainer? _drillItems;
    private static Label? _drillCountLbl;
    /// <summary>Item list for the open drill target. Names come from the loc layer, so resolving
    /// them per keystroke would be wasteful — the set only changes when the target does.</summary>
    private static List<DrillItem>? _drillAll;

    /// <summary>One row of a detail list. <paramref name="Sub"/> is the rarity, shown after the name.
    /// The model is carried so the row can render its relic icon / preview its card art.</summary>
    private readonly record struct DrillItem(string Id, string Name, string Sub, CardModel? Card, RelicModel? Relic);

    // ── Hover preview (cards) ───────────────────────────────
    /// <summary>Overlay the preview is parented to. Positioned at the intended CENTER of the preview:
    /// an NCard's local origin is its middle, not its top-left (see reference_ncard_coordinate).</summary>
    private static Control? _previewHost;
    /// <summary>The pooled card node currently on show. MUST be unparented and returned via
    /// <see cref="NodePool.Free{T}"/> — leaking these drains the 30-node NCard pool the game uses
    /// for real combat cards.</summary>
    private static NCard? _previewCard;

    /// <summary>The character-select screen this panel is attached to (for reading the current selection).</summary>
    private static Node? _screen;

    // ── Character-select screen button ──────────────────────
    public static void Attach(Node screen) =>
        Callable.From(() => DoAttach(screen)).CallDeferred();

    private static void DoAttach(Node screen)
    {
        try
        {
            if (screen == null || !GodotObject.IsInstanceValid(screen)) return;
            _screen = screen;
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
        SyncSelectedToScreen();
        _root.Visible = true;
        SwitchTab(_activeTab);
    }

    /// <summary>Activate a tab (0 = cards, 1 = relics) and rebuild both columns for it.</summary>
    private static void SwitchTab(int tab)
    {
        _activeTab = tab;
        LeaveDrill(); // a drill target belongs to one tab; switching always lands on the pack list
        bool cards = tab == 0;

        if (_hint != null) _hint.Text = Loc.T(cards ? "hint" : "relic_hint");
        if (_tabCardsBtn != null) _tabCardsBtn.AddThemeColorOverride("font_color", cards ? GOLD : GRAY);
        if (_tabRelicsBtn != null) _tabRelicsBtn.AddThemeColorOverride("font_color", cards ? GRAY : GOLD);

        RebuildLeft();
        RebuildRight();
    }

    /// <summary>
    /// Default the "configure FOR" character to whichever character is currently highlighted on the
    /// character-select screen, so opening the panel lands on the class you're about to play. Falls
    /// back to the last selection if the screen's pick can't be resolved (e.g. the Random button).
    /// </summary>
    private static void SyncSelectedToScreen()
    {
        try
        {
            if (_screen == null || !GodotObject.IsInstanceValid(_screen)) return;
            if (IsAllScope) return; // deliberately picked "all characters" — don't drag it back to one class
            var f = _screen.GetType().GetField("_selectedButton", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(_screen) is not NCharacterSelectButton btn || !GodotObject.IsInstanceValid(btn)) return;
            if (btn.IsRandom) return; // Random has no fixed pool to configure — keep prior selection
            string? title = btn.Character?.CardPool?.Title;
            if (string.IsNullOrEmpty(title)) return;
            // Only adopt it if it's a character the panel actually lists (a real colored pool).
            foreach (var ci in CardGuardService.GetAllCharacters())
                if (string.Equals(ci.Title, title, StringComparison.OrdinalIgnoreCase)) { _selected = ci.Title; return; }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] sync selected character failed: {ex.Message}");
        }
    }

    private static void Hide()
    {
        ClearPreview();
        if (_root != null && GodotObject.IsInstanceValid(_root)) _root.Visible = false;
    }

#if DEBUG
    // ── solo-verify hooks ───────────────────────────────────
    // Debug-only, matching the SoloTest/CoopTest exclusion in the csproj: the Workshop DLL ships
    // without any of this. Nothing in gameplay calls them — Attach() is the only production entry.

    /// <summary>
    /// solo-verify hook. Builds and shows the panel over an arbitrary host node so a self-test can
    /// screenshot it outside the character-select screen, optionally already drilled into a pack.
    /// </summary>
    internal static void TestOpen(Node host, int tab, string? drillKey, bool relics) =>
        Callable.From(() =>
        {
            try
            {
                DoAttach(host); // no-ops if already attached; leaves _root built either way
                if (_root == null || !GodotObject.IsInstanceValid(_root)) return;
                _root.Visible = true;
                SwitchTab(tab);
                if (!string.IsNullOrEmpty(drillKey))
                    EnterDrill(relics ? Drill.ModRelics : Drill.ModCards, drillKey!, drillKey!);
            }
            catch (Exception ex) { MainFile.Logger.Warn($"[{MainFile.ModId}] TestOpen failed: {ex.Message}"); }
        }).CallDeferred();

    /// <summary>solo-verify hook — hide the panel again after screenshots.</summary>
    internal static void TestClose() => Callable.From(Hide).CallDeferred();

    /// <summary>solo-verify hook — the character the panel is currently configuring FOR.</summary>
    internal static void TestSelect(string title) { if (!string.IsNullOrEmpty(title)) _selected = title; }

    /// <summary>solo-verify hook — simulate hovering the first row of the open card detail list.
    /// Runs synchronously (unlike <see cref="TestOpen"/>) so the caller's state reads on the next line
    /// observe the result rather than racing a deferred call.</summary>
    internal static void TestHoverFirstCard()
    {
        try
        {
            if (_drillItems == null || !GodotObject.IsInstanceValid(_drillItems))
            { MainFile.Logger.Info($"[{MainFile.ModId}] TestHoverFirstCard: no drill list"); return; }
            var items = VisibleDrillItems();
            if (items.Count == 0 || items[0].Card == null)
            { MainFile.Logger.Info($"[{MainFile.ModId}] TestHoverFirstCard: no card rows ({items.Count} item(s))"); return; }
            Control? anchor = null;
            foreach (var ch in _drillItems.GetChildren())
                if (ch is CheckBox c) { anchor = c; break; }
            if (anchor == null) { MainFile.Logger.Info($"[{MainFile.ModId}] TestHoverFirstCard: no CheckBox anchor"); return; }
            ShowCardPreview(items[0].Card!, anchor);
            MainFile.Logger.Info($"[{MainFile.ModId}] TestHoverFirstCard: previewed '{items[0].Id}', pooledNCard={_previewCard != null}");
        }
        catch (Exception ex) { MainFile.Logger.Warn($"[{MainFile.ModId}] TestHoverFirstCard failed: {ex}"); }
    }

    // solo-verify hooks for the pack↔item linkage. kind: 1 = character cards, 2 = mod cards, 3 = mod relics.
    private static Drill TestKind(int kind) => kind switch
    {
        1 => Drill.CharacterCards,
        2 => Drill.ModCards,
        _ => Drill.ModRelics,
    };

    internal static List<string> TestPackItemIds(int kind, string key) => PackItemIds(TestKind(kind), key);

    internal static (int total, int allowed, bool blocked, bool partial) TestPackState(int kind, string key)
    {
        var s = PackStateOf(TestKind(kind), key);
        return (s.Total, s.Allowed, s.Blocked, s.Partial);
    }

    /// <summary>Exactly what clicking the pack checkbox does.</summary>
    internal static void TestPackToggle(int kind, string key, bool allowed) =>
        ApplyPackToggle(TestKind(kind), key, allowed);

    /// <summary>Exactly what ticking one row of the detail list does.</summary>
    internal static void TestItemToggle(int kind, string key, string id, bool allowed)
    {
        var d = TestKind(kind);
        SetItemAllowed(d, id, allowed);
        ReconcilePack(d, key);
    }

    /// <summary>solo-verify hook — preview an arbitrary card (control case for diagnosing renders).</summary>
    internal static void TestHoverCard(CardModel card)
    {
        try
        {
            if (_drillItems == null || !GodotObject.IsInstanceValid(_drillItems)) return;
            Control? anchor = null;
            foreach (var ch in _drillItems.GetChildren())
                if (ch is CheckBox c) { anchor = c; break; }
            if (anchor == null) return;
            ShowCardPreview(card, anchor);
        }
        catch (Exception ex) { MainFile.Logger.Warn($"[{MainFile.ModId}] TestHoverCard failed: {ex}"); }
    }

    /// <summary>solo-verify hook — end the hover (the MouseExited path). Synchronous, see above.</summary>
    internal static void TestUnhover()
    {
        ClearPreview();
        MainFile.Logger.Info($"[{MainFile.ModId}] TestUnhover: cleared (previewCard={_previewCard != null}, "
                             + $"hostVisible={(_previewHost != null && GodotObject.IsInstanceValid(_previewHost) ? _previewHost.Visible.ToString() : "n/a")})");
    }

    /// <summary>solo-verify hook — free-node count of the game's NCard pool, so a test can prove the
    /// preview borrows and RETURNS a pooled card rather than leaking one per hover.</summary>
    internal static int TestCardPoolFree()
    {
        try
        {
            var f = typeof(NodePool).GetField("_pools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (f?.GetValue(null) is not System.Collections.IDictionary pools) return -1;
            var pool = pools[typeof(NCard)];
            if (pool == null) return -1;
            var list = pool.GetType().GetProperty("DebugFreeObjects")?.GetValue(pool) as System.Collections.ICollection;
            return list?.Count ?? -1;
        }
        catch { return -1; }
    }

    /// <summary>solo-verify hook — is a preview currently mounted, and is it a real pooled NCard?</summary>
    internal static (bool shown, bool isRealCard) TestPreviewState()
    {
        bool shown = _previewHost != null && GodotObject.IsInstanceValid(_previewHost)
                     && _previewHost.Visible && _previewHost.GetChildCount() > 0;
        return (shown, _previewCard != null);
    }
#endif

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

        // Tab bar: Cards | Relics.
        var tabBar = new HBoxContainer();
        tabBar.AddThemeConstantOverride("separation", 8);
        _tabCardsBtn = MakeTab(Loc.T("tab_cards"), 0);
        _tabRelicsBtn = MakeTab(Loc.T("tab_relics"), 1);
        tabBar.AddChild(_tabCardsBtn);
        tabBar.AddChild(_tabRelicsBtn);
        vbox.AddChild(tabBar);

        _hint = new Label
        {
            Text = Loc.T("hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _hint.AddThemeFontSizeOverride("font_size", 14);
        _hint.AddThemeColorOverride("font_color", GRAY);
        vbox.AddChild(_hint);

        vbox.AddChild(new HSeparator());

        // Body: left character selector | right allow list (cards or relics, per the active tab).
        var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 16);
        vbox.AddChild(body);

        // The character column scrolls: with enough character mods installed the list is taller than
        // the panel, and an unscrolled VBox reports that height as its minimum size — which makes the
        // whole panel grow past the bottom of the screen and swallow its own controls.
        var leftCol = new VBoxContainer { CustomMinimumSize = new Vector2(240, 0) };
        var leftLbl = new Label { Text = Loc.T("configure") };
        leftLbl.AddThemeFontSizeOverride("font_size", 16);
        leftLbl.AddThemeColorOverride("font_color", GRAY);
        leftCol.AddChild(leftLbl);

        var leftScroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _leftList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        leftScroll.AddChild(_leftList);
        leftCol.AddChild(leftScroll);
        body.AddChild(leftCol);

        body.AddChild(new VSeparator());

        var rightScroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        // Reserve room for the vertical scrollbar. Without it the rightmost content — the Detail
        // button on a pack row, the blocked-count label in a detail header — is laid out underneath
        // the bar and clipped once the list is long enough to scroll.
        var rightPad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rightPad.AddThemeConstantOverride("margin_right", 18);
        _rightList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rightPad.AddChild(_rightList);
        rightScroll.AddChild(rightPad);
        body.AddChild(rightScroll);

        // Hover preview overlay — last child so it draws above the panel, and mouse-transparent so it
        // can't swallow the hover that spawned it (which would flicker it on and off).
        _previewHost = new Control
        {
            Name = "Sts2CardGuardPreview",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        root.AddChild(_previewHost);

        return root;
    }

    /// <summary>A tab-bar button that activates <paramref name="tab"/>.</summary>
    private static Button MakeTab(string text, int tab)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(120, 40) };
        b.AddThemeFontSizeOverride("font_size", 18);
        b.Pressed += () => SwitchTab(tab);
        return b;
    }

    // ── Left: character selector ────────────────────────────
    private static void RebuildLeft()
    {
        if (_leftList == null) return;
        for (int i = _leftList.GetChildCount() - 1; i >= 0; i--)
            _leftList.GetChild(i).QueueFree();

        // The all-characters scope sits first: it is the one people reach for when they mean "never
        // show me this card again", and a per-character list with no such row invites configuring
        // one character and then playing another.
        var entries = new List<(string Title, string Display)> { (CardGuardService.AllScope, Loc.T("all_chars")) };
        foreach (var ci in CardGuardService.GetAllCharacters()) entries.Add((ci.Title, ci.Display));

        foreach (var (t, disp) in entries)
        {
            bool sel = string.Equals(t, _selected, StringComparison.OrdinalIgnoreCase);
            var b = new Button
            {
                Text = (sel ? "▸ " : "   ") + disp,
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

    /// <summary>Draws and consumes <see cref="_notice"/>. One-shot: the next redraw clears it, so a
    /// refused block explains itself once instead of sticking to the pane.</summary>
    private static void DrawNotice()
    {
        if (_rightList == null || _notice.Length == 0) return;
        var l = new Label { Text = _notice, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", 14);
        l.AddThemeColorOverride("font_color", GOLD);
        _rightList.AddChild(l);
        _notice = "";
    }

    // ── Right: allow list for the selected character (cards or relics, per active tab) ──
    private static void RebuildRight()
    {
        if (_rightList == null) return;
        ClearPreview(); // the hovered row is about to be freed
        foreach (var c in _rightList.GetChildren()) c.QueueFree();
        _drillItems = null;
        _drillCountLbl = null;

        DrawNotice();

        if (_drill != Drill.None) { RebuildDrill(); return; }
        if (_activeTab == 1) { RebuildRelicsRight(); return; }

        _rightList.AddChild(SectionLabel(Loc.T("sec_char")));

        // The selected character's own pack is always applied — checked and locked so applied
        // packs are visibly ticked. The FLAG is what's locked, not the contents: the detail list
        // still prunes your own class card by card, base-game cards included.
        // The all-characters scope has no "own" pack: from there every character pack is a normal
        // blockable row (blocking one there blocks it for everyone), so this row is skipped.
        if (!IsAllScope)
        {
            var ownState = PackStateOf(Drill.CharacterCards, _selected);
            var ownCb = MakeCheck(
                $"{Display(_selected)}  —  {CountOf(_selected)} " + Loc.T("cards")
                + Loc.T("own_suffix") + CountsSuffix(ownState),
                true, _ => { });
            ownCb.Disabled = true;
            ownCb.SetPressedNoSignal(true);
            _rightList.AddChild(RowWithDetail(ownCb, ownState.Total > 0
                ? () => EnterDrill(Drill.CharacterCards, _selected, Display(_selected))
                : null));
        }

        foreach (var ci in CardGuardService.GetAllCharacters())
        {
            if (string.Equals(ci.Title, _selected, StringComparison.OrdinalIgnoreCase)) continue;
            string to = ci.Title, disp = ci.Display;
            var st = PackStateOf(Drill.CharacterCards, to);
            var cb = MakePackCheck($"{disp}  —  {ci.Count} " + Loc.T("cards"), Drill.CharacterCards, to, st);
            // Every character pack is drillable now that base cards are individually blockable; the
            // guard only skips a pool that reported no cards at all.
            _rightList.AddChild(RowWithDetail(cb, st.Total > 0
                ? () => EnterDrill(Drill.CharacterCards, to, disp)
                : null));
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
                string m = kv.Key;
                var mp = kv.Value;
                // Was string.Format(Loc.T(...)) — SmartFormat corrupts a lone {0}; see Loc.F.
                string colorless = mp.Colorless > 0 ? Loc.F("mod_colorless_fmt", mp.Colorless) : "";
                var st = PackStateOf(Drill.ModCards, m);
                var cb = MakePackCheck($"{m}  —  {mp.Total} " + Loc.T("cards") + colorless, Drill.ModCards, m, st);
                _rightList.AddChild(RowWithDetail(cb, () => EnterDrill(Drill.ModCards, m, m)));
            }
        }
    }

    // ── Relics tab: per-character relic-mod allow list for the selected character ──
    private static void RebuildRelicsRight()
    {
        if (_rightList == null) return;

        _rightList.AddChild(SectionLabel(Loc.T("relic_sec")));

        var mods = RelicGuardService.GetModRelicPacks()
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mods.Count == 0)
        {
            var none = new Label { Text = Loc.T("no_relic_mods") };
            none.AddThemeColorOverride("font_color", GRAY);
            _rightList.AddChild(none);
            return;
        }

        foreach (var kv in mods)
        {
            string m = kv.Key;
            var st = PackStateOf(Drill.ModRelics, m);
            var cb = MakePackCheck($"{m}  —  {kv.Value.Total} " + Loc.T("relics"), Drill.ModRelics, m, st);
            _rightList.AddChild(RowWithDetail(cb, () => EnterDrill(Drill.ModRelics, m, m)));
        }
    }

    // ── Detail list: individual cards / relics of one mod pack ──────────────
    private static void EnterDrill(Drill kind, string key, string label)
    {
        _drill = kind;
        _drillKey = key;
        _drillLabel = label;
        _search = "";
        _drillAll = null;
        RebuildRight();
    }

    private static void LeaveDrill()
    {
        _drill = Drill.None;
        _drillKey = "";
        _drillLabel = "";
        _search = "";
        _drillAll = null;
    }

    private static void RebuildDrill()
    {
        if (_rightList == null) return;

        // Header: back to the pack list, which pack, and how much of it is blocked.
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        var back = new Button { Text = Loc.T("back"), CustomMinimumSize = new Vector2(100, 34) };
        back.AddThemeFontSizeOverride("font_size", 15);
        back.Pressed += () => { LeaveDrill(); RebuildRight(); };
        head.AddChild(back);

        var name = new Label
        {
            Text = _drillLabel,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        name.AddThemeFontSizeOverride("font_size", 17);
        name.AddThemeColorOverride("font_color", GOLD);
        head.AddChild(name);

        _drillCountLbl = new Label { VerticalAlignment = VerticalAlignment.Center };
        _drillCountLbl.AddThemeFontSizeOverride("font_size", 14);
        _drillCountLbl.AddThemeColorOverride("font_color", GRAY);
        head.AddChild(_drillCountLbl);
        _rightList.AddChild(head);

        // Only meaningful for a config written before pack/item linkage existed, where a blocked pack
        // could still hold ticked items. Under the current invariant a blocked pack has none, and
        // showing the note there would just be noise.
        if (!PackCurrentlyAllowed() && AllDrillItems().Any(i => DrillAllowed(i.Id)))
        {
            var warn = new Label { Text = Loc.T("pack_off_note"), AutowrapMode = TextServer.AutowrapMode.WordSmart };
            warn.AddThemeFontSizeOverride("font_size", 13);
            warn.AddThemeColorOverride("font_color", GRAY);
            _rightList.AddChild(warn);
        }

        // Search + bulk actions. Bulk applies to what the search currently shows.
        var tools = new HBoxContainer();
        tools.AddThemeConstantOverride("separation", 8);
        var search = new LineEdit
        {
            PlaceholderText = Loc.T("search"),
            Text = _search,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 34),
        };
        search.TextChanged += t => { _search = t ?? string.Empty; FillDrillItems(); };
        tools.AddChild(search);

        var allOn = new Button { Text = Loc.T("all_on"), CustomMinimumSize = new Vector2(110, 34) };
        allOn.AddThemeFontSizeOverride("font_size", 14);
        allOn.Pressed += () => SetAllVisible(true);
        tools.AddChild(allOn);

        var allOff = new Button { Text = Loc.T("all_off"), CustomMinimumSize = new Vector2(110, 34) };
        allOff.AddThemeFontSizeOverride("font_size", 14);
        allOff.Pressed += () => SetAllVisible(false);
        tools.AddChild(allOff);
        _rightList.AddChild(tools);

        _rightList.AddChild(new HSeparator());

        // Only this sub-container is refilled while typing, so the search box keeps focus.
        _drillItems = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _rightList.AddChild(_drillItems);
        FillDrillItems();
    }

    private static void FillDrillItems()
    {
        if (_drillItems == null || !GodotObject.IsInstanceValid(_drillItems)) return;
        ClearPreview(); // rows (and their MouseExited handlers) are about to go away
        // Detach before freeing: QueueFree only takes effect at end of frame, so leaving the old
        // rows parented would lay them out alongside the ones added right below.
        foreach (var c in _drillItems.GetChildren())
        {
            _drillItems.RemoveChild(c);
            c.QueueFree();
        }

        UpdateDrillCount();

        var items = VisibleDrillItems();
        if (items.Count == 0)
        {
            var none = new Label { Text = Loc.T("no_items") };
            none.AddThemeColorOverride("font_color", GRAY);
            _drillItems.AddChild(none);
            return;
        }

        foreach (var it in items)
        {
            string id = it.Id;
            string label = it.Sub.Length > 0 ? $"{it.Name}  —  {it.Sub}" : it.Name;

            // Blocked for every character: show it blocked and lock the row. Leaving it editable
            // would offer a tick that cannot bring the card back — the global block still holds.
            bool lockedByAll = ItemBlockedGlobally(_drill, id);
            if (lockedByAll) label += Loc.T("all_mark");

            var cb = MakeCheck(label, !lockedByAll && DrillAllowed(id), v =>
            {
                var before = Snapshot(_drill, _drillKey);
                SetDrillAllowed(id, v);
                ReconcilePack(_drill, _drillKey); // keep the pack flag in step with its items
                if (!KeepIfAnyCardRemains(before))
                {
                    // The row still shows the click that was refused — redraw it from the restored
                    // state. Deferred: we are inside the checkbox's own Toggled signal.
                    Callable.From(RebuildRight).CallDeferred();
                    return;
                }
                CardGuardConfig.Save();
                UpdateDrillCount();
            });
            if (lockedByAll) cb.Disabled = true;

            Control row = cb;
            if (it.Relic != null)
            {
                // Relics read as icons far more than as names — show it inline on every row.
                var icon = RelicIcon(it.Relic);
                if (icon != null)
                {
                    var hb = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    hb.AddThemeConstantOverride("separation", 8);
                    hb.AddChild(icon);
                    cb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    hb.AddChild(cb);
                    row = hb;
                }
            }
            else if (it.Card != null)
            {
                // A card is only identifiable from its art + text, and 200 inline card renders would
                // be absurd — so it previews on hover instead.
                var card = it.Card;
                cb.MouseEntered += () => ShowCardPreview(card, cb);
                cb.MouseExited += ClearPreview;
            }

            _drillItems.AddChild(row);
        }
    }

    /// <summary>Allow/block every item the current search shows, then redraw the list.</summary>
    private static void SetAllVisible(bool allowed)
    {
        var before = Snapshot(_drill, _drillKey);
        // Rows locked by the all-characters scope are skipped: they are drawn disabled, and writing
        // a per-character setting under them would change nothing while looking like it did.
        foreach (var it in VisibleDrillItems())
            if (!ItemBlockedGlobally(_drill, it.Id)) SetDrillAllowed(it.Id, allowed);
        ReconcilePack(_drill, _drillKey);
        if (KeepIfAnyCardRemains(before)) CardGuardConfig.Save();
        // Whole-pane redraw, because the notice sits above the list rather than inside it — and
        // deferred, since this runs from the Allow all / Block all button's own Pressed signal and
        // the rebuild frees that button.
        Callable.From(RebuildRight).CallDeferred();
    }

    private static void UpdateDrillCount()
    {
        if (_drillCountLbl == null || !GodotObject.IsInstanceValid(_drillCountLbl)) return;
        var all = AllDrillItems();
        int allowed = all.Count(i => DrillAllowed(i.Id));
        _drillCountLbl.Text = Loc.F("counts_fmt", allowed, all.Count - allowed).TrimStart();
    }

    private static List<DrillItem> VisibleDrillItems()
    {
        var all = AllDrillItems();
        if (string.IsNullOrWhiteSpace(_search)) return all;
        string q = _search.Trim();
        return all.Where(i => i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                              || i.Id.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static List<DrillItem> AllDrillItems()
    {
        if (_drillAll != null) return _drillAll;
        var list = new List<DrillItem>();
        try
        {
            switch (_drill)
            {
                case Drill.ModRelics:
                    if (RelicGuardService.GetModRelicPacks().TryGetValue(_drillKey, out var rp))
                        foreach (var r in rp.Relics)
                        {
                            if (!IsListableRelic(r)) continue;
                            list.Add(new DrillItem(RelicGuardService.RelicIdOf(r), RelicName(r), RarityOf(r), null, r));
                        }
                    break;
                case Drill.ModCards:
                    if (CardGuardService.GetModCardPacks().TryGetValue(_drillKey, out var mp))
                        foreach (var c in mp.Cards)
                            list.Add(new DrillItem(CardGuardService.CardIdOf(c), CardName(c), RarityOf(c), c, null));
                    break;
                case Drill.CharacterCards:
                    foreach (var c in CardGuardService.GetPoolAllCards(_drillKey))
                        list.Add(new DrillItem(CardGuardService.CardIdOf(c), CardName(c), RarityOf(c), c, null));
                    break;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] detail list build failed: {ex.Message}");
        }
        list.RemoveAll(i => string.IsNullOrEmpty(i.Id));
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        _drillAll = list;
        return list;
    }

    /// <summary>Whether the drilled item can still appear for the selected scope — its own setting
    /// AND the all-characters one, so the header count matches what the rows show.</summary>
    private static bool DrillAllowed(string id) =>
        !ItemBlockedGlobally(_drill, id)
        && (_drill == Drill.ModRelics
            ? RelicGuardService.GetRelicAllowed(_selected, id)
            : CardGuardService.GetCardAllowed(_selected, id));

    private static void SetDrillAllowed(string id, bool allowed)
    {
        if (_drill == Drill.ModRelics) RelicGuardService.SetRelicAllowed(_selected, id, allowed);
        else CardGuardService.SetCardAllowed(_selected, id, allowed);
    }

    /// <summary>Whether the pack being drilled into is itself still allowed for this scope.</summary>
    private static bool PackCurrentlyAllowed() => !PackBlockedGlobally(_drill, _drillKey) && _drill switch
    {
        Drill.ModRelics => RelicGuardService.GetModAllowed(_selected, _drillKey),
        Drill.ModCards => CardGuardService.GetModAllowed(_selected, _drillKey),
        // Your own pack is never cross-blocked; another character's is.
        Drill.CharacterCards => IsOwnPack(Drill.CharacterCards, _drillKey)
                                || CardGuardService.GetCrossAllowed(_selected, _drillKey),
        _ => true,
    };

    // ── Relic icon / card hover preview ─────────────────────

    /// <summary>Small inline icon for a relic row, or null if the texture won't load.</summary>
    private static TextureRect? RelicIcon(RelicModel relic)
    {
        try
        {
            var tex = relic.Icon;
            if (tex == null) return null;
            // ★ExpandMode must be assigned BEFORE any size: setting Size first clamps the rect to the
            // texture's native size and the icon overflows the row (the Sts2SlotMachine lesson).
            var tr = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Texture = tex,
            };
            tr.CustomMinimumSize = new Vector2(30, 30);
            return tr;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] relic icon load failed ({relic?.Id.Entry}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Show <paramref name="card"/> next to the hovered row. Uses the game's own pooled NCard so the
    /// preview carries the real frame/banner/art (including whatever other mods reskin). Falls back to
    /// the bare portrait if the pool is unavailable (e.g. TestMode.IsOn makes Create return null).
    /// </summary>
    private static void ShowCardPreview(CardModel card, Control anchor)
    {
        ClearPreview();
        if (_previewHost == null || !GodotObject.IsInstanceValid(_previewHost)) return;
        if (_root == null || !_root.Visible) return;

        Control? content = null;
        try
        {
            // ★Parent BEFORE assigning Model. NCard.Create() sets Model immediately, and the setter's
            // Reload() then runs on a node that has never been in the tree — pooled NCards are
            // instantiated at boot and parked — so _Ready hasn't wired the scene up and Reload paints
            // nothing, leaving card.tscn's baked placeholder ("Broken Card / If you can read this,
            // there is a bug") on screen. Entering the tree first makes Reload paint the real card.
            var node = NodePool.Get<NCard>();
            _previewCard = node;
            node.MouseFilter = Control.MouseFilterEnum.Ignore; // never steal the hover that spawned it
            _previewHost.AddChild(node);
            node.Visibility = ModelVisibility.Visible;         // pooled node may carry a stale value
            // The pool's canonical ModelDb instance is a template, not a displayable card — hand NCard
            // a mutable instance, the same thing rewards/decks render.
            CardModel display = card;
            try { display = card.ToMutable(); } catch { }
            node.Model = display;
            // ★Assigning Model only runs Reload(), which paints the IMAGES (portrait, frame, banner,
            // type plaque). Title, cost and description live in UpdateVisuals() and are not called
            // from anywhere in the Model setter — without this the card keeps card.tscn's baked
            // placeholder ("Broken Card / If you can read this, there is a bug") over correct art,
            // which looks like bad card data rather than a missing call.
            node.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
            content = node;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] NCard preview failed ({card?.Id.Entry}): {ex.Message}");
            ReleasePooledCard();
        }

        Vector2 size;
        if (content != null)
        {
            content.Scale = new Vector2(PreviewScale, PreviewScale);
            size = SafeCardSize(_previewCard!) * PreviewScale;
        }
        else
        {
            content = BuildPortraitFallback(card, out size);
            if (content == null) return;
            content.MouseFilter = Control.MouseFilterEnum.Ignore;
            _previewHost.AddChild(content);
        }

        _previewHost.Position = PreviewCenter(anchor, size);
        _previewHost.Visible = true;
    }

    private const float PreviewScale = 0.8f;

    private static Vector2 SafeCardSize(NCard node)
    {
        try { var s = node.GetCurrentSize(); if (s.X > 1f && s.Y > 1f) return s; } catch { }
        return new Vector2(280, 380);
    }

    /// <summary>Where the preview's CENTER should sit: beside the hovered row, clamped to the viewport.</summary>
    private static Vector2 PreviewCenter(Control anchor, Vector2 size)
    {
        try
        {
            var vp = _previewHost!.GetViewportRect().Size;
            var ar = anchor.GetGlobalRect();
            float halfW = size.X * 0.5f, halfH = size.Y * 0.5f;
            // Offset well clear of the row so the preview never lands under the cursor.
            float cx = ar.Position.X + ar.Size.X * 0.60f + halfW;
            float cy = ar.Position.Y + ar.Size.Y * 0.5f;
            cx = Mathf.Clamp(cx, halfW + 8f, Mathf.Max(halfW + 8f, vp.X - halfW - 8f));
            cy = Mathf.Clamp(cy, halfH + 8f, Mathf.Max(halfH + 8f, vp.Y - halfH - 8f));
            return new Vector2(cx, cy);
        }
        catch { return anchor.GetGlobalRect().Position; }
    }

    /// <summary>Unparent and return the borrowed NCard. Clearing Model first drops its subscriptions
    /// so the parked node isn't left listening to a card it no longer shows.</summary>
    private static void ReleasePooledCard()
    {
        if (_previewCard == null) return;
        try
        {
            if (GodotObject.IsInstanceValid(_previewCard))
            {
                _previewCard.Model = null;
                _previewCard.GetParent()?.RemoveChild(_previewCard);
                NodePool.Free(_previewCard);
            }
        }
        catch (Exception ex) { MainFile.Logger.Warn($"[{MainFile.ModId}] card preview release failed: {ex.Message}"); }
        _previewCard = null;
    }

    /// <summary>Bare card art, for when the pooled NCard isn't available.</summary>
    private static Control? BuildPortraitFallback(CardModel card, out Vector2 size)
    {
        size = new Vector2(300, 300);
        Texture2D? tex = null;
        try { tex = card.Portrait; } catch { }
        if (tex == null) return null;

        var tr = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = tex,
        };
        tr.CustomMinimumSize = size;
        tr.Size = size;
        tr.Position = -size * 0.5f;   // host is positioned at the intended centre
        return tr;
    }

    /// <summary>
    /// Tear down any live preview. Must run before the panel hides, the list rebuilds, or another row
    /// is hovered — a pooled NCard left parented here would be handed back out mid-combat still
    /// attached to our panel.
    /// </summary>
    private static void ClearPreview()
    {
        ReleasePooledCard();

        if (_previewHost != null && GodotObject.IsInstanceValid(_previewHost))
        {
            foreach (var c in _previewHost.GetChildren()) { _previewHost.RemoveChild(c); c.QueueFree(); }
            _previewHost.Visible = false;
        }
    }

    // ── Item naming (game text must go through the loc layer, never ToString) ──
    private static string CardName(CardModel card)
    {
        try { var s = card.Title; if (!string.IsNullOrWhiteSpace(s)) return s; } catch { }
        try { return card.Id.Entry ?? string.Empty; } catch { return string.Empty; }
    }

    private static string RelicName(RelicModel relic)
    {
        try { var s = relic.Title?.GetFormattedText(); if (!string.IsNullOrWhiteSpace(s)) return s; } catch { }
        try { return relic.Id.Entry ?? string.Empty; } catch { return string.Empty; }
    }

    private static string RarityOf(CardModel card)
    {
        try { return card.Rarity.ToString(); } catch { return string.Empty; }
    }

    private static string RarityOf(RelicModel relic)
    {
        try { return relic.Rarity.ToString(); } catch { return string.Empty; }
    }

    /// <summary>Whether a relic is worth offering as a toggle. Starter relics are excluded: a
    /// character's starting relics are written straight onto the player (<c>StartingRelics</c> →
    /// <c>player.Relics</c>) and never pass through the grab bag or <c>RelicCmd.Obtain</c>, so the
    /// filter cannot touch them — listing one would be a switch that silently does nothing.</summary>
    private static bool IsListableRelic(RelicModel relic)
    {
        try { return relic.Rarity != RelicRarity.Starter; } catch { return true; }
    }

    // ── Pack ↔ item linkage ─────────────────────────────────
    //
    // The pack checkbox and its detail list are two views of one setting, kept to a single invariant:
    //
    //     pack blocked  ⟺  every individually blockable item in it is blocked
    //
    // Unchecking a pack therefore switches all of its items off, and switching the last item off
    // blocks the pack. Keeping the pack flag in sync (rather than deriving the display from items
    // alone) matters because the flag also gates content the detail list cannot enumerate — a
    // character mod's shared colorless/curse cards, and its Ancient beings.
    //
    // Packs with no enumerable items (the base characters, whose cards are not individually
    // blockable) keep the plain two-state behaviour: the flag is the only state there is.

    /// <summary>Ids of everything individually blockable in a pack. Empty for base-game packs.</summary>
    private static List<string> PackItemIds(Drill kind, string key)
    {
        var ids = new List<string>();
        try
        {
            switch (kind)
            {
                case Drill.ModRelics:
                    if (RelicGuardService.GetModRelicPacks().TryGetValue(key, out var rp))
                        foreach (var r in rp.Relics)
                            if (IsListableRelic(r)) ids.Add(RelicGuardService.RelicIdOf(r));
                    break;
                case Drill.ModCards:
                    if (CardGuardService.GetModCardPacks().TryGetValue(key, out var mp))
                        foreach (var c in mp.Cards) ids.Add(CardGuardService.CardIdOf(c));
                    break;
                case Drill.CharacterCards:
                    foreach (var c in CardGuardService.GetPoolAllCards(key)) ids.Add(CardGuardService.CardIdOf(c));
                    break;
            }
        }
        catch (Exception ex) { MainFile.Logger.Warn($"[{MainFile.ModId}] pack item scan failed ({key}): {ex.Message}"); }
        ids.RemoveAll(string.IsNullOrEmpty);
        return ids;
    }

    /// <summary>Whether the panel is editing the all-characters scope rather than one character.</summary>
    private static bool IsAllScope =>
        string.Equals(_selected, CardGuardService.AllScope, StringComparison.Ordinal);

    /// <summary>Your own character's pack always applies — it has no blockable pack flag. The
    /// all-characters scope has no own pack (its key is never a pool title), so every pack is
    /// blockable there.</summary>
    private static bool IsOwnPack(Drill kind, string key) =>
        kind == Drill.CharacterCards && !IsAllScope
        && string.Equals(key, _selected, StringComparison.OrdinalIgnoreCase);

    // ── All-characters scope, seen from a character scope ───
    //
    // A block set for every character still has to be visible (and un-editable) while a single
    // character is selected — otherwise the row would show "allowed" for a card that can never
    // appear, and un-ticking it would write a per-character block that changes nothing.

    /// <summary>Whether this pack is blocked by the all-characters scope while a character is selected.</summary>
    private static bool PackBlockedGlobally(Drill kind, string key)
    {
        if (IsAllScope) return false;
        return kind switch
        {
            Drill.ModRelics => !RelicGuardService.GetModAllowed(CardGuardService.AllScope, key),
            Drill.ModCards => !CardGuardService.GetModAllowed(CardGuardService.AllScope, key),
            Drill.CharacterCards => !CardGuardService.GetCrossAllowed(CardGuardService.AllScope, key),
            _ => false,
        };
    }

    /// <summary>Whether this item is blocked by the all-characters scope while a character is selected.</summary>
    private static bool ItemBlockedGlobally(Drill kind, string id)
    {
        if (IsAllScope) return false;
        return kind == Drill.ModRelics
            ? !RelicGuardService.GetRelicAllowed(CardGuardService.AllScope, id)
            : !CardGuardService.GetCardAllowed(CardGuardService.AllScope, id);
    }

    private static bool PackBlocked(Drill kind, string key) => kind switch
    {
        Drill.ModRelics => !RelicGuardService.GetModAllowed(_selected, key),
        Drill.ModCards => !CardGuardService.GetModAllowed(_selected, key),
        Drill.CharacterCards => !IsOwnPack(kind, key) && !CardGuardService.GetCrossAllowed(_selected, key),
        _ => false,
    };

    private static void SetPackBlocked(Drill kind, string key, bool blocked)
    {
        if (IsOwnPack(kind, key)) return;
        switch (kind)
        {
            case Drill.ModRelics: RelicGuardService.SetModAllowed(_selected, key, !blocked); break;
            case Drill.ModCards: CardGuardService.SetModAllowed(_selected, key, !blocked); break;
            case Drill.CharacterCards: CardGuardService.SetCrossAllowed(_selected, key, !blocked); break;
        }
    }

    private static bool ItemAllowed(Drill kind, string id) => kind == Drill.ModRelics
        ? RelicGuardService.GetRelicAllowed(_selected, id)
        : CardGuardService.GetCardAllowed(_selected, id);

    private static void SetItemAllowed(Drill kind, string id, bool allowed)
    {
        if (kind == Drill.ModRelics) RelicGuardService.SetRelicAllowed(_selected, id, allowed);
        else CardGuardService.SetCardAllowed(_selected, id, allowed);
    }

    /// <summary><paramref name="Allowed"/> counts only what can actually appear, so a blocked pack
    /// reports 0 however its individual items happen to be ticked.</summary>
    private readonly record struct PackState(int Total, int Allowed, bool Blocked)
    {
        public int BlockedCount => Total - Allowed;
        public bool Partial => Total > 0 && Allowed > 0 && Allowed < Total;
    }

    private static PackState PackStateOf(Drill kind, string key)
    {
        var ids = PackItemIds(kind, key);
        bool blocked = PackBlocked(kind, key) || PackBlockedGlobally(kind, key);
        int allowed = 0;
        if (!blocked)
            foreach (var id in ids)
                if (ItemAllowed(kind, id) && !ItemBlockedGlobally(kind, id)) allowed++;
        return new PackState(ids.Count, allowed, blocked);
    }

    /// <summary>Pack checkbox clicked: drive every item to match, then set the flag.</summary>
    private static void ApplyPackToggle(Drill kind, string key, bool allowed)
    {
        foreach (var id in PackItemIds(kind, key)) SetItemAllowed(kind, id, allowed);
        SetPackBlocked(kind, key, !allowed);
    }

    /// <summary>Item toggled: restore the invariant by re-deriving the pack flag from its items.</summary>
    private static void ReconcilePack(Drill kind, string key)
    {
        if (IsOwnPack(kind, key)) return;
        var ids = PackItemIds(kind, key);
        if (ids.Count == 0) return; // nothing enumerable — the flag stands alone

        bool anyAllowed = false;
        foreach (var id in ids)
            if (ItemAllowed(kind, id)) { anyAllowed = true; break; }

        // Un-blocking the pack when an item comes back on is what makes the detail list usable at
        // all: without it the pack flag would keep suppressing everything the user just re-enabled.
        SetPackBlocked(kind, key, !anyAllowed);
    }

    // ── "At least one card survives" ────────────────────────
    //
    // The one rule the panel enforces, and the only one it needs. A block is absolute — the filter
    // never hands a blocked card back — which works because wherever the game wants more cards than
    // remain, the DEMAND is clamped instead (see Patches/CardFilterPatches + CardSelectPatches).
    // That has exactly one floor: something has to remain. Block literally every card and
    // CardGuardService.Filter runs out of substitutes and falls through to "pass everything
    // through unfiltered" — the one path where a blocked card could still surface. Refusing the
    // last block keeps that path unreachable, and needs no per-content minimums (30 for Sealed
    // Deck, 8 for Gorge, …) because those are handled by clamping.

    /// <summary>
    /// Whether every character the edit can affect still has at least one card it can be offered.
    /// Editing one character only has to hold for that character; editing the all-characters scope
    /// has to hold for ALL of them, since one global block can empty a class whose own pool is
    /// already thinned. Fails permissive: a scan error must never read as "you may not block this".
    /// </summary>
    private static bool EveryAffectedCharacterKeepsACard()
    {
        try
        {
            if (!IsAllScope) return AnyCardAllowedFor(_selected);
            foreach (var ci in CardGuardService.GetAllCharacters())
                if (!AnyCardAllowedFor(ci.Title)) return false;
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] allowed-card scan failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>Whether one card anywhere can still be offered to <paramref name="title"/>, under
    /// that character's own block-set AND the all-characters one. Stops at the first hit — this runs
    /// on every checkbox click, and a full tally would walk every pool of every character.</summary>
    private static bool AnyCardAllowedFor(string title)
    {
        foreach (var ci in CardGuardService.GetAllCharacters())
        {
            // Mirrors CardGuardService.IsAllowed: your own pool is never gated by a pack flag, so a
            // pack block on it only takes effect through the individual blocks it drives.
            bool own = string.Equals(ci.Title, title, StringComparison.OrdinalIgnoreCase);
            if (!own && !CardGuardService.GetCrossAllowedEffective(title, ci.Title)) continue;
            foreach (var id in PackItemIds(Drill.CharacterCards, ci.Title))
                if (CardGuardService.GetCardAllowedEffective(title, id)) return true;
        }
        foreach (var kv in CardGuardService.GetModCardPacks())
        {
            if (CardGuardService.IsCharacterMod(kv.Key)) continue;
            if (!CardGuardService.GetModAllowedEffective(title, kv.Key)) continue;
            foreach (var id in PackItemIds(Drill.ModCards, kv.Key))
                if (CardGuardService.GetCardAllowedEffective(title, id)) return true;
        }
        return false;
    }

    /// <summary>Everything one pack's toggles can change, so a refused edit can be put back exactly
    /// as it was — including the pack flag, which <see cref="ApplyPackToggle"/> moves too.</summary>
    private readonly record struct PackSnapshot(Drill Kind, string Key, bool Blocked, List<(string Id, bool Allowed)> Items);

    private static PackSnapshot Snapshot(Drill kind, string key)
    {
        var items = new List<(string, bool)>();
        foreach (var id in PackItemIds(kind, key)) items.Add((id, ItemAllowed(kind, id)));
        return new PackSnapshot(kind, key, PackBlocked(kind, key), items);
    }

    /// <summary>Keeps the edit if any card survives it; otherwise puts <paramref name="before"/> back
    /// and raises the notice. Relic edits are never refused — relics can't empty the card pool, and
    /// an empty relic bag already resolves to the game's own fallback relic.</summary>
    private static bool KeepIfAnyCardRemains(PackSnapshot before)
    {
        if (before.Kind == Drill.ModRelics) return true;
        if (EveryAffectedCharacterKeepsACard()) return true;

        foreach (var (id, allowed) in before.Items) SetItemAllowed(before.Kind, id, allowed);
        SetPackBlocked(before.Kind, before.Key, before.Blocked);
        _notice = Loc.T("min_one");
        MainFile.Logger.Info($"[{MainFile.ModId}] refused a block that would leave {_selected} with no cards at all.");
        return false;
    }

    private static string CountsSuffix(PackState s) =>
        s.Total == 0 ? string.Empty : Loc.F("counts_fmt", s.Allowed, s.BlockedCount);

    /// <summary>
    /// Build the pack row's checkbox with tri-state presentation: ticked when everything is allowed,
    /// clear when nothing is, and a distinct "partial" glyph in between. Godot's CheckBox has only
    /// two states, so the middle one is drawn by overriding its checked icon.
    /// </summary>
    private static CheckBox MakePackCheck(string label, Drill kind, string key, PackState s)
    {
        bool lockedByAll = PackBlockedGlobally(kind, key);
        if (lockedByAll) label += Loc.T("all_mark");
        bool on = s.Total == 0 ? !s.Blocked : s.Allowed > 0;
        var cb = MakeCheck(label + CountsSuffix(s), on, v =>
        {
            var before = Snapshot(kind, key);
            ApplyPackToggle(kind, key, v);
            if (KeepIfAnyCardRemains(before)) CardGuardConfig.Save();
            // Deferred: this fires from the checkbox's own Toggled signal, and the rebuild frees it.
            Callable.From(RebuildRight).CallDeferred();
        });
        if (lockedByAll) cb.Disabled = true;
        if (s.Partial)
        {
            cb.AddThemeIconOverride("checked", PartialIcon(cb));
            cb.AddThemeColorOverride("font_color", GOLD);
        }
        return cb;
    }

    private static readonly Dictionary<int, Texture2D> _partialIcons = new();

    /// <summary>An "indeterminate" tick drawn at the theme's own checkbox size, so swapping it in
    /// doesn't shift the row's layout.</summary>
    private static Texture2D PartialIcon(CheckBox probe)
    {
        int w = 20, h = 20;
        try
        {
            var themed = probe.GetThemeIcon("checked");
            if (themed != null && themed.GetWidth() > 2 && themed.GetHeight() > 2)
            { w = themed.GetWidth(); h = themed.GetHeight(); }
        }
        catch { }

        int cacheKey = w * 10000 + h;
        if (_partialIcons.TryGetValue(cacheKey, out var cached) && cached != null) return cached;

        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        int y0 = (int)(h * 0.42f), y1 = (int)(h * 0.58f);
        int x0 = (int)(w * 0.24f), x1 = (int)(w * 0.76f);
        for (int y = y0; y <= y1 && y < h; y++)
            for (int x = x0; x <= x1 && x < w; x++)
                img.SetPixel(x, y, GOLD);

        var tex = ImageTexture.CreateFromImage(img);
        _partialIcons[cacheKey] = tex;
        return tex;
    }

    private static string Display(string title)
    {
        if (string.Equals(title, CardGuardService.AllScope, StringComparison.Ordinal)) return Loc.T("all_chars");
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

    /// <summary>A pack row: its checkbox, plus a Detail button when the pack has individually
    /// blockable (mod-added) items. Base-game packs pass null and stay all-or-nothing.</summary>
    private static Control RowWithDetail(CheckBox cb, Action? onDetail)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        cb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(cb);
        if (onDetail != null)
        {
            var b = new Button { Text = Loc.T("detail"), CustomMinimumSize = new Vector2(110, 32) };
            b.AddThemeFontSizeOverride("font_size", 14);
            b.Pressed += onDetail;
            row.AddChild(b);
        }
        return row;
    }
}
