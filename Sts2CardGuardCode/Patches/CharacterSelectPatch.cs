using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using Sts2CardGuard.Ui;

namespace Sts2CardGuard.Patches;

/// <summary>
/// Adds the "Card Guard" button (directly below the checkbox on the character-select screen)
/// + settings panel once that screen is ready.
/// </summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), "_Ready")]
internal static class NCharacterSelectScreen_Ready_Patch
{
    private static void Postfix(NCharacterSelectScreen __instance) => CardGuardPanel.Attach(__instance);
}
