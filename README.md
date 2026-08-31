<p align="center">
  <img src="EnemyFinder/images/icon.png" width="128" height="128" alt="Enemy Finder">
</p>

<h1 align="center">Enemy Finder</h1>

<p align="center">A Dalamud plugin that shows enemy and FATE spawn areas on the native in-game map.</p>

**Beta** (`0.1.0.0`). Install from the Dalamud plugin installer after adding the custom repository below. This is not a live enemy radar — it looks up standing spawn camps (and optionally FATE camps) and draws gathering-style circles on the map.

## Install

1. In game, open Dalamud settings: `/xlsettings`
2. Open the **Experimental** tab
3. Under **Custom Plugin Repositories**, paste this URL into an empty field:

   ```
   https://raw.githubusercontent.com/aetherwrights/enemy-finder/main/pluginmaster.json
   ```

4. Click the **+** button, then the **Save** icon
5. Open the plugin installer: `/xlplugins`
6. Search for **Enemy Finder** and install it

To update later, use the installer’s update list (or reinstall). The same repository URL is used for updates.

This is a custom (third-party) repository, not the official Dalamud plugin list. You can also build from source and add `EnemyFinder/bin/Debug/EnemyFinder.dll` under **Experimental → Dev Plugins**.

## Commands

| Command | Action |
| --- | --- |
| `/efind` | Open the plugin window |
| `/efind Name` | Look up that name and open the map |

Name lookup tries an enemy first, then a FATE if no enemy spawn is found.

## Click sources

Click an entry in a supported book to open its spawn on the map. Each source can be turned off if it conflicts with another plugin.

| Source | Default | Notes |
| --- | --- | --- |
| Hunting log name clicks | On | Click the **name**, not the icon, kill count, or empty space |
| Relic book enemy clicks | On | Trials of the Braves enemies tab |
| Relic book FATE clicks | On | Trials of the Braves FATE tab |
| Other enemy books | On | Bozja field records |

## Other settings

| Setting | Default | Notes |
| --- | --- | --- |
| Include FATE camps | Off | When looking up an **enemy**, also draw FATE camps for that enemy on the same map. Separate from relic-book FATE clicks. |
| Circle radius | 40 yalms | 15–80 |
| Wiki cache size | 32 | 0–64 in-memory lookups. Cleared on unload, or with **Clear cache**. |
| History | Last 20 names | Click a row to look it up again. |

## Spawn lookup

- Gamer Escape and Console Games Wiki, in parallel
- Standing overworld camps by default; quest-only rows are skipped
- When several zones are listed, prefers the higher-level standing camp
- Relic-book FATEs use wiki coordinates (`prev-fate` / required status)
- If a FATE needs another FATE first, a chain window lists them in order so you can show the prerequisite or the later FATE

## Out of scope

Live nearby-enemy overlay / ObjectTable radar.
