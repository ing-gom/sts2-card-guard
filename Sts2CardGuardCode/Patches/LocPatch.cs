using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2CardGuard.Patches;

/// <summary>
/// Re-injects our UI strings into the game loc table whenever the language changes, so the panel
/// always shows text in the current language.
/// </summary>
[HarmonyPatch(typeof(LocManager), nameof(LocManager.SetLanguage))]
internal static class LocManager_SetLanguage_Patch
{
    private static void Postfix(LocManager __instance) => Loc.Apply(__instance);
}
