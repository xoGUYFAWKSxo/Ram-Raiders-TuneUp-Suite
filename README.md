# Ram Raiders TuneUp Suite

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen.svg)](#)
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
* **Standby Auto-Purger**: Monitors physical RAM usage in a 5-second background loop. If free memory drops below 10%, it automatically flushes the standby list and logs the recovered space.

### 🧠 2. CPU ProBalance & Game Booster
* **ProBalance Governor**: Automatically throttles abusive background applications consuming excessive CPU time by temporarily lowering their scheduling priority class, giving the active foreground application 100% responsiveness.
* **Game Mode Booster**: Detects active game processes, elevates their scheduling priority to High, and pins them to all available CPU cores except Core 0 (which is reserved for OS/driver interrupt handling) to prevent frame-time micro-stuttering.

### 🔌 3. Hardware Interrupt Tuning (MSI Mode)
* **MSI Compliance Scanner**: Queries all PCI/PCIe hardware devices in the registry to evaluate Message Signaled Interrupts (MSI) compatibility.
* **Force MSI Provisioner**: Writes MSI mode override entries for compliant hardware, resolving legacy line-based IRQ conflicts and significantly reducing DPC (Deferred Procedure Call) latencies.

### 🧼 4. Rule-Based System Cleaner & Advanced Hasher
* **Winapp2.ini Scanner**: Parses standard and community-sourced Winapp2 cleaner rule files to wipe cache, log, and temp paths for hundreds of third-party applications.
* **Advanced Duplicate Hasher**: Scans user-profile folders, indexes candidates by file size, and generates MD5 hashes of the first 1 MB to identify byte-for-byte duplicate files safely.
* **Empty Folder Finder**: Recursively scans and cleans empty subdirectories within user folders without touching root structures.

### 🛠️ 5. Component Repair & Log Analyzer
* **CBS Log Analyzer**: Reads the Windows CBS log (`C:\Windows\Logs\CBS\CBS.log`) to parse corruption signatures and repair failures.
* **Automatic SFC / DISM Repair**: Triggers a clean DISM restore-health sequence backed by SFC (System File Checker) online repair.

### ⚙️ 6. 30+ Core Registry Tweaks
* **Gaming & Network**: Disables artificial network packet throttling and optimizes system responsiveness.
* **Security & Stability**: Controls Windows UAC, mitigates smartscreen delays, enables elevated network drive sharing, and displays detailed BSOD parameters.
* **Desktop & File Explorer**: Disables web-based Bing search suggestions, accelerates hover times, adds "Open Command Prompt as Admin" to context menus, and enables Windows 10/11 classic context menu overrides.

### 🔄 7. Synchronized Cohesive Lifecycle
* **Silent Elevated Startup**: WPF launcher starts the background engine using hidden process execution (`ProcessWindowStyle.Hidden` and `CreateNoWindow`), suppressing console window popups.
* **Graceful Exit Synchronization**: Closing the WPF GUI or triggering "Exit" from the system tray automatically dispatches a `"ShutdownEngine"` IPC command, ensuring the elevated engine terminates gracefully alongside the client.

---

## Project Structure

```
New Project/
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
├── Start-TuneUp.bat                  # Developer launcher batch script
├── TuneUpApp.slnx                    # Solution manifest
├── TuneUp_Documentation.md           # Deep application technical documentation
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