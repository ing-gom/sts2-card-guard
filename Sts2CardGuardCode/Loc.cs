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
        ["button"] = "Content Filter",
        ["title"] = "Card / Relic / Potion / Event Filter",
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
        ["tab_potions"] = "Potions",
        ["tab_events"] = "Events",
        ["potion_hint"] = "Pick a scope on the left — a single character, or ★ All characters — then UNCHECK a potion pool to block all of it, or press Detail to block potions one by one. Base-game potions included. A blocked potion never shows up in combat rewards, shop stock, potion events, Entropic Brew or Phial Holster. Block EVERY potion and the routes close instead: no potion rewards at all, no potion shelf in the shop, and potion-granting relics simply do nothing.",
        ["potion_sec"] = "Potion pools",
        ["potion_shared"] = "Shared potions",
        ["potion_mod_sec"] = "Mod potion packs",
        ["potions"] = "potions",
        ["no_potions"] = "(no potion pool applies to this scope)",
        ["no_potion_mods"] = "(no potion-adding mods)",
        ["full_ban_potion"] = "Every potion is now blocked: potion rewards and the shop's potion shelf will not appear at all, and relics that hand out potions will do nothing. Re-allow any potion to undo this.",
        ["event_hint"] = "UNCHECK an event to keep it off the map: when the act would place it, the game skips to the next event in its list instead. Base-game events included. Block EVERY event and '?' map points stop becoming event rooms altogether (they turn into fights, shops or treasure). Events belong to the MAP, not to one player, so a block set for a single character applies whenever that character is in the run — use ★ All characters if you never want to see it. Ancient beings (Neow and friends) are never affected: the game hands those out through a path this filter cannot reach.",
        ["event_sec"] = "Map events by act",
        ["event_shared"] = "Shared events (every act)",
        ["event_mod_sec"] = "Mod event packs",
        ["events"] = "events",
        ["no_events"] = "(no events found)",
        ["no_event_mods"] = "(no event-adding mods)",
        ["full_ban_event"] = "Every event is now blocked: '?' map points will no longer become event rooms. Neow and a first-ever run's two scripted events are the exception — the game places those through a path this filter cannot reach.",
    };

    private static readonly Dictionary<string, string> Ko = new()
    {
        ["button"] = "콘텐츠 필터",
        ["title"] = "카드/유물/포션/이벤트 필터",
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
        ["tab_potions"] = "포션",
        ["tab_events"] = "이벤트",
        ["potion_hint"] = "왼쪽에서 적용 범위(개별 캐릭터 또는 [★ 모든 캐릭터])를 고르고, 오른쪽에서 포션 풀의 체크를 해제해 통째로 차단하거나 [상세]로 포션을 하나씩 차단하세요. 기본 게임 포션도 포함입니다. 차단한 포션은 전투 보상·상점·포션 이벤트·엔트로피 브루·약병 홀스터 어디에도 나오지 않습니다. 포션을 전부 차단하면 경로 자체가 닫힙니다 — 포션 보상이 아예 안 뜨고, 상점 포션 진열도 사라지고, 포션을 주는 유물은 아무 동작도 하지 않습니다.",
        ["potion_sec"] = "포션 풀",
        ["potion_shared"] = "공용 포션",
        ["potion_mod_sec"] = "모드 포션팩",
        ["potions"] = "개",
        ["no_potions"] = "(이 범위에 해당하는 포션 풀 없음)",
        ["no_potion_mods"] = "(포션 추가 모드 없음)",
        ["full_ban_potion"] = "이제 모든 포션이 차단됐습니다: 포션 보상과 상점 포션 진열이 아예 나오지 않고, 포션을 주는 유물은 무동작이 됩니다. 아무 포션이나 다시 허용하면 원래대로 돌아갑니다.",
        ["event_hint"] = "체크를 해제한 이벤트는 맵에 배치되지 않습니다 — 해당 막이 그 이벤트를 꺼내려 할 때 목록의 다음 이벤트로 건너뜁니다. 기본 게임 이벤트도 포함입니다. 이벤트를 전부 차단하면 [?] 노드가 이벤트 방으로 해석되는 것 자체가 막힙니다(전투·상점·보물로 바뀜). 이벤트는 플레이어가 아니라 맵에 속하므로, 개별 캐릭터에 설정한 차단은 그 캐릭터가 런에 있을 때 적용됩니다 — 아예 보고 싶지 않다면 [★ 모든 캐릭터]를 쓰세요. 고대 존재(Neow 등)는 영향받지 않습니다: 이 필터가 닿지 않는 경로로 등장합니다.",
        ["event_sec"] = "막별 맵 이벤트",
        ["event_shared"] = "공용 이벤트 (모든 막)",
        ["event_mod_sec"] = "모드 이벤트팩",
        ["events"] = "개",
        ["no_events"] = "(이벤트를 찾지 못했습니다)",
        ["no_event_mods"] = "(이벤트 추가 모드 없음)",
        ["full_ban_event"] = "이제 모든 이벤트가 차단됐습니다: [?] 노드가 더는 이벤트 방이 되지 않습니다. Neow와 최초 런의 스크립트 이벤트 2개는 예외입니다 — 이 필터가 닿지 않는 경로로 배치됩니다.",
    };

    private static readonly Dictionary<string, string> Zh = new()
    {
        ["button"] = "内容过滤器",
        ["title"] = "卡牌 / 遗物 / 药水 / 事件 过滤器",
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
        ["tab_potions"] = "药水",
        ["tab_events"] = "事件",
        ["potion_hint"] = "在左侧选择适用范围（单个角色或[★ 全部角色]），然后在右侧取消勾选某个药水池即可整体屏蔽，或点击[详细]逐个屏蔽药水。包括基础药水。被屏蔽的药水不会出现在战斗奖励、商店、药水事件、熵酿或药瓶皮套中。若屏蔽全部药水，获取途径本身会被关闭 — 完全不出现药水奖励，商店也不再有药水货架，发放药水的遗物则不产生任何效果。",
        ["potion_sec"] = "药水池",
        ["potion_shared"] = "通用药水",
        ["potion_mod_sec"] = "模组药水包",
        ["potions"] = "种",
        ["no_potions"] = "(该范围没有对应的药水池)",
        ["no_potion_mods"] = "(无添加药水的模组)",
        ["full_ban_potion"] = "现已屏蔽全部药水：药水奖励与商店药水货架将完全不出现，发放药水的遗物也不会有任何效果。重新允许任意一瓶药水即可恢复。",
        ["event_hint"] = "取消勾选的事件不会被放置到地图上 — 当该幕要取出它时，游戏会跳到列表中的下一个事件。包括基础事件。若屏蔽全部事件，[?]节点将不再解析为事件房间（改为战斗、商店或宝箱）。事件属于地图而不属于某个玩家，因此对单个角色设置的屏蔽会在该角色参与本局时生效 — 若完全不想看到，请使用[★ 全部角色]。远古存在（Neow 等）不受影响：它们通过本过滤器无法触及的路径出现。",
        ["event_sec"] = "各幕地图事件",
        ["event_shared"] = "通用事件（所有幕）",
        ["event_mod_sec"] = "模组事件包",
        ["events"] = "个",
        ["no_events"] = "(未找到事件)",
        ["no_event_mods"] = "(无添加事件的模组)",
        ["full_ban_event"] = "现已屏蔽全部事件：[?]节点不再变成事件房间。Neow 与首次游戏的两个脚本事件除外 — 游戏通过本过滤器无法触及的路径放置它们。",
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
