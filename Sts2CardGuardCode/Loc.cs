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
        ["button"] = "Card Pack Filter",
        ["title"] = "Card Guard",
        ["enable"] = "Enable",
        ["colorless"] = "Allow colorless",
        ["hint"] = "Default: all card packs are allowed. Pick a character on the left, then UNCHECK a pack on the right to block it for that character.",
        ["configure"] = "Configure character",
        ["sec_char"] = "Character card packs",
        ["sec_mod"] = "Mod card packs",
        ["own_suffix"] = "  (own — always on)",
        ["cards"] = "cards",
        ["no_mods"] = "(no non-character card mods)",
        ["mod_colorless_fmt"] = "  ({0} colorless)",
    };

    private static readonly Dictionary<string, string> Ko = new()
    {
        ["button"] = "카드팩 필터",
        ["title"] = "카드 가드",
        ["enable"] = "사용",
        ["colorless"] = "무색 허용",
        ["hint"] = "기본: 모든 카드팩 허용. 왼쪽에서 캐릭터를 고르고, 오른쪽에서 차단할 카드팩의 체크를 해제하세요.",
        ["configure"] = "설정할 캐릭터",
        ["sec_char"] = "캐릭터 카드팩",
        ["sec_mod"] = "모드 카드팩",
        ["own_suffix"] = "  (자기 카드팩 — 항상 적용)",
        ["cards"] = "장",
        ["no_mods"] = "(캐릭터 외 카드 추가 모드 없음)",
        ["mod_colorless_fmt"] = "  (무색 {0}장)",
    };

    private static readonly Dictionary<string, string> Zh = new()
    {
        ["button"] = "卡包过滤器",
        ["title"] = "卡包过滤器",
        ["enable"] = "启用",
        ["colorless"] = "允许无色卡",
        ["hint"] = "默认：允许所有卡包。在左侧选择角色，然后在右侧取消勾选某个卡包即可为该角色屏蔽。",
        ["configure"] = "选择角色",
        ["sec_char"] = "角色卡包",
        ["sec_mod"] = "模组卡包",
        ["own_suffix"] = "  (自身 — 始终启用)",
        ["cards"] = "张",
        ["no_mods"] = "(无非角色加牌模组)",
        ["mod_colorless_fmt"] = "  (无色 {0} 张)",
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

    /// <summary>Localized text for <paramref name="key"/> — from the loc table, falling back to the in-code dict.</summary>
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
