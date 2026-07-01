# StS2 Card Guard

Control which **card packs** can appear in your Slay the Spire 2 run — per character. Stops other
characters' cards and other mods' cards from being mixed into your card rewards, shops, events, and
in-combat card generation.

![thumbnail](docs/thumb.png)

## Why

With character mods and card mods installed, foreign cards can end up in your offers. Card Guard lets
you pick, for each character, exactly which card packs (base characters, custom characters, and mods)
are allowed to show up.

## How it works

Rather than patching individual card-generation effects, Card Guard filters the **candidate card
pool** that every card source draws from, before the RNG picks:

| System | Hook |
|---|---|
| Card rewards + events/treasures | `CardCreationOptions.GetPossibleCards` |
| In-combat generation ("add a random card") | `CardFactory.GetForCombat` / `GetDistinctForCombat` |
| Shop | `CardFactory.CreateForMerchant` |

The filter is **subtractive** and never empties a pool (if every candidate would be removed, the
original set is passed through), so it can't crash reward/combat RNG. The card library / compendium
is untouched. The current character is read from `player.Character.CardPool` — no guessing.

## In-game panel

The character-select screen gets a **Card Pack Filter** button (bottom-right). It opens a full-screen
panel (no ModConfig dependency):

- **Top toggles** — *Enable*, *Allow colorless*.
- **Left** — pick the character you are configuring for (base **and** custom characters).
- **Right** — the card packs for that character, each with a card count:
  - **Character card packs** — the selected character's own pack (always on) + every other character
    (base and custom). Uncheck one to block that class's cards.
  - **Mod card packs** — each non-character card mod (e.g. a mod that adds colorless cards), showing
    its card count and how many are colorless. Uncheck to block.

**Default is permissive: every pack is checked (allowed).** Uncheck a pack to block it for that
character. Custom characters appear only in the character section (one checkbox controls them).

Settings persist to `Settings/card_guard_config.txt` (JSON) next to the mod DLL and apply from the
first screen on the next launch. English, Korean and Simplified Chinese are supported (follows the game language).

## Debug console

Open the dev console (backtick) and run **`cardguard`** for a report of how many cards from each pool
can appear for the current character under your settings — e.g. `silent: 0/86 can appear` after you
block Silent. Locked-by-progression cards are shown as `(N locked)`.

## Scope / limitations

- **System cards** (curse/status/token/event/quest — Wound, Burn, etc.) are never blocked.
- Blocking is subtractive: it removes cards the game/another mod would have offered; it does not add
  other characters into the shop's native pools.
- If a source would offer *only* disallowed cards, they pass through unchanged (crash-safety).
- Card-generation effects that create a *specific hardcoded* card (not drawn from a pool) are not
  affected.
- Unlocked-by-progression gating is the game's own system, not this mod.

## Author

inggom — a sibling mod of the STS2 Card Advisor toolset.
