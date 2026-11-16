# WhereEQ

A Windows dashboard for [Project 1999](https://www.project1999.com/) characters. It reads the files the
EverQuest client writes and keeps them up to date while you play.

- **Find item**: search every character's equipped gear, bags and bank at once.
- **Characters**: level, class, zone, bind point and free bag/bank space for each character.
- **Friends**: friends from all your characters' logs, with level, class and where they were last seen.
- **Spells**: spells each character is missing for their class and level (lists from the P1999 wiki).

## Map overlay

**🗺 Map** opens a transparent, always-on-top map of your current zone that can sit over the game:

- Your position, heading and recent trail come from `/loc` lines in the log, so bind `/loc` to a key or use a
  `/loc` macro. The map switches automatically when you zone.
- Mouse wheel zooms, drag pans, double-click fits the zone. ⚙ sets the backdrop darkness, map opacity, line
  colors, labels, which character to track, and a manual map pick.
- 🔒 or **Ctrl+Alt+M** makes the map click-through so clicks reach EverQuest; Ctrl+Alt+M again unlocks it.
- EverQuest has to run in windowed mode for any overlay to show on top of it.
- Map files are read from the `maps` folder of your P1999 install (set in Settings). WhereEQ also ships a
  built-in set of P1999 maps, from [EqTool](https://github.com/smasherprog/EqTool) (MIT, see
  `src/WhereEQ/Data/Maps/NOTICE.md`), which is used when your folder lacks a zone or only has its label layers.

## Download

Get **[WhereEQ-win-x64.zip](https://github.com/randaladams/WhereEQ/releases/download/latest/WhereEQ-win-x64.zip)**
(from the [latest release](https://github.com/randaladams/WhereEQ/releases/latest)), unzip it anywhere and run
`WhereEQ.exe`. Nothing else needs to be installed. Windows may warn that the app is unrecognized
(it isn't code-signed): choose **More info → Run anyway**.

## Setup

On first run the Settings window asks where your files are:

| Files | Created by (in game) | Usually in |
|---|---|---|
| `Name-Inventory.txt` | `/outputfile inventory` | EverQuest folder |
| `Name-Spellbook.txt` | `/outputfile spellbook` | EverQuest folder |
| `eqlog_Name_server.txt` | `/log on` | `EverQuest\Logs` |
| `Name_server.ini` | the client (friends list) | EverQuest folder |

From the logs WhereEQ picks up level and class (`/who` yourself, level-ups), zone, bind point
(`/char`, which logs "You are currently bound in: …", or binding) and when friends were last seen online (`/friends`, `/who`). The friends list
itself comes from each character's `.ini` file.

## Building

Requires the .NET 10 SDK.

```
dotnet run --project src/WhereEQ
```

Every push to `main` is built by GitHub Actions and published as the `latest` release.
