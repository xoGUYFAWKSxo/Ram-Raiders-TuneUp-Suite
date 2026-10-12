# Decisions

These are settled. Don't reopen them unless the user asks. Newest first.

- **2026-10-12: Test in the installed app, not dev folders.** Every build the user tests is installed with `update-installed.ps1`, and main is reinstalled after each merge. No `publish/DevTest*` folders: the installed service won't talk to a UI outside Program Files.
- **2026-10-12: Merge on green CI.** Claude may merge its own PRs without asking once the `build-and-test` check passes. A failed or pending check is never merged.
- **2026-10-12: The public repo is the only active repo.** `Ram-Raiders-TuneUp-Suite` (public, GPL-3.0, noreply commit emails) is where all work happens. `Ram-Raiders-TuneUp-Suite-archive` (private) is frozen and stays private.
- **2026-10-12: The DPC monitor names drivers with a kernel ETW trace** (TraceEvent, private session name so LatencyMon/WPR keep the NT Kernel Logger). Driver status goes by its longest single run: OK under 500 µs, ELEVATED from 500 µs, HIGH from 2 ms. If the trace can't start, say why; the core counters keep working.
- **2026-10-12: The DPC monitor explains and advises; it doesn't tweak.** Fix advice points at drivers, BIOS and chipset updates, and disabling unused devices. It warns against placebo latency tweaks (disabling HPET, timer resolution, forcing MSI mode everywhere) instead of offering them. Peaks use a 5-second window because the Processor DPC/interrupt counters are tick-quantized.
- **2026-10-12: UI text is plain English for non-technical users.** Explain what a result means (e.g. "Windows checked its backup copy of system files and fixed all 876 problems"), not raw log lines or tool jargon. Tool names (DISM, SFC) appear only in brackets or error details. Raise an alarm only for something still unfixed.

- **2026-10-12: Registry repair never deletes without a backup.** If the `.reg` export fails, the entry is left in place and reported. Backups and the manifest go in `%APPDATA%\RamRaidersTuneUpSuite\RegistryBackups`. Registry repair isn't journaled; the `.reg` files are its undo.
- **2026-10-12: Re-enabling a service restores its previous start type.** The service is started only if that type is Automatic.
- **2026-10-11: No code signing.** There is no paid certificate. Instead, the installer ships with a SHA-256 file, and the README explains SmartScreen's "Run anyway".
- **2026-10-11: Never auto-disable SysMain.** Show its status with a one-click toggle instead.
- **2026-10-11: First-run defaults are all off**: ProBalance, Game Mode and auto purge.
- **2026-10-11: Merge manually.** GitHub auto-merge isn't allowed on this repo. Merge with `gh pr merge N --merge` after CI passes and the user says so.
- **2026-10-11: CI runs on GitHub Actions** (windows-latest, .NET 11 preview). The repo is private, so Windows minutes count double; keep the workflow lean.
- **2026-10-11: The engine runs as a LocalSystem Windows service.** The UI is never elevated.
- **2026-10-11: No full MVVM rewrite.** MainWindow is split into per-tab partial files, with typed IPC contracts.
- **2026-10-10: Engine lockdown.** The pipe accepts only `TuneUpUI.exe` from the engine's own folder. The engine acts on IDs from its own scans, not on client-supplied paths. Machine-wide commands require an admin client.
- **2026-10-10: Every change is reversible through the ChangeJournal** in ProgramData (ACL: SYSTEM and Admins full, Users read). Uninstall reverts everything.
- **2026-10-10: Show measured telemetry only.** The fabricated per-driver DPC, standby and Recycle Bin numbers were replaced with real measurements.
