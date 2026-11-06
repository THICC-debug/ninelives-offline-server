# ContentLoader for Nine Lives

**Version 1.2.0** · a mod for *Ninelives* 0.24.3 (offline) · includes a content editor

> [!NOTE]
> This mod was made with AI (Anthropic's Claude, in Claude Code) and tested by a human player.

Change the game's content with JSON **content packs**, with no code:

- **Items:** price and any of the ~240 stats (damage, armor, health, stack size…)
- **Loot:** what monsters and chests drop, and how often
- **Shops:** what each vendor sells

Edit packs by hand, or with the **content editor**, a simple page in your browser.

## Requirements

- *Ninelives* 0.24.3 patched with the
  [Nine Lives offline server](https://github.com/THICC-debug/ninelives-offline-server)'s
  `Patch Game.bat` (mod loader 2.1 or newer).

## Install

Unzip into your game folder (the one with `Ninelives.exe`). You get `Mods\ContentLoader.dll`,
the editor in `Mods\ContentLoader\editor\`, and `Mods\Content\example-content.json`. The example makes alchemy herbs cheaper and Gnolls drop more.
Delete the example if you don't want it.

## The content editor

You need [Python 3.8+](https://www.python.org/downloads/). Play once with ContentLoader installed
(enter the world) so it can export the game's data, then run:

```
python Mods\ContentLoader\editor\content_editor.py
```

It opens in your browser (`http://127.0.0.1:8096`).

- **Items:** every item's original price and stats, filterable by category (weapons, armor, herbs,
  potions…). **Change this item**, then set a new price, markup, stats (with plain names like
  "Weapon damage"), name or description. Blank fields keep the original. **Where it comes from**
  lists every loot table and shop that has it, including its variants (Fine, Rugged…).
- **Loot:** every loot table, labelled with the monsters that use it (as you visit zones). Shows the
  chest chance, how many items, what always drops, and each random group's chance in %.
  **Change this table** copies the original into your pack to edit. **Roll 10 kills** previews what
  the table drops, using the game's own rules.
- **Shops:** the same for every vendor, labelled by name (with [QuestLoader](../QuestLoader) installed).
- **Shared item groups** (at the bottom of the Loot and Shops lists): changing one changes every
  table that uses it.

**Pack info & changes** edits the pack's name, author and description, and lists everything it changes.
Press `/` to jump to search.

**Save pack** (Ctrl+S) writes `Mods\Content\<pack>.json`. In game, type `reloadcontent` in the
[DevTools](../DevTools) command box (F7) to see it without restarting.

## The exported reference files

A few seconds after you enter the world, ContentLoader writes the game's **original** data to
`Mods\ContentLoader\export\`. Use it to look things up and copy from:

| File | What's in it |
|---|---|
| `items.json` | Every item: name, price, and the stats that differ from a blank item |
| `loot.json` | Every loot table (drop id → what drops) and the shared item groups |
| `shops.json` | Every shop's stock (drop id = the shop NPC's id) |
| `monsters.json` | Which monster uses which loot table, added as you visit zones |
| `variants.json` | Item variants ("Fine Knife") and their base item |

## Content packs

Put `.json` files in `Mods\Content\`. Every part is optional. Packs load in name order, and later ones win.

```json
{
  "name": "my-content",
  "items": {
    "600000001": { "price": 1 },
    "100101000": { "stats": { "weaponDamage": 12 } }
  },
  "loot": {
    "drops": {
      "1000": { "dropRate": 0.66 }
    }
  },
  "shops": {
    "drops": {
      "1005": { "random": [ { "rate": 10, "items": [ { "id": 630000050, "count": 5 } ] } ] }
    }
  }
}
```

### Items

| Field | Notes |
|---|---|
| `price` | Base price. Shops sell for price × `buyPriceAmp` (usually 20) per item. |
| `buyPriceAmp` | Shop markup |
| `stats` | Any stat field by name, as listed in `export\items.json` (e.g. `weaponDamage`, `armor`, `health`, `stackMax`) |

### Loot and shops

A drop list (by drop id) can set:

| Field | Notes |
|---|---|
| `dropRate` | Chance (0–1) that a monster leaves a chest at all. Not used by shops. |
| `min`, `max` | How many items in total |
| `always` | Items that always come: `[{"id": …, "count": …}]`. **Replaces** the original list. |
| `random` | Weighted groups for the rest. **Replaces** the original list. Each group has a `rate` (weight) and either a shared `group` id or its own `items`. In random groups, `count` means "up to". |

Only the fields you give are changed. To keep a monster's loot and **add** to it, copy its
`random` list from `export\loot.json` and add your own group (the example does this for Gnolls).

`groups` (shared item groups, by id) can be replaced too. Every drop list that uses that group updates.

New drop ids are created, but a monster only uses the drop id it was built with (see `monsters.json`).

## Reload without restarting

With [DevTools](../DevTools), open the command box (F7) and type `reloadcontent`. The tables are
rebuilt from the game's originals and your packs applied again. Item stats update the next time an
item is shown. Shops restock with the new list when they next refresh.

## Known limits

- You can change existing items but not add brand-new ones: new items need art and models.
- Which monster uses which loot table is fixed by the game; you change what the table contains.

## Build from source

```
dotnet build -c Release              # build and install into the game
dotnet build -c Release -t:Package   # make release\ContentLoader-1.2.0.zip
```

License: MIT (code only; the game belongs to SmokymonkeyS).
