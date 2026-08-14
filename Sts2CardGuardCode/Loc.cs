using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2CardGuard;

/// <summary>
/// UI localization via the game's loc table. Our strings are merged (prefixed) into the shared
/// "main_menu_ui" table for the current language, re-applied on every language change (see
/// <c>LocManager_SetLanguage_Patch</c>). Text is read back through <see cref="LocString"/>; if the
/// table isn't available the in-code dictionary is used, so display is always robust.
///
/// English, Korean and Simplified Chinese are provided; any other game language falls back to English.
/// </summary>
internal static class Loc
{
    private const string Table = "main_menu_ui";
    private const string Prefix = "CARDGUARD.";

    private static readonly Dictionary<string, string> En = new()
    {
        ["button"] = "Card / Relic Filter",
        ["title"] = "Card / Relic Pack Filter",
        ["enable"] = "Enable",
        ["colorless"] = "Allow colorless",
        ["hint"] = "Default: all card packs are allowed. Pick a scope on the left — a single character, or ★ All characters, which applies no matter who you play — then UNCHECK a pack on the right to block it. Press Detail to block cards one by one; base-game cards included, your own class too. At least one card must stay allowed.",
        ["configure"] = "Configure character",
        ["all_chars"] = "★ All characters",
        ["all_mark"] = "  (blocked for every character)",
        ["sec_char"] = "Character card packs",
        ["sec_mod"] = "Mod card packs",
        ["own_suffix"] = "  (own — always on)",
        ["cards"] = "cards",
        ["no_mods"] = "(no non-character card mods)",
        ["mod_colorless_fmt"] = "  ({0} colorless)",
        ["tab_cards"] = "Cards",
        ["tab_relics"] = "Relics",
        ["relic_hint"] = "Pick a scope on the left — a single character, or ★ All characters — then UNCHECK a mod's relics to block them. Press Detail to block single relics. Base-game relics are never affected.",
        ["relic_sec"] = "Mod relic packs",
        ["relics"] = "relics",
        ["no_relic_mods"] = "(no relic-adding mods)",
        ["detail"] = "Detail ▸",
        ["back"] = "◂ Back",
        ["search"] = "Search",
        ["all_on"] = "Allow all",
        ["all_off"] = "Block all",
        ["counts_fmt"] = "   ({0} on / {1} off)",
        ["no_items"] = "(nothing matches)",
        ["pack_off_note"] = "This whole pack is currently blocked — re-check the pack to make these individual settings take effect.",
        ["min_one"] = "At least one card has to stay allowed — this block was not applied.",
    };

    private static readonly Dictionary<string, string> Ko = new()
    {
        ["button"] = "카드/유물 필터",
        ["title"] = "카드/유물 팩 필터",
        ["enable"] = "사용",
        ["colorless"] = "무색 허용",
        ["hint"] = "기본: 모든 카드팩 허용. 왼쪽에서 적용 범위를 고르세요 — 개별 캐릭터, 또는 어떤 캐릭터로 플레이하든 적용되는 [★ 모든 캐릭터]. 그다음 오른쪽에서 차단할 카드팩의 체크를 해제하세요. [상세]로 카드를 한 장씩 차단할 수 있습니다 — 기본 게임 카드도, 자기 클래스도 포함입니다(최소 한 장은 남겨야 함).",
        ["configure"] = "설정할 캐릭터",
        ["all_chars"] = "★ 모든 캐릭터",
        ["all_mark"] = "  (모든 캐릭터에서 차단됨)",
        ["sec_char"] = "캐릭터 카드팩",
        ["sec_mod"] = "모드 카드팩",
        ["own_suffix"] = "  (자기 카드팩 — 항상 적용)",
        ["cards"] = "장",
        ["no_mods"] = "(캐릭터 외 카드 추가 모드 없음)",
        ["mod_colorless_fmt"] = "  (무색 {0}장)",
        ["tab_cards"] = "카드",
        ["tab_relics"] = "유물",
        ["relic_hint"] = "왼쪽에서 적용 범위(개별 캐릭터 또는 [★ 모든 캐릭터])를 고르고, 오른쪽에서 차단할 모드 유물의 체크를 해제하세요. [상세]로 유물 하나씩 차단할 수 있습니다. 기본 유물은 영향받지 않습니다.",
        ["relic_sec"] = "모드 유물팩",
        ["relics"] = "개",
        ["no_relic_mods"] = "(유물 추가 모드 없음)",
        ["detail"] = "상세 ▸",
        ["back"] = "◂ 뒤로",
        ["search"] = "검색",
        ["all_on"] = "전체 허용",
        ["all_off"] = "전체 차단",
        ["counts_fmt"] = "   (허용 {0} / 차단 {1})",
        ["no_items"] = "(검색 결과 없음)",
        ["pack_off_note"] = "이 팩은 현재 통째로 차단된 상태입니다 — 팩 체크를 다시 켜야 아래 개별 설정이 적용됩니다.",
        ["min_one"] = "카드를 최소 한 장은 남겨야 합니다 — 이 차단은 적용되지 않았습니다.",
    };

    private static readonly Dictionary<string, string> Zh = new()
    {
        ["button"] = "卡牌/遗物过滤器",
        ["title"] = "卡牌/遗物包过滤器",
        ["enable"] = "启用",
        ["colorless"] = "允许无色卡",
        ["hint"] = "默认：允许所有卡包。在左侧选择适用范围 — 单个角色，或[★ 全部角色]（无论玩哪个角色都生效），然后在右侧取消勾选某个卡包即可屏蔽。可点击[详细]逐张屏蔽 — 包括基础卡牌与本职业卡牌（至少需保留一张）。",
        ["configure"] = "选择角色",
        ["all_chars"] = "★ 全部角色",
        ["all_mark"] = "  (已对全部角色屏蔽)",
        ["sec_char"] = "角色卡包",
        ["sec_mod"] = "模组卡包",
        ["own_suffix"] = "  (自身 — 始终启用)",
        ["cards"] = "张",
        ["no_mods"] = "(无非角色加牌模组)",
        ["mod_colorless_fmt"] = "  (无色 {0} 张)",
        ["tab_cards"] = "卡牌",
        ["tab_relics"] = "遗物",
        ["relic_hint"] = "在左侧选择适用范围（单个角色或[★ 全部角色]），然后在右侧取消勾选某个模组的遗物即可屏蔽。可点击[详细]逐件屏蔽。基础遗物不受影响。",
        ["relic_sec"] = "模组遗物包",
        ["relics"] = "件",
        ["no_relic_mods"] = "(无添加遗物的模组)",
        ["detail"] = "详细 ▸",
        ["back"] = "◂ 返回",
        ["search"] = "搜索",
        ["all_on"] = "全部允许",
        ["all_off"] = "全部屏蔽",
        ["counts_fmt"] = "   (允许 {0} / 屏蔽 {1})",
        ["no_items"] = "(无匹配结果)",
        ["pack_off_note"] = "该卡包目前被整体屏蔽 — 需重新勾选卡包，下面的单项设置才会生效。",
        ["min_one"] = "至少需要保留一张卡牌 — 本次屏蔽未生效。",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Langs = new()
    {
        ["en"] = En,
        ["ko"] = Ko,
        ["zh"] = Zh,
    };

    /// <summary>Resolve the game language to "en" / "ko" / "zh" (Simplified). Others -> "en".</summary>
    private static string LangKey()
    {
        string code = "";
        try { code = (LocManager.Instance?.Language ?? "").ToLowerInvariant(); } catch { }
        if (string.IsNullOrEmpty(code))
        {
            try { code = (TranslationServer.GetLocale() ?? "").ToLowerInvariant(); } catch { }
        }
        if (code.StartsWith("ko", StringComparison.Ordinal)) return "ko";
        // Traditional Chinese has no dictionary here -> fall back to English.
        if (code.Contains("hant") || code.Contains("tchinese") || code.Contains("tw")) return "en";
        if (code.Contains("hans") || code.Contains("schinese") || code.StartsWith("zh", StringComparison.Ordinal) || code.Contains("chinese"))
            return "zh";
        return "en";
    }

    private static Dictionary<string, string> Dict() => Langs.TryGetValue(LangKey(), out var d) ? d : En;

    /// <summary>Merge the current-language strings into the game loc table. Safe to call repeatedly.</summary>
    public static void Apply(LocManager? mgr)
    {
        try
        {
            mgr ??= LocManager.Instance;
            if (mgr == null) return;
            var dict = Dict();
            var merged = new Dictionary<string, string>(dict.Count);
            foreach (var kv in dict) merged[Prefix + kv.Key] = kv.Value;
            mgr.GetTable(Table)?.MergeWith(merged);
        }
        catch { /* table not present yet — T() falls back to the in-code dict */ }
    }

    /// <summary>
    /// Localized FORMAT string applied with <paramref name="args"/>.
    ///
    /// Deliberately bypasses the loc table, unlike <see cref="T"/>: the table goes through
    /// <c>LocString.GetFormattedText()</c>, and SmartFormat reads a lone <c>{0}</c> as a selector on
    /// the LocString's argument bag — rendering it as
    /// <c>System.Collections.Generic.Dictionary`2[System.String,System.Object]</c> rather than
    /// leaving the placeholder for <c>string.Format</c>. It does not throw, so <see cref="T"/>'s
    /// fallback never engages and the corruption reaches the screen. (Two placeholders happen to
    /// throw and fall back correctly, which makes the bug look intermittent.)
    ///
    /// The in-code dictionaries are the source of these strings anyway — <see cref="Apply"/> only
    /// copies them into the table — so formatting from them loses no translation.
    /// </summary>
    public static string F(string key, params object[] args)
    {
        var d = Dict();
        string fmt = d.TryGetValue(key, out var v) ? v
                   : En.TryGetValue(key, out var e) ? e
                   : key;
        try { return string.Format(fmt, args); }
        catch { return fmt; }
    }

    /// <summary>Localized text for <paramref name="key"/> — from the loc table, falling back to the in-code dict.
    /// For strings with <c>{0}</c>-style placeholders use <see cref="F"/> instead.</summary>
    public static string T(string key)
    {
        try
        {
            var s = new LocString(Table, Prefix + key).GetFormattedText();
            if (!string.IsNullOrEmpty(s) && s != Prefix + key) return s;
        }
        catch { }
        var d = Dict();
        if (d.TryGetValue(key, out var v)) return v;
        return En.TryGetValue(key, out var e) ? e : key;
    }
}
