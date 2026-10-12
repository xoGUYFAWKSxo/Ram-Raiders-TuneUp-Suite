# Status

_Last updated: 2026-10-12, main at 8380398 (PR #22)._

## Repository history
- 2026-10-12: the repo went public as a fresh repository with commit emails rewritten to the GitHub noreply address, under GPL-3.0. The original private repo, with PRs #1 to #23 and their discussions, is `xoGUYFAWKSxo/Ram-Raiders-TuneUp-Suite-archive`. PR numbers and commit hashes below refer to that archive; the file contents are identical.

## Current state
- The phased rework plan is complete (PRs #1 to #16).
- End-to-end review (PR #17) and testing of the destructive features against a real engine (PR #18) are done and merged.
- Build: 0 warnings, 281 tests pass. CI is green.
- The installer has been tested for install, upgrade and uninstall (`test-installer.ps1`). The dev PC runs the PR #22 build (9b4e929) as a service; its upgrade test passed on 2026-10-12, and the user checked the Live Monitor changes in the app.

## Verified on a real engine
- All read-only commands.
- Registry repair, backup and restore.
- Duplicate clean.
- Tweaks in HKCU and HKLM, with exact restore.
- Service toggle.
- Network apply and revert.
- MSI scan.
- Migration and undo, checked with SHA-256.
- winapp2 download.
- ProBalance.
- Game Mode.
- DISM and SFC.
- Refusals of unsafe appx removals.

## Not verified live
- Cleaning with junk rules, and cleaning empty folders. Both share their code path with the duplicate clean, which was tested.
- Running as a real SYSTEM service on a clean PC that isn't the dev machine.

## Open items / ideas
- A full MVVM conversion of the UI was not done (it is split into per-tab partials instead).
- The UI's appearance has not been checked by screenshot after the UI polish PR (#10).
- DPC monitor, next step: an ETW kernel trace (DPC + interrupt events mapped to loaded driver images) to name the responsible driver and show its longest single DPC/ISR in microseconds, then advice specific to that driver. Performance counters can't do either.

## Recent PRs
| PR | Merge | What |
|----|-------|------|
| #22 | 8380398 | Live Monitor DPC card: 5-second peaks (the counters move in 1.56% clock-tick steps), peak total with matching DPC/ISR parts, consistent log line, scrollable "What is this, and how do I fix it?" panel (checked in the app) |
| #20 | 996e1fa | Component Repair shows plain-English DISM/SFC results; a successful repair no longer reads as "corruption detected" (not yet checked in the app) |
| #19 | 303a4bb | CLAUDE.md, STATUS.md, DECISIONS.md |
| #18 | f7b4a90 | Registry repair reports failures and won't delete without a backup; re-enabled Manual services aren't started; appx refusals return Error |
| #17 | d00ebc9 | Standby purge enum fix, legacy settings migration, tweak error messages, latency badge colours, glyph/mojibake fixes |
| #16 | b216b4c | Installer SHA-256 file, README install notes |
| #15 | 76f139d | test-installer.ps1; uninstall cleanup |
