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
When a fully-blocked pool would be emptied, cards from **one** allowed other character are
substituted (falling back to your own only if no other is allowed), so the Kaleidoscope no longer
surfaces your own class and the replacement still reads as a single class instead of a mash-up of
every class you left allowed. The Colorful Philosophers event also has its **choice list** filtered
directly: each option is matched against the card pool it actually offers, so mod-added characters
are caught too — RitsuLib extends this event with them — as is a character whose cards you have
blocked one by one. A blocked choice is **replaced** by another allowed character rather than
removed, so the event keeps its full set of options instead of shrinking (or, with every rolled
character blocked, offering nothing but Skip); a choice is only dropped when no allowed character
is left to stand in. If you have blocked *every* other character the event is not placed on the map
at all, so a room is not spent on an event that would end the moment you walked into it.

## In-game panel

The character-select screen gets a **Card / Relic Filter** button (bottom-right). It opens a full-screen
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

## Per-item blocking (v0.5.0, base-game cards since v0.6.0)

A whole pack is often too blunt — usually only a handful of cards or relics are unwanted.
Press **Detail** next to any pack to open its contents and tick them one by one.

- **Cards** — hovering a row shows the **real card**, rendered by the game itself, so you can see the
  art, cost and text of exactly what you are about to block.
- **Relics** — every row carries its icon.
- A search box and **Allow all / Block all** keep long lists (200+ cards) workable.

**Every card is individually blockable, base-game ones included** — your own class as well as any
other. Only one rule is enforced: at least one card has to stay allowed. Relics remain mod-only,
because the game has no per-character base relic pool to list (a character's starting relics are
written straight onto the player and never pass through the filter at all).

**A block is absolute.** A blocked card is never handed back to keep something running. Where the
game would otherwise want more cards than remain — a 3-card reward, Sealed Deck's 30, the Room Full
of Cheese's 8 commons — Card Guard reduces what the game *asks for* instead, so the screen simply
shows fewer cards and every one of them is a card you allowed.

**The pack checkbox and its detail list are one setting.** Unchecking a pack switches every item in it
off; switching the last item off blocks the pack. When only part of a pack is allowed, the checkbox
shows a partial mark and the row reports the tally, e.g. `SlayTheUniverse — 117 relics (115 on / 2 off)`.
Keeping the pack flag in step matters beyond cosmetics: that flag also gates content the detail list
cannot enumerate — a character mod's shared colorless/curse cards, and its Ancient beings.

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

- **Both players must have the mod, on v0.7.0 or newer.** If a peer lacks it, or the host's config
  can't be confirmed, Card Guard **turns filtering off for that run** rather than risk a desync — it
  never causes a disconnect, it just does nothing that run. Before v0.7.0 the config exchange lost a
  race against the joining client and this fallback triggered in every two-player lobby, so co-op
  filtering never actually engaged.
- Your saved settings are untouched; the host-follow behavior only applies inside a co-op run.
- Singleplayer is unaffected (uses your own settings as before).

## Scope / limitations

- **System cards** (curse/status/token/event/quest — Wound, Burn, etc.) are never blocked.
- Blocking is subtractive: it removes cards the game/another mod would have offered; it does not add
  other characters into the shop's native pools.
- A block is absolute: blocked cards are never passed back to fill out a screen. Where a source would
  run short, the mod reduces how many cards the game *asks for*. The settings panel enforces the one
  floor that needs — at least one card must stay allowed.
- **Your starting deck is not filtered.** Blocking Strike does not remove the Strikes you begin with;
  a character's opening deck is dealt straight from its definition and never passes through the pool.
- Card transformation is filtered too; when nothing allowed remains to transform into, the transform
  does nothing rather than producing a blocked card.
- Card-generation effects that create a *specific hardcoded* card (not drawn from a pool) are not
  affected.
- Unlocked-by-progression gating is the game's own system, not this mod.

## Author

inggom — a sibling mod of the STS2 Card Advisor toolset.
