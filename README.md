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
  colors, labels, the trail (show it, or clear it to start over), which character to track, and a manual map pick.
- Kills (yours and the ones on your Timers list) leave a tiny white dot at your last `/loc` before the kill;
  it fades out over 10 minutes.
- Drag the title bar to move it; it can go right up to a screen edge (Windows Snap doesn't grab it).
- 🔒 or **Ctrl+Alt+M** makes the map click-through so clicks reach EverQuest; Ctrl+Alt+M again unlocks it.
- EverQuest has to run in windowed mode for any overlay to show on top of it (full screen hides every other
  window). Set `WindowedMode=TRUE` in `eqclient.ini` and size the game window to fill the screen.
- Map files are read from the `maps` folder of your P1999 install (set in Settings). WhereEQ also ships a
  built-in set of P1999 maps, from [EqTool](https://github.com/smasherprog/EqTool) (MIT, see
  `src/WhereEQ/Data/Maps/NOTICE.md`), which is used when your folder lacks a zone or only has its label layers.

## Spawn timers

**⏱ Timers & buffs** opens a small, see-through, always-on-top window (like the map). Spawn timers are on top,
your [buffs](#buffs) below; drag the bar between them to give either one more room (all the way, to hide one).
The spawn timers list mobs and when they respawn:

- A timer starts whenever your log says `You have slain a fire beetle!`. Tick **Kills by others** to also time
  mobs that others or pets kill (`... has been slain by Somebody!`). A death you're too far away to see isn't
  in your log, so it can't be timed.
- Some kills have no "slain" line but still give experience or change faction. Those show as
  **faction death (Faction)** or **xp death** with a blue timer counting *up* from the kill (the mob, and so its
  respawn time, isn't known). Quest turn-ins (an NPC talking or paying you) are ignored.
- Respawn times are P1999's (from the P1999 wiki, via [EqTool](https://github.com/smasherprog/EqTool), MIT,
  `src/WhereEQ/Data/SpawnTimes/NOTICE.md`): the zone's usual timer, or a mob's own timer where one is known.
  Zones without data use 6:40, shown as `6:40?`. Timers on P1999 can vary, especially for rare spawns.
- Countdowns turn yellow in the last minute and green ("up") when the mob is due, then drop off after the
  **Keep up timers** time.
- In front of each timer: **✕** removes it, **🔔** beeps when the mob is 30 seconds from respawning. Both
  still work while the window is locked (click-through). Right-click a timer to restart it.
- The buttons run down the right edge. While unlocked, drag anywhere on the window to move it (it stays on
  screen, so the buttons can't end up out of reach); drag the corner to resize. ☰ adds zone,
  kill time, killer and timer length (and widens the window). 🔒 or **Ctrl+Alt+T** makes it click-through;
  Ctrl+Alt+T again unlocks it.
- ⚙ has the options (kills by others, how long "up" timers stay, backdrop darkness) and a box to type a mob
  name and a time (`22`, `6:40` or `8:00:00`): **Start timer** starts one by hand, **Save time** always uses
  that time for that mob in that zone.

## Buffs

The bottom part of the **⏱ Timers & buffs** window lists the spells on you, each with a countdown and a bar that
drains as it runs out. Green = buff, red = debuff on you.

- It works from the log the way EqTool does: each spell has a "lands on you" line (`Your skin turns hard as
  steel.`) and a "wears off" line (`Your skin returns to normal.`), both read from the game's own
  `spells_us.txt` in your EverQuest folder (found next to your inventory files; nothing to set up). Buffs from
  other players and from clicky items are tracked too. Dying clears the list.
- **Your spells on others** (blue): after `You begin casting X.`, the line saying X landed on someone
  (`A gnoll has been mesmerized.`) starts a timer on that target. It ends with `Your X spell has worn off.` or
  when the target dies. Turn it off in ⚙.
- **Weapon procs** (e.g. Boiling Blood from Nadrandi's Lance) have no "begin casting" line: when an item/weapon
  effect lands on the mob you've been meleeing in the last 10 seconds (`A froglok shin knight's blood boils.`),
  it's listed as yours on that mob. A new proc restarts the timer. A groupmate's identical proc on the same mob
  looks the same in the log.
- **Bard songs on mobs** (`A gnoll begins to chant.`, `…'s hair stands on end.`, `…is surrounded by chains of
  music.`): songs never log "You begin singing", but your own songs log `Your song ends.` in the same second, so a
  song landing right next to that line is yours. Each lasts about 18 seconds and restarts every time you sing it
  again. Some songs share a landing line (Tuyen's Chant of Flame and of Frost both say "begins to chant"); the
  tooltip names the other possibility.
- Times are estimates from the spell's duration formula: your own spells use your level; buffs from others assume
  the caster is at least your level (most buffs reach their cap well before 60). Some landing lines are shared by
  several spells (`You feel replenished.`); the tooltip says which others it could be.
- Shows whoever played most recently, or pick a character in ⚙ (Buffs section). Right-click a spell to remove it.

## Loot prices

The bottom part of the **⏱ Timers & buffs** window lists what your characters looted in the last two hours, newest
first, with each item's price from [pigparse.org](https://www.pigparse.org): the average asking price (WTS) in
auction chat over the last 30 days, or a longer stretch when there were no recent auctions. Prices at or above the
**Valuable loot** amount (Settings → Sounds) are gold, and the heading adds up the total. Hover a price for the
number of auctions, the wanted-to-buy (WTB) average and when it was last for sale; right-click to clear the list.

- Only your own loot counts (`--You have looted a Nephrite.--`), not groupmates'.
- WhereEQ sends pigparse only the item name and your server (from the log's file name, e.g. P1999Green); prices are
  remembered for an hour. Without internet, or for items pigparse has never seen, the price says "no price".
- These are asking prices from auction chat, not what items actually sold for.

## Sounds

Settings → **Sounds** reacts when a new line shows up in your log. Each alert can be **Off**, a **Beep** (each
has its own tone) or **Voice**: the voice built into Windows says a short phrase ("Tell from Somebody",
"Levitate ending", "Invis fading", "Stand up", "Low on food", "a fire beetle spawning in 30 seconds"). **▶** tries
it. Pick the voice, speed and volume there; **Read tells aloud** also reads the tell's text (off by default:
anyone in the room hears it). The voice works offline and needs nothing installed.

| Alert | Log line |
|---|---|
| Tell | a player sends you a tell (merchants, bankers and pets are ignored) |
| About to fall | "You feel as if you are about to fall." (levitate wearing off) |
| Invisibility fading | "You feel yourself starting to appear." / "Your shadows fade." |
| Illusion ending (off by default) | "You feel as if you are about to look like yourself again." |
| Stand up | "You must be standing to attack / cast a spell / …" |
| Group invite | "Somebody invites you to join a group." |
| Trade offered | "Somebody has offered you …" |
| Fizzle / missed note | "Your spell fizzles!" / "You miss a note, bringing your song to a close!" |
| Out of mana | "Insufficient Mana to cast this spell!" |
| Foraged | "You have scrounged up some food / water / …" |
| Charm landed | "… 's eyes glaze over." (see below for Bards) |
| Velious Armor Gems | you or a groupmate loots one of the Velious armor quest gems (Flawless Diamond, Flawed Sea Sapphire, Flawed Emerald, Pristine Emerald, Nephrite, Jaundice Gem, Black Marble, Chipped Onyx Sapphire, Flawed Topaz, and the Crushed Coral / Topaz / Black Marble / Flame Emerald / Lava Ruby / Opal / Onyx Sapphire / Flame Opal / Jaundice Gem); plays a two-note chime |
| Valuable loot | you loot something whose [pigparse.org](https://www.pigparse.org) price is at least the amount set under the voice options (500 platinum to start); the voice says "Valuable loot: Flawless Diamond, 1,117 platinum" |
| Food / drink low | "You are low on food / out of drink …" (at most once a minute) |

The 🔔 on a spawn timer has its own Off / Beep / Voice setting too.

**Bards:** a charm song lasts 18 seconds, so on a Bard "… 's eyes glaze over." starts an 18-second **charm: mob**
timer in the Timers window (opening it if needed) instead of the plain alert, with a short blip (or "Charm
ending") 4 seconds before it ends. Charming the same mob again restarts it. The class comes from the log
(`/who` yourself) or the class you set on the Characters tab.

## Download

Get **[WhereEQ-win-x64.zip](https://github.com/randaladams/WhereEQ/releases/download/latest/WhereEQ-win-x64.zip)**
(from the [latest release](https://github.com/randaladams/WhereEQ/releases/latest)), unzip it anywhere and run
`WhereEQ.exe`. Nothing else needs to be installed. Windows may warn that the app is unrecognized
(it isn't code-signed): choose **More info → Run anyway**.

Already installed? **[WhereEQ-update-win-x64.zip](https://github.com/randaladams/WhereEQ/releases/download/latest/WhereEQ-update-win-x64.zip)**
is the small download: only what changed since the previous build (usually just `WhereEQ.exe`; the release notes
list the files). Its `WhereEQ.exe` is a few MB because it uses Microsoft's **.NET 10 Desktop Runtime** instead of
carrying its own copy: install that once from https://dotnet.microsoft.com/download/dotnet/10.0 (".NET Desktop
Runtime", Windows x64). Close WhereEQ and unzip the update over your WhereEQ folder, replacing files. If you skipped
builds and one of them changed something in `Data`, use the full zip instead.

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

**Experience:** EverQuest doesn't log how far into a level you are, so type it: `$xp78` (or `$XP78`) in any chat
(`/say`, group, guild, a tell…) records that the character is 78% into their current level. The Characters tab
then shows `60 (78%)`; hover it for how much is left to the next level (60 is the top level on P1999). Only lines
your own character typed count, never someone else's `$xp`. After a level-up the old percent is dropped until you
type a new one.

## Building

Requires the .NET 10 SDK.

```
dotnet run --project src/WhereEQ
```

Every push to `main` is built by GitHub Actions and published as the `latest` release.
