# StS2 Card / Relic Pack Filter

Control which **card packs and relics** can appear in your Slay the Spire 2 run — per character. Stops
other characters' cards and other mods' cards/relics from being mixed into your rewards, shops, events,
treasure, and in-combat generation.

![thumbnail](docs/thumb.png)

## Why

With character mods and content mods installed, foreign cards and relics can end up in your offers.
This mod lets you pick, per character, exactly which card packs and which mods' relics are allowed to
show up. A two-tab panel (**Cards** / **Relics**) on the character-select screen drives it.

## Relics & Ancients (v0.4.0)

Blocking a mod on the **Relics** tab removes its relics from every run source (rewards, shops, treasure
rooms); relics granted directly — including from Ancient events — are substituted with a normal relic.
If a mod replaces the vanilla Ancient events with its own, blocking it restores that act's **original
vanilla Ancient** in place of the mod's, so its custom ancient (and everything it offers) is gone.
Base-game relics are never affected. Both card and relic filtering work in single-player and co-op
(networked runs use the host's settings).

## How it works

Rather than patching individual card-generation effects, Card Guard filters the **candidate card
pool** that every card source draws from, before the RNG picks:

| System | Hook |
|---|---|
| Card rewards + events/treasures | `CardCreationOptions.GetPossibleCards` |
| In-combat generation ("add a random card") | `CardFactory.GetForCombat` / `GetDistinctForCombat` |
| Shop | `CardFactory.CreateForMerchant` |
| Cross-character event choices (Colorful Philosophers) | `ColorfulPhilosophers.GenerateInitialOptions` |

The filter is **subtractive** and never empties a pool (if every candidate would be removed, the
original set is passed through), so it can't crash reward/combat RNG. The card library / compendium
is untouched. The current character is read from `player.Character.CardPool` — no guessing.

**Cross-character sources.** Relics and events that deliberately hand you *another* character's cards
— **Kaleidoscope**, **Prismatic Gem**, **Splash**, and the **Colorful Philosophers** event — all
draw their cards through the pool hooks above, so a blocked character's cards are filtered there too.
When a fully-blocked pool would be emptied, an *allowed other* character's cards are substituted
first (falling back to your own only if no other is allowed), so the Kaleidoscope no longer surfaces
your own class. The Colorful Philosophers event also has its **choice list** filtered directly, so a
blocked character never even appears as an option.

## In-game panel

The character-select screen gets a **Card Pack Filter** button (bottom-right). It opens a full-screen
panel (no ModConfig dependency):

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

## Multiplayer (co-op)

STS2 co-op is host-authoritative lockstep: every peer re-computes card generation from the host's
shared RNG, and a peer whose result diverges is kicked back to the menu. Since Card Guard filters
the candidate pool *before* the pick, two players with different settings would filter differently
and desync. To prevent this, **in a networked run every player automatically uses the host's Card
Guard settings** (each client's own settings are ignored for that run) — so all peers filter
identically and stay in sync. The host's config is exchanged in the lobby before the run starts.

- **Both players must have the mod** (matching version). If a peer lacks it, or the host's config
  can't be confirmed, Card Guard **turns filtering off for that run** rather than risk a desync — it
  never causes a disconnect, it just does nothing that run.
- Your saved settings are untouched; the host-follow behavior only applies inside a co-op run.
- Singleplayer is unaffected (uses your own settings as before).

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
