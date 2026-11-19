WhereEQ - Project 1999 character dashboard
==========================================

Install
  Unzip this folder anywhere (e.g. C:\Games\WhereEQ) and run WhereEQ.exe.
  Nothing else to install. Keep the Data folder next to WhereEQ.exe (it holds the class spell lists).

First run
  The Settings window opens. Point it at:
    Inventory files  - where Name-Inventory.txt files are (/outputfile inventory writes to your EverQuest folder)
    Spellbook files  - where Name-Spellbook.txt files are (/outputfile spellbook, also the EverQuest folder)
    Log files        - your EverQuest\Logs folder (turn logging on in game with /log on)
  Each box shows how many matching files it found, so you can tell the path is right.
  If you point a box at the main EverQuest folder, tick "Also look in subfolders".

In game
  /outputfile inventory   - refresh a character's inventory/bank (WhereEQ updates within a second)
  /outputfile spellbook   - refresh a character's spellbook
  /who (yourself)         - records your level and class in the log
  /char                   - records your bind point in the log (binding also updates it)
  /friends                - records which friends are online, with level/class/zone
  /log on                 - logging must be on for level/class/zone/bind/friends, timers and buffs

Your settings are stored in %LocalAppData%\WhereEQ (settings.json, logstate.json).
