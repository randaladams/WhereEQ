# WhereEQ — handoff

Native Windows dashboard + map overlay for **Project 1999 (Green)** characters. It reads the files the EverQuest
client writes and keeps them live while you play. This file is the starting point for a new session.

- Repo: https://github.com/randaladams/WhereEQ (public). Every push to `main` builds and publishes the
  **`latest`** release: https://github.com/randaladams/WhereEQ/releases/download/latest/WhereEQ-win-x64.zip
- The owner tests on a separate "test PC" that has P1999 installed; **EverQuest is not installed on the dev
  machine**, so features are verified with sample files + rendered snapshots (see *Verifying UI* below).
- Status: build **0.1.30** (weapon procs on mobs, e.g. Boiling Blood, in the buffs list) after 0.1.29 (buffs
  moved into the Timers window, split by a draggable bar), 0.1.28 (buff
  tracking: spells on you and your spells on others), 0.1.27 ("Velious Armor Gems" loot alert), 0.1.26 (fizzle/mana/forage/charm alerts; Bard
  18 s charm timer), 0.1.25 (voice alerts:
  Off/Beep/Voice per alert), 0.1.24 (kill dots half size and white; spell-off/charmed/died/rampage/gate alerts removed at
  the owner's request, 8 alerts left) after 0.1.23 (log alerts with their own tones), 0.1.22 (arena line isn't a zone),
  0.1.21 (kill dots on the map; sounds for falling, stand up, tells), 0.1.20 (timers drag
  from anywhere, overlays kept on screen), 0.1.19 (faction/xp deaths
  as count-up timers), 0.1.18 (✕/🔔 work while locked; overlays
  drag without Windows Snap), 0.1.17 (timer ✕/🔔
  per row, side button strip, map trail show/clear), 0.1.16
  (timers as a small transparent overlay; first version 0.1.15) and 0.1.13 (bundled maps fallback; loc/heading on the map's zone line; Find item Reset
  button; full-screen hint in map options). Check https://github.com/randaladams/WhereEQ/actions after each push.

## Hard rules (privacy)

- **Never commit personal game data.** `Logs/`, `Inventory/`, `Spellbook/`, `*.ini`, root-level `*.txt` and
  `publish/` are gitignored. Logs contain private chat (tells, group chat).
- Before each commit, check staged files for character/player names from the sample data
  (`git grep --cached -iE "<names>"`); code comments use generic names (`Yourname`, `Somefriend`).
- Commits use the noreply identity configured in this repo (`git config user.email`), not a real email.

## Tech

- WPF, **.NET 10** (`net10.0-windows`), Fluent theme (`ThemeMode="System"`, follows Windows light/dark).
- No NuGet dependencies. Single project: `src/WhereEQ/WhereEQ.csproj`.
- Release = self-contained single-file `win-x64` exe + `Data\` folder (spell lists, maps). Workflow:
  `.github/workflows/build.yml` (version `0.1.<run_number>`, shown in the window title).
- Settings + log read-positions live in `%LocalAppData%\WhereEQ\` (`settings.json`, `logstate.json`).

```
dotnet run --project src/WhereEQ          # dev
dotnet build src/WhereEQ                  # close any running WhereEQ.exe first (exe is locked)
```

## Input files (all optional, each in its own configurable folder)

| File | Written by | Parsed in |
|---|---|---|
| `Name-Inventory.txt` | `/outputfile inventory` (EQ folder) | `Services/InventoryParser.cs` |
| `Name-Spellbook.txt` | `/outputfile spellbook` | `Services/SpellbookParser.cs` |
| `eqlog_Name_server.txt` | `/log on` (`EverQuest\Logs`) | `Services/LogScanner.cs` |
| `Name_server.ini` | the client (friends list) | `Services/CharacterIniParser.cs` |
| `maps\zone[_1.._3].txt` | map packs in `<P1999>\maps` | `Services/MapData.cs` |

Format notes learned from real data:
- Inventory: TSV `Location Name ID Count Slots`. Top-level `General1-8`, `Bank1-16` (P1999 uses **8** bank
  slots; setting `BankSlots`), `SharedBank*` (doesn't exist on P1999, ignored). A top-level row is a bag iff
  `<loc>-SlotN` rows exist for it. CRLF.
- Spellbook: `level<TAB>name` (names use backticks: `Selo`s`); compared with `'` normalised.
- Log lines: `[Tue Nov 10 15:16:42 2026] message`, CRLF, Latin-1. Scanner reads incrementally from a saved
  offset; `LogState.CurrentVersion` (now **5**) forces a one-time re-read when the parser learns new things.
  Extracted: level/class (`/who` of self `[51 Rake] Name (Halfling) <Guild>`, level-up), zone
  (`You have entered X.`), bind (`You are currently bound in: X` from `/char`; `You feel yourself bind to the
  area.` = current zone), guild (self `/who` tag; no tag = none), friends (`X is now/no longer your friend.`,
  `/friends` blocks), `/loc` + trail + sense-heading.
- `/loc` prints **Y, X, Z**; map files use **(-X, -Y)** (`MapData.FromLoc`). Verified: 10,645 of 10,647 logged
  East Commonlands locs fall inside the ecommons map with this transform.
- Seen in bigger logs (Shadow Knight 2.4M lines, Bard 1.7M): stray NUL bytes (harmless: such lines don't match
  the timestamp regex); until mid-2026 tells could also appear as `Somebody -> Yourname: text` (not used now);
  "You have entered an Arena (PvP) area." is not a zone change; bard song/DoT kills often have no "slain" line
  (corpse shows as `...'scorpse`), which the faction/xp deaths cover.
- `.ini` friends: `[Friends]` section, `FriendN=Name`; `UI_Name_server.ini` is ignored. Writing keeps other
  sections and makes `.whereeq.bak`.

## Code map

- `MainWindow.xaml(.cs)` — tabs: **Find item**, **Bags**, **Characters**, **Friends**, **Spells**; top bar has
  **🗺 Map** and **Settings…**. Also the dev snapshot hook (below).
- `SettingsWindow` — folders (inventory, spellbook, logs, character .ini, map files), server filter, bank slots,
  always-on-top. Shows how many matching files each folder has.
- `SyncFriendsWindow` — preview + write a combined friends list into each character's `.ini`.
- `MapWindow.xaml(.cs)` + `MapView.cs` — transparent always-on-top overlay. Click-through lock via
  `WS_EX_TRANSPARENT`, global hotkey **Ctrl+Alt+M** (`RegisterHotKey`). `MapView` draws walls as frozen
  geometry per colour, labels, trail (breaks on jumps > 300 units), player dot + heading arrow.
- `Services/DataStore.cs` — finds/watches files (FileSystemWatcher, debounced, ≤ ~1 s even for busy logs),
  merges everything into `Models/Character`. Raises `Changed` (rebuild tabs) vs `PositionChanged` (only `/loc`
  or chatter → refresh map only, so grids don't reset scroll every second).
- `Services/ZoneNames.cs` — log zone name → map short name (Classic–Velious), checked against all zones in the
  sample logs.
- `Services/MapData.cs` — map loader; `LoadBest` = user's map if it has walls, else bundled map.
- `Services/SpellDatabase.cs` + `Data/Spells/<Class>.txt` — class spell lists scraped from the P1999 wiki
  (`level<TAB>name<TAB>era`).
- `Data/Maps/*.txt` — 429 P1999 map files bundled from EqTool (MIT, `Data/Maps/NOTICE.md`).
- `TimersWindow.xaml(.cs)` — spawn timers overlay (transparent like the map; compact = countdown + mob, ☰ =
  details and wider; buttons in a strip on the right; per-row ✕ remove and 🔔 alert (beeps 30 s before, keys in
  `Settings.Timers.Alerts`; while locked a 40 ms cursor poll drops `WS_EX_TRANSPARENT` only over those
  buttons, so they stay clickable); click-through lock with **Ctrl+Alt+T**; snapshot `|timers|details` / `|timers|options`). Kills come from `LogState.Kills` (`LogScanner.TryParseKill`:
  "You have slain X!" / "X has been slain by Y!", skipping chat, player deaths and Eye of Zomm). Kills arrive via
  `PositionChanged` (not in `Signature`), so tabs don't rebuild on every kill. Same mob + zone within 5 s in two
  logs = one kill. Experience/faction lines with no matching "slain" line within 2 s become unnamed deaths
  (`KillRecord.Kind` "xp"/"faction", count-up timers; `LogScanner.FlushPending`); a group with "You receive … from
  <NPC>" or a multi-word speaker "says" is a quest turn-in and is skipped. Manual timers, removed rows, "Clear all" and saved times live in `Settings.Timers`.
- `Services/SpawnTimes.cs` + `Data/SpawnTimes/spawntimes.txt` — respawn times generated from EqTool's
  `EQToolShared/Zones.cs` by `tools/make_spawn_times.py` (`zone<TAB>*|=Name|~contains<TAB>h:mm:ss`).
- `Services/Speech.cs` — Windows voice via SAPI COM late binding (`SAPI.SpVoice`, `dynamic`; no package, works in
  the single-file publish). Runs on the UI thread, async queue, 3 s duplicate drop. `Sounds.Alert` /
  `Sounds.TimerAlert` route each alert by `SoundSettings.Modes` (Off/Beep/Voice); `Sounds.Phrase` builds the
  spoken text. Dev hook `out.txt|voices` lists voices and speaks silently (volume 0), writing "speak: ok".
- Gem alert: `Sounds.VeliousGems` (owner's list) matched on the whole looted item name (`LogScanner.LootedItem`),
  so "Crushed Nephrite" ≠ "Nephrite"; fires for your loot and groupmates' ("--Somebody has looted a …--").
- Bard charm: `LogScanner.Notice` passes the log's class; `MainWindow.StartBardCharm` adds a `ManualTimer`
  (Kind "charm", 18 s, `AlertSeconds` 4, `KeepSeconds` 10) and opens Timers, which owns the alert tick. The log
  doesn't say who cast a charm (other players' charms show the same line), so non-Bards just get the alert.
- `Services/Sounds.cs` — `Sounds.Alerts` is the one table of log alerts (label, description, default); Settings
  builds its rows from it. Beep patterns per `Sound` (same names as `LogNotice`), off the UI thread, 2 s throttle
  (food 60 s). `LogScanner.Classify(msg)` decides the alert; `LogScanner.Notice` fires for fresh lines (stamped
  < 20 s ago) only. Tell filter drops multi-word speakers and merchant/banker/pet phrases (`NpcTell`).
  `Settings.Sounds`: Fall/Stand/Tell bools (0.1.21) + `On` dictionary for the rest. Dev hook
  `WHEREEQ_SNAPSHOT="out.txt|classify|<log>"` writes alert counts for a log.
- Kill markers: `KillRecord.X/Y` = last `/loc` in the same zone ≤ 10 min before the kill; `MapWindow.RecentKills`
  draws kills from all logs in the map's zone for 10 minutes (15 s refresh timer so they fade).
- `WindowDrag.cs` — moves the overlays by hand instead of `DragMove`, so Windows Snap doesn't maximize/tile
  them at screen edges, and clamps them to the screen (also on open). Timers: drag from anywhere while
  unlocked (`anywhere: true`, 4 px threshold so clicks still select rows; buttons/text boxes/sliders excluded).
- `BuffsPanel.xaml(.cs)` + `Services/SpellEffects.cs` — buff tracker (0.1.28; own window with Ctrl+Alt+B in
  0.1.28, since 0.1.29 a UserControl in the bottom row of `TimersWindow`). A `GridSplitter` between the rows sets
  `Settings.Timers.BuffsShare` (0..1, star heights; null = first time, window grows to 360 high). Buff options
  (character, spells on others) are in the Timers ⚙ panel; `Settings.Buffs.Open` only migrates 0.1.28 users (opens
  Timers once). Lock = Ctrl+Alt+T for both. Snapshot `|timers` / `|timers|options` shows both. `SpellEffects` reads the client's `spells_us.txt`
  (EverQuest folder, found from the inventory/ini/log folders and their parents; field layout as in EqTool:
  1 name, 6 lands-on-you, 7 lands-on-other, 8 wears-off, 13 cast ms, 16/17 duration formula/ticks, 83 beneficial,
  104–117 class levels Warrior..Enchanter). Duplicate names: later row wins unless it has no classes (EqTool rule).
  `LogScanner.TrackSpells` (only lines < 3 h old) keeps `LogState.Spells` (`ActiveSpell`, persisted): a
  lands-on-you line adds a buff on you (ambiguous lines resolved by `SpellEffects.Pick`: the spell being cast, then
  your class at/below your level, then closest level); its wears-off line removes it; "You begin casting X." +
  X's lands-on-other line within cast time + 10 s adds a timer on that target (item clicks were seen landing
  10 s after a 5 s cast); "Your X spell has worn off." or the target's death removes it; weapon procs (0.1.30: `SpellInfo.IsProc` = cast
  time 0 and no class levels, indexed by lands-on-other text) count as yours when the line starts with
  `LogState.MeleeTarget` (last "You pierce X for N points of damage." / "You try to slash X, but miss!", ≤ 10 s); your death clears your
  buffs. Durations: `SpellInfo.DurationSeconds` (EqTool's formula table); own spells use your level, others' assume
  max(your level, spell's level). Buff changes don't touch `Signature`, so they ride `PositionChanged`.
- `Services/RecycleBin.cs` — `SHFileOperation` to send deleted character files to the Recycle Bin.

## Verifying UI without EverQuest

`WHEREEQ_SNAPSHOT="<png>|<what>|<arg>|<arg>"` makes the app render a window to PNG and exit
(`MainWindow.DevSnapshotAsync`). Examples: `out.png|2` (tab index; tabs are 0 Find, 1 Bags, 2 Characters,
3 Friends, 4 Spells), `out.png|4|Charname|missing`, `out.png|1|Charname|bank`, `out.png|settings`,
`out.png|sync`, `out.png|map`, `out.png|timers` (`|timers|details`, `|timers|options`), `out.png|map|options`,
`out.png|buffs` (`|buffs|options`).

`WHEREEQ_APPDATA=<folder>` makes the app use that folder for `settings.json`/`logstate.json` instead of
`%LocalAppData%\WhereEQ`, so tests don't touch the real settings (the owner may have WhereEQ running). Buff test
used: a slice of a real log with timestamps shifted to end ~20 s ago, in a scratch `Logs` folder, plus a test
`settings.json` with `InventoryFolder` = the P1999 folder (for `spells_us.txt`) and `LogFolder` = the scratch folder.

Map tests used a fake character: write a slice of a real log (zone entry + `/loc` lines) to
`Logs\eqlog_Testmap_P1999Green.txt`, temporarily set `MapsFolder` and `Map.Character` in `settings.json`
(back it up first), snapshot `|map`, then restore settings and delete the test log. Test logs must never be
committed (they're under the ignored `Logs/`).

Lesson learned: **don't restyle `DataGridRow`/`DataGridCell` with `BasedOn` the type key** — it picks up the
classic style and gives white rows in dark mode. Colour cell text via `ElementStyle`
(`BasedOn="{x:Static DataGridTextColumn.DefaultElementStyle}"`) or a template column instead.

## Open items / next steps

Confirmed working in-game by the owner (2026-11-18): Skyshrine map fallback, spawn timers (beep, locked ✕/🔔,
snap-free drag), overlay hotkeys/performance, friends `.ini` sync. EverQuest must run **windowed** for overlays.

1. **Buffs are new** (0.1.28, in the Timers window since 0.1.29): checked only against real log slices + snapshots, not live in-game. Ideas not
   built: alert when a buff on you is about to fade, spell icons (EqTool reads `uifiles\default\spells0N.tga`),
   tracking other players' buffs you didn't cast. Bard songs refresh on every pulse (no "begin casting" line for
   songs in the logs seen); offline time counts down like play time.
2. Shelved by the owner for now: guild join/leave wording (a guess; `/who` of self is reliable), Friends tab short
   zone names (could map via `ZoneNames.ToLong`), map ideas (other characters' last positions, z-level filtering,
   per-zone zoom).
3. `HANDOFF.md`/README should be updated when features change.
