# TuneUp Premium Suite 2026 — Application Documentation

> A professional-grade Windows system optimization and maintenance utility built with a WPF frontend and a dedicated elevated background engine communicating over a secure Named Pipe IPC channel.

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
11. [Security & Safety Model](#security--safety-model)
12. [File & Folder Structure](#file--folder-structure)

---

## Architecture Overview

TuneUp Premium Suite is split into two separate processes that communicate in real-time:

```
┌─────────────────────┐        Named Pipe IPC         ┌──────────────────────────┐
│     TuneUpUI.exe    │  ◄──── "TuneUpEnginePipe" ───► │   TuneUpEngine.exe       │
│  (WPF Frontend)     │        JSON over stream         │  (Elevated Backend)      │
│  Runs as user       │                                 │  Runs as Administrator   │
└─────────────────────┘                                 └──────────────────────────┘
```

| Component | Role |
|---|---|
| **TuneUpUI** | The visual WPF application the user interacts with. Runs at standard user privileges. |
| **TuneUpEngine** | The headless background service that performs all system-level work. Must run as Administrator for full functionality. |
| **Named Pipe (`TuneUpEnginePipe`)** | Secure bidirectional JSON communication channel between the two processes. Supports streaming progress updates so the UI stays responsive during long operations. |

The UI auto-discovers and launches the Engine if it is not already running, prompting for a UAC elevation prompt.

---

## How to Launch

Run **`Start-TuneUp.bat`** from the project root. This script:

1. Starts `TuneUpEngine.exe` with Administrator elevation (UAC prompt appears).
2. Starts `TuneUpUI.exe` as the current user.
3. The UI polls the named pipe every 500ms for up to 15 seconds until the engine is ready, then immediately populates the dashboard.

> [!IMPORTANT]
> The engine **must** run as Administrator for memory management, registry writes, and SFC/DISM repairs to function. Features that require elevation will silently fail or show warnings if the engine is not elevated.

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
| **Standby List Size** | Estimated size of Windows' standby memory cache (inactive pages held in RAM). High standby usage is normal but can be purged to free RAM for active applications. |
| **Total Memory Freed** | Cumulative amount of RAM recovered by all standby list purges since the engine started. |

### Purge Standby Memory Button

Manually triggers an immediate flush of the Windows Standby List using the `NtSetSystemInformation` low-level API. This reclaims memory that Windows is holding in cache for potentially-needed data, making it available to running applications instantly. Useful before launching a memory-intensive game or application.

> [!NOTE]
> This is safe and non-destructive. Windows will rebuild the standby list organically as applications run. No data is lost.

### Feature Toggles

| Toggle | What it controls |
|---|---|
| **ProBalance Monitoring** | Enables the ProBalance Governor — an automated background process that detects when non-foreground processes are consuming excessive CPU and temporarily lowers their priority class, giving the active foreground application more CPU time. Priority is restored the moment the offending process becomes the foreground window. |
| **Standby Auto-Purge** | When enabled, the engine monitors free RAM every 30 seconds. If free RAM drops below 10% of total installed RAM, a standby list purge is automatically triggered. |
| **Game Mode Booster** | When enabled, the engine monitors the active foreground window every second. If the active process is identified as a game (by executable name whitelist or install path heuristics), it receives High process priority and is pinned to all CPU cores except Core 0 (which is reserved for OS and driver interrupt handling). When the game loses focus, its priority is restored. |

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
| **Scan Duplicate User Files** | After the main scan, indexes all files in your user folders by file size, then uses MD5 hashing of the first 1 MB of each candidate to identify true byte-for-byte duplicates. Reports each duplicate group with the amount of space that *could* be recovered by keeping only one copy. |

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

> [!TIP]
> For large rule sets (500+ files), the panel shows the first 50 paths plus a count summary to keep the UI responsive.

### Scan for Junk Files

Initiates the scan sequence:
1. The primary Winapp2 rule engine scans system paths for cached, temporary, and log files.
2. If "Scan Empty User Folders" is checked, the advanced scanner runs next.
3. If "Scan Duplicate User Files" is checked, the duplicate hasher runs last.
4. All results are merged into a single list for review.

### Empty Junk Files

Deletes only the rows with their checkbox **checked**. Progress is streamed back from the engine in real-time. A summary dialog reports total megabytes recovered when complete.

> [!WARNING]
> Deletion is permanent. Use the File Inspection Panel to verify items you are unsure about before cleaning.

### Update Definitions

Downloads the latest community `winapp2.ini` rule set from GitHub, expanding the number of applications the scanner can clean. The engine hot-reloads the new rules without restarting.

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
1. Creates an automatic backup of the key to `%APPDATA%\TuneUpPremiumSuite\RegistryBackups\` with a timestamp.
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

> [!IMPORTANT]
> This operation requires an active internet connection for the DISM phase and can take 10–30 minutes depending on system speed. The UI streams progress messages from the engine throughout.

After repair completes, the CBS log is automatically re-read to confirm the issues are resolved.

---

## 5. Live Monitor

**Navigation label:** Live Monitor  
**Tab index:** 4

A real-time scrolling event log showing all actions the engine has taken since it started. This is the same event log shown in the Dashboard but displayed full-screen for easier reading.

Events are categorized by source:
- `[ProBalance]` — Process priority throttle/restore events
- `[Memory]` — Standby list auto-purge events with MB freed
- `[GameMode]` — Game detection boost/restore events
- `[Engine]` — Informational engine status messages
- `[Downloader]` — Definition update events

The log retains the most recent 100 entries, rotating out older ones automatically.

---

## 6. Settings

**Navigation label:** Settings gear icon (top-right title bar)  
**Tab index:** 5

Persistent application preferences, saved to `%LOCALAPPDATA%\TuneUpPremiumSuite\settings.json`.

### Behavior Settings

| Setting | Description |
|---|---|
| **Start on Windows Startup** | Adds TuneUpUI to `HKCU\...\Run` with the `--minimized` flag so it launches silently in the background when you log in. |
| **Launch UI Minimized** | When TuneUp starts, immediately hide the main window and run from the system tray only. |
| **Minimize to Tray on Close** | When you click the ✕ button, hide to the system tray instead of exiting. The engine keeps running. Right-click the tray icon → Exit to fully quit. |

### Notification Settings

| Setting | Description |
|---|---|
| **Enable Tray Notifications** | Master switch for all Windows toast notifications from the tray icon. |
| **ProBalance Notifications** | Show a tray notification when ProBalance throttles or restores a process. |
| **Memory Purge Notifications** | Show a tray notification when an automatic standby list purge is triggered. |

---

## 7. Gaming / OS Tweaks

**Navigation label:** Gaming/OS Tweaks  
**Tab index:** 6

Two sections: persistent registry-based Windows optimization tweaks, and hardware interrupt tuning.

### Registry Tweaks

Each tweak has a toggle, title, and description. Toggles apply or revert changes immediately via the engine. The current state of each tweak is read from the registry on startup so toggles always reflect reality.

| Tweak | Registry Key | What it does |
|---|---|---|
| **Disable Network Throttling** | `HKLM\...\Multimedia\SystemProfile` → `NetworkThrottlingIndex = 0xFFFFFFFF` | Removes Windows' artificial cap on network packet scheduling for multimedia applications, allowing the NIC to process at full speed. Default value (10) is restored when toggled off. |
| **Maximum System Responsiveness** | `HKLM\...\Multimedia\SystemProfile` → `SystemResponsiveness = 0` | Allocates 100% of CPU scheduling quantum to the foreground task's multimedia needs instead of the default 20% background/OS allocation. Reduces micro-stutters in games. |
| **Disable Xbox Game DVR** | `HKCU\System\GameConfigStore` + `HKCU\...\GameDVR` | Disables the background screen recording hooks (`GameDVR_Enabled = 0`, `AppCaptureEnabled = 0`) that inject into every DirectX application, eliminating associated input lag and frame-time spikes. |
| **Disable Global Power Throttling** | `HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling` → `PowerThrottlingOff = 1` | Prevents Windows from automatically moving background processes into Efficiency mode (Eco QoS) while gaming, which can cause stutters when a background task suddenly needs CPU. Deleting the key restores default Windows behavior. |

### Hardware Interrupt Tuning (MSI Mode)

> [!WARNING]
> Forcing MSI on unsupported hardware can cause BSODs, loss of display, or network drops. Always run the compliance scan first.

| Button | What it does |
|---|---|
| **Auto-Search Compliance** | Scans all PCI/PCIe devices via the Windows registry and reports which are already using Message Signaled Interrupts vs. legacy line-based IRQs. Devices eligible for MSI forcing are listed. |
| **Force MSI Tweak** | Writes MSI mode override entries to the registry for each compliant device found. Requires a system reboot to take effect. MSI eliminates interrupt sharing and significantly reduces DPC (Deferred Procedure Call) latency, which improves audio/input/GPU scheduling consistency. |

---

## 8. Windows Resources

**Navigation label:** Windows Resources  
**Tab index:** 7

A grid of quick-launch buttons that open built-in Windows administrative tools directly from TuneUp. Each button launches its tool via `ShellExecute` (UAC prompts appear for tools that require elevation).

| Button | Tool launched |
|---|---|
| Disk Management | `diskmgmt.msc` |
| Computer Management | `compmgmt.msc` |
| Task Manager | `taskmgr` |
| Programs and Features | `appwiz.cpl` |
| Device Manager | `devmgmt.msc` |
| Event Viewer | `eventvwr.msc` |
| System Properties | `sysdm.cpl` |
| Control Panel | `control` |
| Registry Editor | `regedit.exe` |
| Group Policy Editor | `gpedit.msc` |

---

## Security & Safety Model

| Area | Approach |
|---|---|
| **Process isolation** | The UI runs at user privileges; only the engine runs elevated. This means a bug in the UI cannot directly manipulate the system. |
| **No auto-deletion** | The System Cleaner and Advanced Scanner never delete anything automatically. Items must be explicitly selected and confirmed. |
| **Registry backups** | All Registry Guard repairs create timestamped backups before deletion. |
| **User-folder scoping** | The Advanced Scanner (duplicates, empty folders) is strictly scoped to `%USERPROFILE%` subdirectories — it will never touch `System32`, `Program Files`, or other system directories. |
| **MSI warning gate** | The MSI Force button requires explicit confirmation after a compliance scan and displays a full-screen warning dialog about BSOD risk before proceeding. |

---

## File & Folder Structure

```
New Project/
├── TuneUpEngine/                     # Backend service (runs elevated)
│   ├── Program.cs                    # Entry point; initializes all modules
│   ├── IpcServer.cs                  # Named pipe server; routes all UI commands
│   ├── ProBalanceGovernor.cs         # CPU process priority monitoring loop
│   ├── StandbyListManager.cs         # RAM standby list API + auto-purge
│   ├── GameModeManager.cs            # Foreground game detection + boosting
│   ├── Winapp2Parser.cs              # Junk file scanner & cleaner (ScanItem model)
│   ├── AdvancedScanner.cs            # Duplicate file + empty folder scanner
│   ├── RegistryVerifier.cs           # Registry structural integrity scanner
│   ├── RegistryTweaker.cs            # Gaming registry tweak read/write
│   ├── HardwareController.cs         # MSI interrupt compliance + provisioning
│   ├── SystemIntegrityAgent.cs       # CBS log analysis + SFC/DISM repair
│   ├── Winapp2Downloader.cs          # Community definitions downloader
│   └── TuneUpService.cs              # Windows Service wrapper (--service mode)
│
├── TuneUpUI/                         # WPF Frontend (runs as current user)
│   ├── MainWindow.xaml               # All UI layout and tab definitions
│   ├── MainWindow.xaml.cs            # All UI event handlers and IPC client logic
│   ├── ThemeStyles.xaml              # Global style definitions (buttons, toggles, cards)
│   ├── App.xaml                      # Application entry + resource merging
│   └── TrayIconHelper.cs             # System tray icon, context menu, notifications
│
├── Start-TuneUp.bat                  # Launch script (starts both processes)
└── TuneUpApp.slnx                    # Visual Studio solution file
```

### Settings & Data Paths (Runtime)

| File | Location |
|---|---|
| User settings | `%LOCALAPPDATA%\TuneUpPremiumSuite\settings.json` |
| Registry backups | `%APPDATA%\TuneUpPremiumSuite\RegistryBackups\` |
| Winapp2 definitions | Same directory as `TuneUpEngine.exe` (`winapp2.ini`) |
