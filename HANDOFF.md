# WhereEQ — handoff

Native Windows dashboard + map overlay for **Project 1999 (Green)** characters. It reads the files the EverQuest
client writes and keeps them live while you play. This file is the starting point for a new session.

- Repo: https://github.com/randaladams/WhereEQ (public). Every push to `main` builds and publishes the
  **`latest`** release: https://github.com/randaladams/WhereEQ/releases/download/latest/WhereEQ-win-x64.zip
- The owner tests on a separate "test PC" that has P1999 installed; **EverQuest is not installed on the dev
  machine**, so features are verified with sample files + rendered snapshots (see *Verifying UI* below).
- Status at handoff: build **0.1.11** pushed (bundled maps fallback). Its GitHub build was still running when this
  was written; check https://github.com/randaladams/WhereEQ/actions.

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
- `Services/RecycleBin.cs` — `SHFileOperation` to send deleted character files to the Recycle Bin.

## Verifying UI without EverQuest

`WHEREEQ_SNAPSHOT="<png>|<what>|<arg>|<arg>"` makes the app render a window to PNG and exit
(`MainWindow.DevSnapshotAsync`). Examples: `out.png|2` (tab index; tabs are 0 Find, 1 Bags, 2 Characters,
3 Friends, 4 Spells), `out.png|4|Charname|missing`, `out.png|1|Charname|bank`, `out.png|settings`,
`out.png|sync`, `out.png|map`.

Map tests used a fake character: write a slice of a real log (zone entry + `/loc` lines) to
`Logs\eqlog_Testmap_P1999Green.txt`, temporarily set `MapsFolder` and `Map.Character` in `settings.json`
(back it up first), snapshot `|map`, then restore settings and delete the test log. Test logs must never be
committed (they're under the ignored `Logs/`).

Lesson learned: **don't restyle `DataGridRow`/`DataGridCell` with `BasedOn` the type key** — it picks up the
classic style and gives white rows in dark mode. Colour cell text via `ElementStyle`
(`BasedOn="{x:Static DataGridTextColumn.DefaultElementStyle}"`) or a template column instead.

## Open items / next steps

1. **Confirm the Skyshrine map fix on the test PC** (0.1.11). Root cause: the P1999 install's Skyshrine map file
   apparently has no wall lines; the bundled EqTool map is now used as a fallback. Not yet seen in-game.
2. Overlay not yet exercised in-game for: Ctrl+Alt+M hotkey, performance under heavy `/loc` spam, other zones,
   multi-monitor DPI. EverQuest must run **windowed** for any overlay to show.
3. Friends `.ini` format and the 100-friend cap are assumptions (no real `.ini` seen yet); sync hasn't been run
   against real files.
4. Guild join/leave message wording is a guess (none in the sample logs); `/who` of self is the reliable source.
5. Friends tab "Where" shows `/friends` short zone names (e.g. `ecommons`); could map via `ZoneNames.ToLong`.
6. Map ideas not built: show other characters' last known positions, z-level filtering for multi-level zones,
   per-zone remembered zoom.
7. `HANDOFF.md`/README should be updated when features change.
