using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2CardGuard;

/// <summary>
/// Dev-console command <c>cardguard</c>. Reports, for the current run's character, how many cards
/// from every card pool (base characters, colorless, and each mod) CAN appear under the current
/// Card Guard settings — a safe way to verify that blocking a pack actually removes its cards.
///
/// Auto-registered: the game's DevConsole concats <c>GetSubtypesInMods&lt;AbstractConsoleCmd&gt;()</c>,
/// so subclassing <see cref="AbstractConsoleCmd"/> in the mod assembly wires it up. Open the console
/// with the backtick key while running modded.
/// </summary>
public class CardGuardCmd : AbstractConsoleCmd
{
    public override string CmdName => "cardguard";
    public override string Args => "";
    public override string Description =>
        "Reports which cards can appear in the current run per Card Guard settings (pool & mod counts).";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (issuingPlayer == null)
            return new CmdResult(success: false, "No active player — start a run first.");

        var pool = CardGuardService.TryGetCurrentPool(issuingPlayer);
        string cur = pool?.Title ?? "?";

        var sb = new StringBuilder();
        int uniTotal = 0, uniAllowed = 0;
        // Per-mod tally of (allowed, total) under the current settings.
        var modAllowed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var modTotal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pl in ModelDb.AllCardPools.OrderBy(x => x?.Title))
            {
                if (pl == null) continue;
                int poolTotal = 0, unlocked = 0, allowed = 0;
                try { poolTotal = pl.AllCards.Count(); } catch { }

                // Use the game's UNLOCKED set (respects epoch/unlock gating) so the report matches
                // what can actually appear, not the raw pool.
                IEnumerable<CardModel> cards;
                try { cards = pl.GetUnlockedCards(issuingPlayer.UnlockState, issuingPlayer.RunState.CardMultiplayerConstraint); }
                catch { try { cards = pl.AllCards; } catch { continue; } }

                foreach (var c in cards)
                {
                    if (c == null) continue;
                    unlocked++;
                    bool ok = CardGuardService.WouldAppear(c, issuingPlayer);
                    if (ok) allowed++;

                    string mod = CardGuardService.ModNameOf(c);
                    if (mod.Length > 0)
                    {
                        modTotal.TryGetValue(mod, out int mt); modTotal[mod] = mt + 1;
                        if (ok) { modAllowed.TryGetValue(mod, out int ma); modAllowed[mod] = ma + 1; }
                    }
                }
                uniTotal += unlocked;
                uniAllowed += allowed;
                string lockNote = poolTotal > unlocked ? $" ({poolTotal - unlocked} locked)" : "";
                sb.AppendLine($"  {pl.Title}: {allowed}/{unlocked} can appear{lockNote}");
            }
        }
        catch (Exception ex)
        {
            return new CmdResult(success: false, $"scan failed: {ex.Message}");
        }

        var mods = CardGuardService.GetModCardPacks()
            .Where(kv => !CardGuardService.IsCharacterMod(kv.Key))
            .OrderBy(k => k.Key).ToList();
        if (mods.Count > 0)
        {
            sb.AppendLine("  -- mod card packs (allowed/total) --");
            foreach (var kv in mods)
            {
                var mp = kv.Value;
                modAllowed.TryGetValue(kv.Key, out int ma);
                modTotal.TryGetValue(kv.Key, out int mt);
                if (mt == 0) mt = mp.Total;
                string colorless = mp.Colorless > 0 ? $", {mp.Colorless} colorless" : "";
                sb.AppendLine($"  {kv.Key}: {ma}/{mt} can appear{colorless}");
            }
        }

        string full = $"[CardGuard] character '{cur}': {uniAllowed}/{uniTotal} cards can appear.\n{sb}";
        MainFile.Logger.Info(full);
        return new CmdResult(success: true, full);
    }
}
