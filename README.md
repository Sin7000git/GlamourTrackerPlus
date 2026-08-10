# Glamour Tracker+

Tracks what’s in your glamour dresser and armoire, see which pieces are missing from your Outfit sets, convenient Fashion Report cheat sheet based on FashionReportXIV.com, randomize glamour plates — a native in-game UI that fits alongside FFXIV’s own windows.

**Author:** Sin7000

---

## Features

### Overview

A quick snapshot of dresser usage, Outfit set progress, and Fashion Report status. Open Fashion Report from here when you need the full weekly board.

### Outfit sets

Browse official Outfit sets and see which pieces you already have in the dresser or armoire (and which you’re still missing). Filter by category and storage, sort the list, and drill into a set for per-piece details and acquisition tips where available.

### Fashion Report

Weekly Fashion Report helpers in a dedicated window: slot themes, recommended items, ownership from your dresser/armoire, materials, and links out when you want more detail. Also available via `/glamplus fashion` (or `fr` / `report`). When you talk to the Masked Rose for judging, the plugin can warn you if no VIP Card / Jackpot III MGP bonus is active yet (Settings → Fashion Report).

### Plate randomize

Open **Edit Glamour Plates** at a dresser, then use the floating controls above the plate editor to roll a full plate — or reroll individual slots. Filters (job, level, dresser/armoire sources, slot locks) live on that same overlay.

### Item tooltips & GC delivery

Optional markers on item tooltips and Grand Company expert delivery lists show whether something lives in your dresser or armoire, so you can avoid turning in glam pieces by mistake.

### Settings

Toggle tooltip icons, GC markers, the Fashion Report MGP-buff reminder, the plate-editor overlay, and other preferences from the Settings tab.

---

## Getting started

1. Install and enable the plugin in Dalamud.
2. Open your **glamour dresser** and/or **armoire** once so ownership can sync.
3. Run `/glamplus` for the main UI.

If counts look stale after moving a lot of gear, use `/glamplus refresh`.

---

## Commands


| Command             | What it does                       |
| ------------------- | ---------------------------------- |
| `/glamplus`         | Main UI (`open`, `ui`)             |
| `/glamplus fashion` | Fashion Report (`fr`, `report`)    |
| `/glamplus refresh` | Force-refresh dresser/armoire data |
| `/glamplus help`    | Full list of aliases (`?`)         |


Type `/glamplus help` in chat for every accepted alias.

---

## Tips

- **Plate randomize** needs the plate editor open at a dresser. Slot reload icons on the overlay reroll one piece at a time.
- **Tooltip / GC icons** learn the dresser & armoire textures the first time you hover a relevant item; leave that option on if you use GC delivery a lot.
- Fashion Report ownership updates when your dresser/armoire index changes, or when you refresh.

---

## Credits

Native UI is built with **[KamiToolKit](https://github.com/MidoriKami/KamiToolKit)** by **[MidoriKami](https://github.com/MidoriKami)** — thank you for making native Dalamud UIs practical.