# Status

_Last updated: 2026-10-16, public repo PR #20._

**Project status: complete for now (paused 2026-10-12).** Every planned feature is built, tested and merged, and the installed app on the dev PC matches main. Nothing is in progress. If work resumes, start from "If work resumes" below.

## Repository history
- **Active repo:** `xoGUYFAWKSxo/galactic-tune-up` (public, GPL-3.0; named `Ram-Raiders-TuneUp-Suite` until 2026-10-16, and the old URL redirects). All work happens here: branches, PRs, CI and releases.
- **Archive (frozen):** `xoGUYFAWKSxo/Ram-Raiders-TuneUp-Suite-archive` (private). It is the pre-2026-10-12 history, with PRs #1 to #23 and their discussions. It is no longer modified: don't push to it, open PRs on it, or make it public (its PRs contain personal email addresses). Read it only to look up old PR discussions.
- 2026-10-12: the active repo was created fresh from the archive's main, with commit emails rewritten to the GitHub noreply address. The file contents are identical. PR numbers and commit hashes in the table below (up to #23) refer to the archive; PR numbers in the active repo start again at #1.

## Current state
- The phased rework plan is complete (PRs #1 to #16).
- End-to-end review (PR #17) and testing of the destructive features against a real engine (PR #18) are done and merged.
- 2026-10-16: renamed from "Ram Raiders TuneUp Suite" to **Galactic Tune-Up**, with a new icon (the CPU knight on the Galactic Macro galaxy, built by `assets/make_icon.py`). Upgrading moves the install folder, the data folders (change journal included) and the startup entry to the new name.
- 2026-10-16: first release, [v1.0.0](https://github.com/xoGUYFAWKSxo/galactic-tune-up/releases/tag/v1.0.0), from main at 386f2c9: `GalacticTuneUp_Setup.exe` (SHA-256 c017a800…60e3b) with its `.sha256` file. It is the same installer that upgraded the dev PC, and `test-installer.ps1` passed on it.
- 2026-10-16: GitHub Pages is on, serving `docs/` from main at https://xoguyfawksxo.github.io/galactic-tune-up/ (also the repo's homepage link). The page has a "Download for Windows" button to the latest release; the "Alpha" badge was removed.
- Build: 0 warnings, 312 tests pass. CI is green.
- The installer has been tested for install, upgrade and uninstall (`test-installer.ps1`). The dev PC runs main as a service, installed with `update-installed.ps1` after each merge.

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
- Cleaner switches filtering the junk scan (all on: 7 rules, 3,285.7 MB; all three off: Recycle Bin only).

## Not verified live
- Cleaning with junk rules, and cleaning empty folders. Both share their code path with the duplicate clean, which was tested.
- Running as a real SYSTEM service on a clean PC that isn't the dev machine.

## If work resumes
- Test on a clean PC that isn't the dev machine (see "Not verified live").

## Open items / ideas
- A full MVVM conversion of the UI was not done (it is split into per-tab partials instead).
- The UI's appearance has not been checked by screenshot after the UI polish PR (#10).
- Cleaner: the "Scan Browser Cache" switch covers only the built-in Chrome and Edge rules. winapp2 browser entries always run, because winapp2's browser groups (LangSecRef numbers) haven't been checked against a real winapp2.ini.

## Recent PRs
| PR | Merge | What |
|----|-------|------|
| public #20 | (this PR) | Switch labels wrap instead of running off the card (the Dashboard's Auto-Purge description was cut off): the shared switch style lays out with a DockPanel, so the label gets the width left after the switch; web page screenshots retaken (?v=5) |
| public #19 | 1350169 | Cleaner: the "Scan Temp Files", "Scan Windows Update Cache" and "Scan Browser Cache" switches now choose which built-in rules the junk scan runs (they were never read before) and start switched on; Recycle Bin and winapp2 entries always run |
| public #18 | e197bf4 | Web page screenshots retaken from the installed app after #17 (image links bumped to ?v=4); sidebar engine label shortened to "Background service" so it isn't cut off |
| public #17 | da91ae1 | In-app wording follows the plain-language rule: "PRO 2026" badge removed; ProBalance, Game Mode, Cleaner, Registry Guard, Tweaks, network, Live Monitor, Settings and Storage text says what the code does (checked against the engine); `UiWordingTests` blocks the removed phrases |
| public #16 | 784c534 | Test fails the build if any source file isn't valid UTF-8 or has double-encoded text (like the garbled dash once in the Storage Tools subtitle); web page screenshots retaken from the installed app (the old ones predated that fix and the rename) |
| public #15 | 2888296 | Web page: hero text and buttons stay readable when a browser's dark mode strips gradients |
| public #14 | 0207c87 | Web page: Download button, Alpha badge removed; STATUS records GitHub Pages |
| public #13 | 0dd4554 | README and STATUS: v1.0.0 released |
| public #12 | 386f2c9 | Renamed to Galactic Tune-Up: new galaxy + knight icon and installer artwork; data folders, install folder and startup entry move to the new name on upgrade |
| public #11 | cea6393 | Project marked complete for now; unused waitlist styles removed from the web page's stylesheet |
| public #10 | f4fe335 | Plain-language pass: the project web page drops the fake waitlist (invented seat counter, sign-ups that stayed in the visitor's browser), pricing survey and hype, and says plainly what the app does and doesn't do; README gains a "What It Doesn't Do" section |
| public #9 | 5d3c749 | README and TuneUp_Documentation describe the driver table and Save Trace; MSI wording matches DECISIONS |
| public #8 | aeb41a5 | STATUS: record public PRs #6 and #7 |
| public #7 | e73b133 | Live Monitor "Save Trace": saves the DPC results as a text report or CSV (choose in "Save as type"); no computer or user name in the file (checked in the app) |
| public #6 | 1363d17 | STATUS/DECISIONS: record public PRs #3 to #5 |
| public #5 | e63d89b | DPC monitor names drivers: a kernel ETW trace (TraceEvent) times every DPC/ISR run and charges it to its driver; driver table with longest run in µs and advice; help panel replaces the tables while open (checked in the app: 252 drivers named) |
| public #4 | d19e37d | `update-installed.ps1`: test builds go into the installed app; no DevTest folders |
| public #3 | a299ab4 | STATUS: record public PRs #1 and #2 |
| public #2 | 72db7d5 | DPC monitor: first sample no longer reads hundreds of percent (wait one interval after priming; clamp to 0-100%). Found by CI reading 772% |
| public #1 | bc8cff7 | Docs: public repo is the only active repo, the archive is frozen; merge PRs once CI passes green |
| #22 | 8380398 | Live Monitor DPC card: 5-second peaks (the counters move in 1.56% clock-tick steps), peak total with matching DPC/ISR parts, consistent log line, scrollable "What is this, and how do I fix it?" panel (checked in the app) |
| #20 | 996e1fa | Component Repair shows plain-English DISM/SFC results; a successful repair no longer reads as "corruption detected" (not yet checked in the app) |
| #19 | 303a4bb | CLAUDE.md, STATUS.md, DECISIONS.md |
| #18 | f7b4a90 | Registry repair reports failures and won't delete without a backup; re-enabled Manual services aren't started; appx refusals return Error |
| #17 | d00ebc9 | Standby purge enum fix, legacy settings migration, tweak error messages, latency badge colours, glyph/mojibake fixes |
| #16 | b216b4c | Installer SHA-256 file, README install notes |
| #15 | 76f139d | test-installer.ps1; uninstall cleanup |
