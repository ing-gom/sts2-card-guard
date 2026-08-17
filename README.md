# StS2 Content Filter (Cards / Relics / Potions / Events)

Control what your Slay the Spire 2 run is allowed to offer you — per character. Cards, relics, potions
and map events, all from one panel: keep other characters' cards and other mods' content out of your
rewards, shops, treasure rooms and in-combat generation, and keep the potions and events you don't want
off the table entirely.

![thumbnail](docs/thumb.png)

## Why

With character mods and content mods installed, foreign cards and relics can end up in your offers.
This mod lets you pick, per character, exactly what is allowed to show up. A four-tab panel
(**Cards** / **Relics** / **Potions** / **Events**) on the character-select screen drives it. Cards,
potions and events are blockable individually, base-game ones included — because "never offer me this
again" is as often about a potion or an event as it is about a card.

## Relics & Ancients (v0.4.0)

Blocking a mod on the **Relics** tab removes its relics from every run source (rewards, shops, treasure
rooms); relics granted directly — including from Ancient events — are substituted with a normal relic.
If a mod replaces the vanilla Ancient events with its own, blocking it restores that act's **original
vanilla Ancient** in place of the mod's, so its custom ancient (and everything it offers) is gone.
Base-game relics are never affected. Both card and relic filtering work in single-player and co-op
(networked runs use the host's settings).

## Potions & Events (v0.9.0)

**Potions.** Every random potion in the game — combat rewards, shop stock, potion events, Entropic Brew,
Phial Holster, Alchemize, the Crystal Sphere — is drawn from the same two pools (your character's own,
plus the shared pool), so blocking a potion removes it from all of them at once. Base-game potions
included; the **Potions** tab lists each pool with its potions and their icons.

One consequence is visible and intended: if you block a lot of potions, a merchant that would have
stocked three may stock fewer. The game clamps a draw to what is available rather than handing back
something you blocked.

**Block every potion and the routes close instead.** "No potions in this run" is a different request
from "fewer potions", so the sources stop existing rather than thinning out: no potion appears in any
reward set, the shop has no potion shelf at all (its slots are removed, not left empty), and relics or
cards that hand out potions — Phial Holster, Alchemical Coffer, Delicate Frond, Entropic Brew, Alchemize,
the Crystal Sphere's potion slot, Cauldron, Lost Coffer — simply do nothing. They still appear; they just
have nothing to give.

**Events.** Unchecking a map event keeps it off the map: each act walks a shuffled list of its events,
and the game already knows how to step past one it doesn't want, so a blocked event is skipped and the
act simply places the next event in its list. Base-game events included. The **Events** tab groups them
per act, plus the shared list every act draws from, plus any event-adding mods.

Two things to know about event scope:

- Events belong to the **map**, not to a player — in co-op both players walk into the same room — so a
  block set for a single character applies whenever that character is in the run. Use **★ All
  characters** if you never want to see it at all.
- **Ancient beings (Neow and friends) are not listed.** The game hands those out through a path this
  gate never sees. Blocking a *mod's* ancient still works, from the Relics tab (see above).

**Block every event and `?` map points stop becoming event rooms.** With no event left to place, a `?`
resolves to a fight, a shop or a treasure instead — through the game's own room-type hook, so the odds
table and its RNG behave exactly as they would have. Two events are unreachable from here by design:
Neow (a separate map point) and the two scripted `?` events a player's very first run always gets.

Blocking is a skip rather than a deletion, so it works on a run loaded from a save and reacts to
settings you change mid-run.

## How it works

Rather than patching individual card-generation effects, Card Guard filters the **candidate card
pool** that every card source draws from, before the RNG picks:

| System | Hook |
|---|---|
| Card rewards + events/treasures | `CardCreationOptions.GetPossibleCards` |
| In-combat generation ("add a random card") | `CardFactory.GetForCombat` / `GetDistinctForCombat` |
| Shop | `CardFactory.CreateForMerchant` |
| Cross-character event choices (Colorful Philosophers) | `ColorfulPhilosophers.GenerateInitialOptions` |
| Potions (rewards, shop, events, relics, cards) | `PotionPoolModel.GetUnlockedPotions` (every override, mod pools included) |
| Map events | `RoomSet.EnsureNextEventIsValid` |
| All potions blocked → routes closed | `RewardsSet.GenerateWithoutOffering`, `MerchantInventory.PopulatePotionEntries`, `PotionFactory.CreateRandomPotionsOutOfCombat` |
| All events blocked → no event rooms | `Hook.ModifyUnknownMapPointRoomTypes` |

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

The character-select screen gets a **Content Filter** button (bottom-right). It opens a full-screen
panel (no ModConfig dependency):

- **Left** — pick the scope you are configuring: a single character (base **and** custom), or
  **★ All characters**, which applies no matter who you play.
- **Tabs** — **Cards**, **Relics**, **Potions**, **Events**.
- **Right** — the packs for that scope and tab, each with an item count. On the Cards tab:
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
- **Potions** — icon on every row, and hovering shows the potion's own description.
- **Events** — a name says nothing about an event, so hovering shows the game's **event portrait and its
  opening paragraph** — the same text the event screen greets you with. Rendered through the game's own
  rich-text label, so tags like the gold highlight on a keyword come out coloured rather than printed.
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

- **Both players must have the mod, on the same version.** If a peer lacks it, or the host's config
  can't be confirmed, the filter **turns itself off for that run** rather than risk a desync — it never
  causes a disconnect, it just does nothing that run. The wire format carries a version, so a lobby
  mixing v0.9.0 with an older build also runs unfiltered rather than filtering differently on each side
  (an older peer would not know about the potion and event lists). Before v0.7.0 the config exchange
  lost a race against the joining client and this fallback triggered in every two-player lobby, so
  co-op filtering never actually engaged.
- Your saved settings are untouched; the host-follow behavior only applies inside a co-op run.
- Singleplayer is unaffected (uses your own settings as before).

## Scope / limitations

- **System cards** (curse/status/token/event/quest — Wound, Burn, etc.) are never blocked.
- **Ancient beings** (Neow and friends) are not on the Events tab — the game hands them out through a
  path the event gate never sees. A *mod's* ancient is still removable from the Relics tab.
- **Potions you already hold** are yours; blocking affects what the game rolls next, not your belt.
- Blocking many potions can leave a shop stocking fewer than three; that is the game clamping to what
  is available rather than the mod handing back something you blocked.
- With **every** potion blocked, potion-granting relics and cards stay in their pools and do nothing.
  They are not removed — a relic you can no longer use is the cost of banning what it produces.
- With **every** event blocked, Neow and a first-ever run's two scripted `?` events still happen; the
  game places those through a path the room-type hook never sees.
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
