# Ram Raiders TuneUp Suite

[![Build and test](https://github.com/xoGUYFAWKSxo/Ram-Raiders-TuneUp-Suite/actions/workflows/build.yml/badge.svg)](https://github.com/xoGUYFAWKSxo/Ram-Raiders-TuneUp-Suite/actions/workflows/build.yml)
[![Target Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue.svg)](#)
[![Language](https://img.shields.io/badge/language-C%23-violet.svg)](#)
[![Framework](https://img.shields.io/badge/framework-.NET%2011%20%7C%20WPF-red.svg)](#)

A professional-grade, high-performance Windows system optimization and maintenance utility. **Ram Raiders TuneUp Suite** is built with a highly responsive WPF desktop frontend that communicates over a secure, custom-serialized Named Pipe IPC channel to a dedicated elevated background engine.

```
                  ┌──────────────────────────────────────────┐
                  │          Ram Raiders TuneUp Suite        │
                  │              (WPF Frontend)              │
                  │           Runs at User Privilege         │
                  └────────────────────┬─────────────────────┘
                                       │
                                Named Pipe IPC
                            "TuneUpEnginePipe"
                                       │
                  ┌────────────────────▼─────────────────────┘
                  │          Ram Raiders TuneUp Engine       │
                  │             (Elevated Backend)           │
                  │           Runs as Administrator          │
                  └──────────────────────────────────────────┘
```

---

## Key Capabilities

### ⚡ 1. Memory Health & Auto-Purging
* **Standby List Flusher**: Purges inactive system standby memory instantly via the low-level `NtSetSystemInformation` native API (safe and non-destructive).
* **Standby Auto-Purger**: Checks every 5 seconds and empties the standby list only when truly free memory is below 10% of RAM while the standby cache holds at least as much (at most once a minute), logging the measured amount freed.
* **Driver DPC & Interrupt Load**: Per-core share of CPU time spent in driver DPCs and interrupts, from Windows performance counters, with peaks and averages. (Counters can't name the responsible driver; LatencyMon can.)

### 🧠 2. CPU ProBalance & Game Booster
* **ProBalance Governor**: Automatically throttles abusive background applications consuming excessive CPU time by temporarily lowering their scheduling priority class, giving the active foreground application 100% responsiveness.
* **Game Mode Booster**: Detects the active game and raises its priority to High. Following Microsoft's guidance for games, it does not hard-pin games on ordinary CPUs; on hybrid CPUs with at least three performance cores it keeps the game on the performance cores (detected from Windows' own core efficiency classes).

### 🔌 3. Hardware Interrupt Tuning (MSI Mode)
* **MSI Compliance Scanner**: Queries all PCI/PCIe hardware devices in the registry to evaluate Message Signaled Interrupts (MSI) compatibility.
* **Force MSI Provisioner**: Writes MSI mode override entries for compliant hardware, resolving legacy line-based IRQ conflicts and significantly reducing DPC (Deferred Procedure Call) latencies.

### 🧼 4. Rule-Based System Cleaner & Advanced Hasher
* **Winapp2.ini Scanner**: Full support for community winapp2 rules — wildcard folders, registry `Detect` keys, numbered `DetectFile` keys, `REMOVESELF`, wildcard exclusions, all common path tokens, categories and rule warnings. Event logs, the CBS log, registry hives and the component store are never cleaned, whatever a rule says.
* **Advanced Duplicate Hasher**: Scans your Documents, Pictures, Videos, Music, Desktop, Downloads and OneDrive folders, groups candidates by file size, pre-filters on a SHA-256 of the first 1 MB, then confirms true byte-for-byte duplicates with a full-file SHA-256. Cloud-only (online) files are skipped, and cleaning a duplicate set always keeps the first copy.
* **Empty Folder Finder**: Recursively scans and cleans empty subdirectories within user folders without touching root structures.

### 🛠️ 5. Component Repair & Log Analyzer
* **CBS Log Analyzer**: Reads the Windows CBS log (`C:\Windows\Logs\CBS\CBS.log`) to parse corruption signatures and repair failures.
* **Automatic SFC / DISM Repair**: Runs DISM RestoreHealth then SFC, and reports what actually happened (DISM exit codes and SFC's verdict) instead of always reporting success.

### ⚙️ 6. 30+ Core Registry Tweaks
* **Gaming & Network**: Disables artificial network packet throttling and optimizes system responsiveness.
* **Security & Stability**: Controls Windows UAC, mitigates smartscreen delays, enables elevated network drive sharing, and displays detailed BSOD parameters.
* **Desktop & File Explorer**: Disables web-based Bing search suggestions, accelerates hover times, adds "Open Command Prompt as Admin" to context menus, and enables Windows 10/11 classic context menu overrides.

### 🔄 7. Synchronized Cohesive Lifecycle
* **Windows Service Engine**: The installer registers the engine as an automatically starting Windows service, so there is no UAC prompt on launch or sign-in and background features keep running after the window is closed. (Development builds without the service fall back to launching the engine with a UAC prompt and stop it on exit.)
* **Per-User Safety**: Each request is handled for the user who made it — your registry hive, your folders, and file deletion performed as you. Machine-wide changes require an administrator account.

---

## Project Structure

```
Ram Raiders TuneUp Suite/
├── TuneUpEngine/                     # Elevated Backend Engine
│   ├── Program.cs                    # Main entry point & service wrappers
│   ├── IpcServer.cs                  # Named Pipe server & JSON serializers
│   ├── RegistryTweaker.cs            # Registry read/write configurations
│   ├── StandbyListManager.cs         # P/Invoke NtSetSystemInformation RAM flusher
│   └── ...                           # CPU, Cleaner, CBS, and Hardware managers
│
├── TuneUpUI/                         # WPF Desktop Client Frontend
│   ├── MainWindow.xaml               # Visual layout, GlassCard, and theme stylings
│   ├── MainWindow.xaml.cs            # IPC client channels, event loops, and UI bindings
│   ├── Converters.cs                 # Custom WPF bindings & size formatting
│   └── TrayIconHelper.cs             # System tray wrapper, notifications, & context menus
│
├── TuneUpEngine.Tests/              # xUnit unit tests for engine logic
├── docs/                            # Static landing/waitlist site (GitHub Pages)
├── Start-TuneUp.bat                  # Developer launcher batch script
├── TuneUpApp.slnx                    # Solution manifest
├── TuneUp_Documentation.md           # Deep application technical documentation
├── build-installer.ps1               # Publishes both projects and compiles the installer
├── test-installer.ps1                # Install / upgrade / uninstall test (run as administrator)
└── installer.iss                     # Inno Setup installer script
```

---

## How to Build and Run

### Prerequisites
* Windows 10 or 11
* [.NET 11.0 SDK](https://dotnet.microsoft.com/download)

### Run Diagnostics Locally
Simply double-click **`Start-TuneUp.bat`** in the root of the workspace folder. This script will:
1. Auto-compile the solution (`dotnet build`).
2. Start the WPF Client GUI (`TuneUpUI.exe`).
3. The GUI will automatically discover the background service, prompt for Administrator elevation (UAC prompt), and start `TuneUpEngine.exe` completely silently in the background.