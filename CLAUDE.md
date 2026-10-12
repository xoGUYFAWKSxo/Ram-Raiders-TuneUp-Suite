# Ram Raiders TuneUp Suite

Windows tune-up app, C# / .NET 11, WPF. Read `STATUS.md` for current state and `DECISIONS.md` before changing anything listed there. Don't re-read the whole codebase; open only the files the task touches.

## Layout
- `TuneUpEngine/`: elevated engine. Runs as the LocalSystem service `TuneUpEngineService`, or in console mode for dev. Does every privileged action.
- `TuneUpUI/`: non-elevated WPF client. `MainWindow.<Tab>.cs` partials, one per tab. `EngineClient` speaks typed `IpcContracts`.
- `TuneUpEngine.Tests/`: xUnit tests; references both projects.
- IPC: the named pipe `TuneUpEnginePipe`. The engine only trusts `TuneUpUI.exe` from its own folder.
- `installer.iss`, `build-installer.ps1`, `test-installer.ps1`: Inno Setup installer, build, and elevated install/upgrade/uninstall test.
- `publish/`: gitignored build output and test reports.

## Build and test
```
dotnet restore --source https://api.nuget.org/v3/index.json
dotnet build TuneUpApp.slnx -c Release
dotnet test TuneUpApp.slnx -c Release --no-build
```
- The build must have 0 warnings, and nullable warnings are errors.
- CI runs the `build-and-test` check (`.github/workflows/build.yml`) on every PR.
- If a running engine locks `bin/`, build to another output folder rather than killing it. Stopping the elevated engine needs UAC.

## Workflow
- Work only in the public repo `xoGUYFAWKSxo/Ram-Raiders-TuneUp-Suite` (remote `origin`). The private `...-archive` repo (remote `archive`) is frozen: never push to it, open PRs on it, or make it public. See STATUS.md, "Repository history".
- Commit as `xoGUYFAWKSxo <206061236+xoGUYFAWKSxo@users.noreply.github.com>` (set in this clone's git config). Never commit with a personal email address; the repo is public.
- Branch, then open a PR, then merge only when the user says to, after CI passes, with `gh pr merge N --merge`. GitHub auto-merge is disabled on this repo.
- Commit messages: write them to a file and use `git commit -F`, in UTF-8 without a BOM.
- Update `STATUS.md` when a PR merges. Add to `DECISIONS.md` when the user makes a choice.

## Rules for engine code
- Every system change must be reversible. Record the original in the ChangeJournal (ProgramData) before writing. Uninstall runs `--revert-all`.
- Never delete anything without a backup. Report partial failures as `Error` with a message; never report success for work that was skipped.
- The engine acts only on IDs from its own scan results, never on paths or keys the client sends.
- Machine-wide commands require an administrator client (`ClientContext.IsAdministrator`).
- Show measured numbers only. Never invent a value for the UI.

## Style
- Keep comments sparse: explain why, not what. Match the surrounding code.
- User-facing text must be plain English (see DECISIONS.md, 2026-10-12).
- Add a test for every bug fix (see `EndToEndFixTests.cs` for the pattern).
