# Galactic Tune-Up — Application Technical Documentation

> A Windows maintenance tool: a WPF desktop app that talks over a named pipe to a background Windows service, which does the work needing administrator rights.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [How to Launch](#how-to-launch)
3. [Dashboard](#1-dashboard)
4. [System Cleaner](#2-system-cleaner)
5. [Registry Guard](#3-registry-guard)
6. [Component Repair](#4-component-repair)
7. [Live Monitor](#5-live-monitor)
8. [Settings](#6-settings)
9. [Gaming / OS Tweaks](#7-gamingas-tweaks)
10. [Windows Resources](#8-windows-resources)
11. [Storage Tools](#9-storage-tools)
12. [Security & Safety Model](#security--safety-model)
13. [File & Folder Structure](#file--folder-structure)

---

## Architecture Overview

**Galactic Tune-Up** is split into two separate processes that communicate in real-time:

```
┌──────────────────────────────┐        Named Pipe IPC         ┌──────────────────────────────┐
│         TuneUpUI.exe         │  ◄──── "TuneUpEnginePipe" ───►│       TuneUpEngine.exe       │
│        (WPF Frontend)        │        JSON over stream       │      (Elevated Backend)      │
│     Runs as standard user    │                               │     Runs as Administrator    │
└──────────────────────────────┘                               └──────────────────────────────┘
```

| Component | Role |
|---|---|
| **TuneUpUI** | The visual WPF desktop application the user interacts with. Runs at standard user privileges. |
| **TuneUpEngine** | The headless background service that performs all system-level work. Runs elevated as Administrator to perform memory management, registry writes, and system component repairs. |
| **Named Pipe (`TuneUpEnginePipe`)** | Secure bidirectional JSON communication channel between the two processes. Supports streaming progress updates so the UI stays responsive during long operations. |

---

## How to Launch

**Installed copy:** the installer registers the engine as an automatically starting Windows service (**TuneUp Engine**, `TuneUpEngineService`). Just open **Galactic Tune-Up** — there is no UAC prompt, and the engine keeps working in the background after you close the window.

1. `TuneUpUI.exe` starts as you (never elevated).
2. It connects to the engine's pipe, first checking that the pipe belongs to the genuine `TuneUpEngine.exe` installed next to it.
3. If the service is stopped, the UI starts it (signed-in users are allowed to start it; stopping or changing it needs an administrator).
4. While open (including minimised to the tray), the UI tells the engine which program is in the foreground each second — a service can't see your desktop, and Game Mode / ProBalance need to know.

**Development build:** run `TuneUpUI.exe` from its build folder (or `Start-TuneUp.bat`). With no service installed, the UI falls back to launching the engine itself with a UAC prompt, and stops it again when the UI exits.

### Per-user work and administrator rights

The engine is shared by everyone signed in to the PC, so each request is handled for the user who made it:

- **Your settings and files:** per-user tweaks go to *your* registry hive, scans use *your* folders, and files inside your profile are deleted **as you** — Windows itself guarantees nothing is removed that you couldn't remove yourself.
- **Machine-wide changes** (system tweaks, services, MSI, network, Game Mode/ProBalance switches, SFC/DISM, migration, cleaning Windows logs) require an **administrator account**. A standard user can still scan, clean their own files and change their own per-user tweaks.

---

## 1. Dashboard

**Navigation label:** Dashboard  
**Tab index:** 0

The Dashboard is the home screen and provides a live overview of system health, refreshed every 3 seconds.

### Memory Health Panel

| Metric | Description |
|---|---|
| **Free RAM %** | Percentage of physical RAM currently available to applications. |
| **Free RAM (GB)** | Available vs. total physical memory in gigabytes. |
| **Standby List Size** | The real size of Windows' standby cache (recently used file data kept in RAM), read from Windows' page lists — the same figure RAMMap shows. Shows "n/a" if it can't be read. A large standby list is normal and healthy. |
| **Total Memory Freed** | Cumulative amount of RAM recovered by all standby list purges since the engine started. |

### Purge Standby Memory Button

Manually triggers an immediate flush of the Windows Standby List using the `NtSetSystemInformation` low-level API. This reclaims memory that Windows is holding in cache for potentially-needed data, making it available to running applications instantly. Useful before launching a memory-intensive game or application.

### Feature Toggles

| Toggle | What it controls |
|---|---|
| **ProBalance Monitoring** | Enables the ProBalance Governor — an automated background process that detects when non-foreground processes are consuming excessive CPU and temporarily lowers their priority class, giving the active foreground application more CPU time. Priority is restored the moment the offending process becomes the foreground window. |
| **Standby Auto-Purge** | When enabled, the engine checks every 5 seconds and empties the standby list only when truly free memory (free + zeroed pages) is below 10% of RAM **and** the standby cache holds at least 10% of RAM — at most once a minute. "Freed" figures are the measured shrink of the standby list. |
| **Game Mode Booster** | When enabled, the engine monitors the active foreground window every second. If the active process is identified as a game (by executable name whitelist or install path heuristics), it receives High process priority. On hybrid CPUs (performance + efficiency cores) with at least three performance cores it is kept on the performance cores; on other CPUs it is not pinned, per Microsoft's guidance for games. When the game loses focus, its priority (and affinity) is restored. |

### Event Log

A scrolling list of all recent actions performed by the engine (throttle events, memory purges, game boosts), timestamped to the second.

---

## 2. System Cleaner

**Navigation label:** System Cleaner  
**Tab index:** 1

A two-tier junk file scanner and cleaner. The primary scanner is powered by a built-in rule set (`winapp2.ini` format). The advanced scanner adds user-folder-specific duplicate file and empty folder detection.

### Scan Options (Header Toggles)

| Toggle | Effect |
|---|---|
| **Scan Empty User Folders** | After the main scan, recursively walks your user folders (Documents, Downloads, Desktop, Pictures, Videos, Music, OneDrive) and identifies every subdirectory that contains no files or non-empty subdirectories. The root folders themselves are never flagged — only their children. |
| **Scan Duplicate User Files** | After the main scan, indexes the files in your Documents, Pictures, Videos, Music, Desktop, Downloads and OneDrive folders by file size, pre-filters candidates on a SHA-256 of the first 1 MB, then confirms true byte-for-byte duplicates with a full-file SHA-256. Cloud-only (online) files are skipped so they are never downloaded. Reports each duplicate group with the space recovered by keeping only one copy — cleaning a set always keeps the first copy listed. |

### Select All / Deselect All

Buttons in the top-right of the header. Toggle the selection checkbox for every row in the results list at once.

### Results List

Each row in the results table represents one **clean rule** (a named set of files to delete).

| Column | Description |
|---|---|
| **Select** | Checkbox — only checked rows will be deleted when you click "Empty Junk Files". All rows start **unchecked** by default — you must explicitly select what to clean. |
| **Category** | The type of junk: `System`, `Empty Folders`, or `Duplicate Files`. |
| **Cleaner Target** | The rule name (e.g., "Windows Temporary Files", "Duplicate Set 3-1 (115 KB)"). |
| **Size** | Total recoverable disk space for this rule group (cyan text). |
| **Files Found** | Number of individual file paths matched by this rule. |

### File Inspection Panel

Click any row to reveal a detail panel below the list. This panel shows every individual file or folder path belonging to that rule. **Click any path** to open its exact location in Windows Explorer (files are highlighted with `/select`; folders are opened directly). Use this to verify a file before deciding to delete it.

### Scan for Junk Files

Initiates the scan sequence:
1. The primary Winapp2 rule engine scans system paths for cached, temporary, and log files.
2. If "Scan Empty User Folders" is checked, the advanced scanner runs next.
3. If "Scan Duplicate User Files" is checked, the duplicate hasher runs last.
4. All results are merged into a single list for review.

### Empty Junk Files

Deletes only the rows with their checkbox **checked**. Progress is streamed back from the engine in real-time. A summary dialog reports total megabytes recovered when complete.

---

## 3. Registry Guard

**Navigation label:** Registry Guard  
**Tab index:** 2

Scans the Windows Registry for structural defects — keys that reference file system paths, COM objects, or application entries that no longer exist. This is different from "registry cleaners" that delete arbitrary keys; TuneUp only targets provably broken references.

### What it Scans

| Category | Description |
|---|---|
| **Uninstall entries** | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` — entries whose `InstallLocation` path no longer exists on disk. |
| **App Paths** | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths` — entries pointing to executables that no longer exist. |
| **Run keys** | `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` — startup entries pointing to missing executables. |
| **Shared DLLs** | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs` — references to DLL files that have been removed. |

### Results List

| Column | Description |
|---|---|
| **Select** | Checkbox to include/exclude this issue from repair. |
| **Issue Title** | Human-readable description of the broken key. |
| **Registry Location** | The full registry key path. |
| **Target Path (Missing)** | The file system path that is no longer present (shown in red). |

### Scan Registry

Runs the structural integrity scan. Broken keys are listed in the results table with their exact registry path and the missing target.

### Repair Registry

For each selected issue, the engine:
1. Creates an automatic backup of the key to `%APPDATA%\GalacticTuneUp\RegistryBackups\` with a timestamp.
2. Deletes the orphaned registry key.

A confirmation dialog reports how many keys were repaired. The backup folder can be used to manually restore any key if needed.

---

## 4. Component Repair

**Navigation label:** Component Repair  
**Tab index:** 3

Analyzes Windows Component-Based Servicing (CBS) logs to detect system file corruption, then triggers automated repair using Windows' built-in tools.

### Scan System Integrity

Reads the Windows CBS log file (`C:\Windows\Logs\CBS\CBS.log`) and extracts entries tagged as corruption or repair failures. Results are displayed in a scrollable list showing exactly which components have reported errors.

- **No errors found:** The log shows no recent corruption events — system files are healthy.
- **Errors detected:** The list shows the specific CBS entries indicating corrupted or unrepairable components.

### Baseline SFC / DISM Repair

If corruption is detected, click this button to run a two-phase system repair:

| Phase | Tool | What it does |
|---|---|---|
| **1** | `DISM /RestoreHealth` | Contacts Windows Update to download and restore the Windows component store (WinSxS), fixing the source files that SFC uses. |
| **2** | `sfc /scannow` | System File Checker — scans all protected system files and replaces corrupted ones from the now-repaired component store. |

The result shown afterwards is the real outcome: DISM's exit code (0 = healthy/repaired, 3010 = repaired, restart needed) and SFC's verdict (no violations / repaired / could not repair everything / could not run).

---

## 5. Live Monitor

**Navigation label:** Live Monitor  
**Tab index:** 4

### Driver DPC & Interrupt Load

Checks whether drivers are holding CPU cores long enough to cause game stutter or audio crackles.

| Control | What it does |
|---|---|
| **Start Audit** | Starts two measurements in the engine. Results appear after 5 seconds and refresh every 2 seconds. |
| **Stop** | Ends the measurement. The last results stay on screen and can still be saved. |
| **Save Trace** | Saves the current results as a **text report** (summary, driver table, core table, advice for ELEVATED/HIGH drivers) or a **CSV** (one row per driver and core). Pick the type in the save window's "Save as type" list. The file holds no computer or user name. |
| **What is this, and how do I fix it?** | Shows plain-English help in place of the tables; **Back to the results** returns. |

- **CPU core table**: each core's share of time spent on DPC and interrupt work, from Windows performance counters, as the highest 5-second average (the counters move in 15.6 ms clock-tick steps, so single readings are too coarse). OK under 3%, ELEVATED 3 to 10%, HIGH 10% or more.
- **Driver table**: a real-time kernel ETW session (DPC, interrupt and image-load events, under its own session name so LatencyMon or WPR can still run) times every DPC and interrupt run and charges it to the driver whose code ran. Columns: driver file, what it is, longest single run (µs), number of runs, total ms, status. Status goes by the longest single run: OK under 500 µs, ELEVATED from 500 µs, HIGH from 2,000 µs. Hover over a driver for advice; the advice for the worst problem driver also shows under the table. If the trace can't start, the table says why and the core table keeps working.

### Event Log

A real-time scrolling event log showing all actions the engine has taken since it started. This is the same event log shown in the Dashboard but displayed full-screen for easier reading.

Events are categorized by source:
- `[ProBalance]` — Process priority throttle/restore events
- `[Memory]` — Standby list auto-purge events with MB freed
- `[GameMode]` — Game detection boost/restore events
- `[Engine]` — Informational engine status messages
- `[Downloader]` — Definition update events
- `[LatencyAuditor]` — audit start/stop, driver trace start (with how many drivers it found) or the reason it couldn't start, and CPU cores spending 10%+ of their time on driver DPCs/interrupts

The log retains the most recent 100 entries, rotating out older ones automatically.

---

## 6. Settings

**Navigation label:** Settings gear icon (top-right title bar)  
**Tab index:** 5

Persistent application preferences, saved to `%LOCALAPPDATA%\GalacticTuneUp\settings.json`.

### Behavior Settings

| Setting | Description |
|---|---|
| **Start on Windows Startup** | Adds TuneUpUI to `HKCU\...\Run` with the `--minimized` flag so it launches silently in the background when you log in. |
| **Launch UI Minimized** | When TuneUp starts, immediately hide the main window and run from the system tray only. |
| **Minimize to Tray on Close** | When you click the ✕ button, hide to the system tray instead of exiting. The engine keeps running. Right-click the tray icon → Exit to fully quit. |

---

## 7. Gaming / OS Tweaks

**Navigation label:** Gaming/OS Tweaks  
**Tab index:** 6

Three categories of extensive Windows configurations: System Tweaks, Desktop Tweaks, and Windows Appearance Tweaks, accompanied by Message Signaled Interrupts (MSI) hardware interrupt provisioning.

### Message Signaled Interrupts (MSI Mode)

| Button | What it does |
|---|---|
| **Auto-Search Compliance** | Scans all PCI/PCIe devices via the Windows registry and reports which are already using Message Signaled Interrupts vs. legacy line-based IRQs. Devices eligible for MSI forcing are listed. |
| **Force MSI Tweak** | Writes MSI mode override entries to the registry for each compliant device found. Requires a system reboot to take effect. MSI avoids interrupt sharing for devices still on legacy line-based interrupts. Most current devices already use MSI, so this rarely changes DPC latency; use the Live Monitor's driver table to find and fix the driver instead. |

---

## 8. Windows Resources

**Navigation label:** Windows Resources  
**Tab index:** 7

A grid of quick-launch buttons that open built-in Windows administrative tools directly from the suite. Each button launches its tool via `ShellExecute` (UAC prompts appear for tools that require elevation).

* **Disk Management** (`diskmgmt.msc`)
* **Computer Management** (`compmgmt.msc`)
* **Task Manager** (`taskmgr`)
* **Programs and Features** (`appwiz.cpl`)
* **Device Manager** (`devmgmt.msc`)
* **Event Viewer** (`eventvwr.msc`)
* **System Properties** (`sysdm.cpl`)
* **Control Panel** (`control`)
* **Registry Editor** (`regedit.exe`)
* **Group Policy Editor** (`gpedit.msc`)

---

## 9. Storage Tools

**Navigation label:** Storage Tools  
**Tab index:** 8

Allows users to analyze their directories and migrate large folders (such as game folders or application data) to a different drive, establishing NTFS symbolic links so Windows and launchers keep finding them without a reinstall.

**How a migration stays safe:** files are copied and verified first; the original is then renamed aside (instant on the same drive), the link is created and checked, and only then is the original deleted. If the link can't be created, the rename is reversed and nothing changes. **Undo** on a history entry copies the folder back, verifies it, replaces the link with the real folder and removes the migrated copy.

---

## Security & Safety Model

| Area | Approach |
|---|---|
| **Process isolation** | The UI runs at user privileges; only the engine runs elevated. This means a bug in the UI cannot directly manipulate the system. |
| **No auto-deletion** | The System Cleaner and Advanced Scanner never delete anything automatically. Items must be explicitly selected and confirmed. |
| **Registry backups** | All Registry Guard repairs create timestamped backups in `%APPDATA%\GalacticTuneUp\RegistryBackups\` before deletion. |
| **User-folder scoping** | The Advanced Scanner (duplicates, empty folders) is strictly scoped to your personal folders under `%USERPROFILE%` (Documents, Pictures, Videos, Music, Desktop, Downloads, OneDrive — not AppData) — it will never touch `System32`, `Program Files`, or other system directories. |
| **MSI warning gate** | The MSI Force button requires explicit confirmation after a compliance scan and displays a warning dialog about BSOD risk before proceeding. |
| **Engine pipe access** | Only the account that launched the engine (plus the account the UI vouches for with `--allow-sid`, for over-the-shoulder UAC) can open `TuneUpEnginePipe`. The engine refuses to start if another process already owns the pipe name, and it only answers a `TuneUpUI.exe` located next to the engine (or, for development builds, the UI built from the same solution). |
| **Act on scan results only** | Cleaning and registry repair send back only the IDs of items the engine's own scan found — never file paths or registry keys — so the engine cannot be asked to delete arbitrary data. Each ID can be used once. |
| **Duplicate re-verification** | Before deleting an extra copy of a duplicate, the engine re-hashes it and the kept copy; if the kept copy is missing or the files no longer match, nothing in that set is deleted. |
| **Allowlists** | Service changes are limited to the services listed in OS Tweaks, AppX removal to the curated/OEM package list, and Storage Migrator refuses drive roots, Windows, `Program Files`/`ProgramData`/`Users` themselves, your profile root, and destinations inside the source. |
| **Change journal (exact undo)** | Before changing any system setting — registry tweaks, Game Mode's MMCSS/timer/boot settings, the power plan, TCP settings, service start types, MSI mode — the engine records the original value in `%ProgramData%\GalacticTuneUp\change_journal.json` (writable only by Administrators/SYSTEM). Switching a feature off restores the values you actually had, even after a crash or reboot. A setting shared by two features is restored only when both are off. |
| **Crash recovery** | Changes meant to last only while a game is boosted (the power plan) are undone automatically on the next engine start if the engine stopped unexpectedly. Closing the app hands back the boosted game's priority, the power plan and any processes ProBalance throttled. |
| **Full revert** | `TuneUpEngine.exe --revert-all` (run as administrator, with the engine stopped) undoes every change the app has recorded. |

---

## File & Folder Structure

```
Galactic Tune-Up/
├── TuneUpEngine/                     # Backend service (runs elevated)
│   ├── Program.cs                    # Entry point; initializes all modules
│   ├── IpcServer.cs                  # Named pipe server; routes all UI commands
│   ├── ProBalanceGovernor.cs         # CPU process priority monitoring loop
│   ├── StandbyListManager.cs         # RAM standby list API + auto-purge
│   ├── GameModeManager.cs            # Foreground game detection + boosting
│   ├── Winapp2Parser.cs              # Junk file scanner & cleaner
│   ├── AdvancedScanner.cs            # Duplicate file + empty folder scanner
│   ├── RegistryVerifier.cs           # Registry structural integrity scanner
│   ├── RegistryTweaker.cs            # Gaming registry tweak read/write
│   ├── HardwareController.cs         # MSI interrupt compliance + provisioning
│   ├── LatencyAuditor.cs             # Per-core DPC/interrupt load (performance counters)
│   ├── DriverLatencyTracer.cs        # Kernel ETW trace: per-driver DPC/ISR run times
│   ├── SystemIntegrityAgent.cs       # CBS log analysis + SFC/DISM repair
│   ├── Winapp2Downloader.cs          # Community definitions downloader
│   └── TuneUpService.cs              # Windows Service wrapper
│
├── TuneUpUI/                         # WPF Frontend (runs as current user)
│   ├── MainWindow.xaml               # All UI layout and tab definitions
│   ├── MainWindow.xaml.cs            # Window setup and IPC client logic
│   ├── MainWindow.<Tab>.cs           # Event handlers, one partial file per tab
│   ├── Presentation.cs               # Testable display logic (incl. saved DPC trace files)
│   ├── ThemeStyles.xaml              # Global style definitions
│   ├── App.xaml                      # Application entry + resource merging
│   └── TrayIconHelper.cs             # System tray icon, context menu, notifications
│
├── Start-TuneUp.bat                  # Launch script (starts both processes)
└── TuneUpApp.slnx                    # Solution file
```

### Settings & Data Paths (Runtime)

| File | Location |
|---|---|
| User settings | `%LOCALAPPDATA%\GalacticTuneUp\settings.json` |
| Registry backups | `%APPDATA%\GalacticTuneUp\RegistryBackups\` |
| Winapp2 definitions | Same directory as `TuneUpEngine.exe` (`winapp2.ini`) |
| Engine log | `%PROGRAMDATA%\GalacticTuneUp\logs\engine-YYYYMMDD.log` (7 days kept, 10 MB/day cap; set `TUNEUP_DEBUG_LOG=1` to also record every request) |
| UI log | `%LOCALAPPDATA%\GalacticTuneUp\logs\ui-YYYYMMDD.log` |
| Change journal, engine settings, MSI backups, migration history | `%PROGRAMDATA%\GalacticTuneUp\` (administrators only) |
