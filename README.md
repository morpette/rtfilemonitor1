# rtfilemonitor1
Real-time file monitor (vibe coded using Codex)

This version of the app is built for Windows 11.
The app is written in C# with .NET and WPF.

## Run the portable app

Extract the Windows x64 release ZIP to a writable folder and double-click
`RtFileMonitor.exe`. The build includes the .NET runtime. The app saves your two
folder choices in `settings.json` alongside the executable.

- **.codex folder** shows updates in the selected folder and its subfolders.
- **JSONL sessions** watches for new or moved-in `.jsonl` files, including those
  in dated subfolders. Select a session to search or filter its timeline and
  expand a row to inspect its original JSON. Keep the app open while monitoring.
- **SQLite activity** opens a database read-only and monitors one selected table
  at a time. It shows added, updated, and removed row counts between snapshots
  while keeping row values hidden. Without a usable primary key, edits appear
  as added and removed rows. Switching tables clears the activity list.

## Build

With the .NET 10 SDK on Windows, run `./publish.ps1` in PowerShell. The next ZIP
is `dist/RtFileMonitor-win-x64-v3.zip`. Each successful build uses the next
number after the highest versioned ZIP in `dist`, so keep the latest ZIP there
to preserve the sequence.
