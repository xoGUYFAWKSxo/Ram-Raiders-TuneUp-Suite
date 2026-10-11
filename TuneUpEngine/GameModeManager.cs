using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace TuneUpEngine
{
    public class GameModeManager
    {
        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtSetTimerResolution(uint DesiredResolution, bool SetResolution, out uint CurrentResolution);

        private bool _isEnabled = false;
        private Thread _boosterThread;
        private readonly object _lock = new object();
        
        private volatile int _lastBoostedPid = 0;
        private string _lastBoostedName = "";
        private ProcessPriorityClass _lastOriginalPriority = ProcessPriorityClass.Normal;
        private IntPtr _lastOriginalAffinity;

        // PID of the game currently boosted (0 when none). Read by ProBalance so it never throttles it.
        public int BoostedProcessId => _lastBoostedPid;

        // Every system setting Game Mode changes is recorded in the change journal first, so turning
        // Game Mode off restores the user's real previous values — even after a crash or reboot.
        // (Previously the originals were cached in memory only: closing the engine lost them, and
        // the next start then cached the already-tweaked values as the "originals".)
        private const string JournalOwner = "gamemode";
        // Changes that only apply while a game is boosted. Transient: undone on the next engine
        // start if the engine died mid-game.
        private const string BoostJournalOwner = "gamemode-boost";
        private readonly ChangeJournal _journal;

        private const string MmcssProfileKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        private const string GamesTaskKey = MmcssProfileKey + @"\Tasks\Games";
        private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";

        // e9a42b02-… is Windows' hidden Ultimate Performance *template*. `powercfg -duplicatescheme`
        // copies it to a brand-new random GUID, so the template GUID normally never appears in
        // `powercfg /list` and can't be activated. The old code checked for and activated the template
        // GUID, so it duplicated the plan on every enable and then failed to switch to it. We now copy
        // the template to a fixed GUID that belongs to this app, which we can find and activate.
        private const string UltimateTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        private const string AppUltimatePlanGuid = "5c6a6e1d-7b0a-4d8e-9f3a-52a1d0c7e4b9";
        private string _ultimatePlanGuid = ""; // Resolved plan to activate while a game is boosted

        // Dynamic game path keywords
        private readonly List<string> _gameKeywords = new List<string>
        {
            "steamapps", "epic games", "origin games", "gog galaxy", "ubisoft game", "riot games",
            "blizzard", "games", "gaming", "fortnite", "minecraft", "valorant", "cyberpunk"
        };

        // Standard common game executable names
        private readonly HashSet<string> _gameWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csgo", "cs2", "dota2", "leagueoflegends", "valorant", "gta5", "rdr2", "cyberpunk2077",
            "witcher3", "minecraft.launcher", "javaw", "fortniteclient-win64-shipping", "fifa", "cod"
        };

        public event Action<string, int> OnGameBoosted;
        public event Action<string, int> OnGameBoostRestored;

        // journal == null keeps an in-memory journal (tests); the engine passes the persisted one.
        public GameModeManager(ChangeJournal? journal = null)
        {
            _journal = journal ?? new ChangeJournal(null);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                lock (_lock)
                {
                    if (_isEnabled == value) return;
                    _isEnabled = value;
                    if (_isEnabled)
                    {
                        // Pre-load core topology so affinity decisions are instant
                        var topology = CoreDirector.GetTopology();
                        EngineLog.Info($"[GameMode] Core topology: {topology.DetectionMethod} | Hybrid: {topology.HasHybridCores}");
                        ApplyGlobalTweaks();
                        StartBooster();
                    }
                    else
                    {
                        RestoreLastBoosted();
                        RevertGlobalTweaks();
                    }
                }
            }
        }

        private void ApplyGlobalTweaks()
        {
            try
            {
                if (!_journal.IsOwned(JournalOwner))
                    ResetStateLeftByOlderVersion();

                // 1. MMCSS SystemResponsiveness
                SetJournaledDword(MmcssProfileKey, "SystemResponsiveness", 0);

                // 2. MMCSS Game Tasks Priority
                SetJournaledDword(GamesTaskKey, "GPU Priority", 8);
                SetJournaledDword(GamesTaskKey, "Priority", 6);

                // 3. Kernel Timer Resolution (now also reverted — it previously never was)
                SetJournaledDword(KernelKey, "GlobalTimerResolutionRequests", 1, createKey: true);

                // 4. BCDEdit Dynamic Tick. Reverting deletes the value if it wasn't set before,
                // rather than writing "no" (which left a non-default boot setting behind).
                if (_journal.CaptureBcd(JournalOwner, "disabledynamictick"))
                    _journal.Commands.SetBcdValue("disabledynamictick", "yes");

                // 5. Ultimate Power Plan — resolve (creating it at most once) the plan to activate
                // while a game is boosted.
                _ultimatePlanGuid = EnsureUltimatePlan();
            }
            catch (Exception ex)
            {
                EngineLog.Warn($"[GameMode] Failed to apply global tweaks: {ex.Message}");
            }
        }

        private void SetJournaledDword(string keyPath, string valueName, int value, bool createKey = false)
        {
            if (!createKey)
            {
                using var existing = Registry.LocalMachine.OpenSubKey(keyPath);
                if (existing == null) return; // Only adjust MMCSS values on systems that have them.
            }

            if (!_journal.CaptureRegistryValue(JournalOwner, RegistryHive.LocalMachine, keyPath, valueName))
                return; // Original couldn't be recorded — don't make a change we couldn't undo.

            using var key = Registry.LocalMachine.CreateSubKey(keyPath);
            key.SetValue(valueName, value, RegistryValueKind.DWord);
        }

        // Versions before the change journal applied Game Mode's settings without recording the
        // originals. If they are still in place with no journal entry, recording them now would
        // make "revert" restore the tweaked values. Recognise that exact signature and put the
        // Windows defaults back first, so those become the recorded originals.
        private void ResetStateLeftByOlderVersion()
        {
            using var profile = Registry.LocalMachine.OpenSubKey(MmcssProfileKey, writable: true);
            using var games = Registry.LocalMachine.OpenSubKey(GamesTaskKey, writable: true);
            if (profile == null || games == null) return;

            bool looksLegacyApplied =
                profile.GetValue("SystemResponsiveness") is int sr && sr == 0 &&
                games.GetValue("GPU Priority") is int gpu && gpu == 8 &&
                games.GetValue("Priority") is int prio && prio == 6;
            if (!looksLegacyApplied) return;

            EngineLog.Info("[GameMode] Found settings left by an older version; restoring Windows defaults before recording originals.");
            profile.SetValue("SystemResponsiveness", 20, RegistryValueKind.DWord);
            games.SetValue("Priority", 2, RegistryValueKind.DWord);
            using (var kernel = Registry.LocalMachine.OpenSubKey(KernelKey, writable: true))
                kernel?.DeleteValue("GlobalTimerResolutionRequests", throwOnMissingValue: false);
            _journal.Commands.DeleteBcdValue("disabledynamictick");
        }

        private void RevertGlobalTweaks()
        {
            int failed = _journal.Release(JournalOwner);
            if (failed > 0)
                EngineLog.Warn($"[GameMode] {failed} setting(s) could not be restored yet; they will be retried on the next engine start.");
        }

        // Called when the engine shuts down: hand the boosted game and the power plan back. Game
        // Mode's persistent settings stay (the feature is still on); they're reverted when the
        // user switches Game Mode off.
        public void EndActiveBoost()
        {
            lock (_lock)
            {
                RestoreLastBoosted();
            }
        }

        private void StartBooster()
        {
            if (_boosterThread != null && _boosterThread.IsAlive) return;

            _boosterThread = new Thread(BoosterLoop)
            {
                IsBackground = true,
                Name = "GameModeBooster"
            };
            _boosterThread.Start();
        }

        private void BoosterLoop()
        {
            while (true)
            {
                lock (_lock)
                {
                    if (!_isEnabled) break;
                }

                try
                {
                    CheckForegroundProcess();
                }
                catch (Exception ex)
                {
                    EngineLog.Warn($"[GameMode] Error in check loop: {ex.Message}");
                }

                Thread.Sleep(1000); // Check active window every 1 second
            }
        }

        private void CheckForegroundProcess()
        {
            // Works in service mode too: the UI reports the foreground process (see ForegroundTracker).
            int pid = ForegroundTracker.GetForegroundProcessId();
            if (pid == 0) return;

            // If it's already the boosted game, do nothing
            if (pid == _lastBoostedPid) return;

            try
            {
                using (var proc = Process.GetProcessById((int)pid))
                {
                    string procName = proc.ProcessName;
                    
                    // Don't boost critical system interfaces or our own programs
                    if (procName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("TuneUpEngine", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("TuneUpUI", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                    {
                        RestoreLastBoosted();
                        return;
                    }

                    if (IsProcessAGame(proc))
                    {
                        // Restore any previous boosted game first
                        RestoreLastBoosted();

                        // Boost new game
                        try
                        {
                            _lastOriginalPriority = proc.PriorityClass;
                            _lastOriginalAffinity = proc.ProcessorAffinity;

                            if (_lastOriginalPriority != ProcessPriorityClass.High && 
                                _lastOriginalPriority != ProcessPriorityClass.RealTime)
                            {
                                proc.PriorityClass = ProcessPriorityClass.High;
                            }

                            // Core placement follows Microsoft's guidance for games
                            // (learn.microsoft.com, "Game Performance in Hybrid CPU Environments"):
                            //  * Ordinary CPUs (one core type): don't hard-pin — a pinned thread can be
                            //    starved by other work on its core. Only the priority is raised.
                            //  * Hybrid CPUs with at least 3 performance cores: keep the game on the
                            //    performance cores, free to float across all of them.
                            // (Previously every CPU was pinned to "all cores but core 0", and on hybrid
                            // CPUs the core classes were read backwards, pinning games to E-cores.)
                            var topology = CoreDirector.GetTopology();
                            if (topology.HasHybridCores && topology.PCoreCount >= 3 && topology.PCoresMask != IntPtr.Zero)
                            {
                                proc.ProcessorAffinity = topology.PCoresMask;
                                EngineLog.Info($"[GameMode] Keeping {procName} on {topology.PCoreCount} P-cores (mask: 0x{topology.PCoresMask.ToInt64():X})");
                            }

                            _lastBoostedPid = proc.Id;
                            _lastBoostedName = procName;
                            
                            // Apply Active Enhancements
                            if (!string.IsNullOrEmpty(_ultimatePlanGuid) &&
                                _journal.CapturePowerPlan(BoostJournalOwner, transient: true))
                            {
                                _journal.Commands.SetActivePowerPlan(_ultimatePlanGuid);
                            }
                            NtSetTimerResolution(5000, true, out uint _); // 0.5ms Timer

                            OnGameBoosted?.Invoke(procName, proc.Id);
                        }
                        catch (Exception ex)
                        {
                            EngineLog.Warn($"[GameMode] Failed to boost {procName}: {ex.Message}");
                        }
                    }
                    else
                    {
                        // Active foreground window is not a game, restore previous game priority
                        RestoreLastBoosted();
                    }
                }
            }
            catch
            {
                // Process might have exited or access denied
            }
        }

        private bool IsProcessAGame(Process proc)
        {
            // 1. Check Whitelist
            if (_gameWhitelist.Contains(proc.ProcessName)) return true;

            // 2. Check File Path Heuristics
            try
            {
                string exePath = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    exePath = exePath.ToLower();
                    foreach (var keyword in _gameKeywords)
                    {
                        if (exePath.Contains(keyword))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // MainModule access might fail due to privilege restrictions on standard processes
            }

            return false;
        }

        private void RestoreLastBoosted()
        {
            if (_lastBoostedPid == 0) return;

            try
            {
                // Revert Active Enhancements
                NtSetTimerResolution(5000, false, out uint _);
                _journal.Release(BoostJournalOwner); // Back to the user's own power plan

                using (var proc = Process.GetProcessById(_lastBoostedPid))
                {
                    proc.PriorityClass = _lastOriginalPriority;
                    if (_lastOriginalAffinity != IntPtr.Zero)
                    {
                        proc.ProcessorAffinity = _lastOriginalAffinity;
                    }
                    OnGameBoostRestored?.Invoke(_lastBoostedName, _lastBoostedPid);
                }
            }
            catch
            {
                // Process exited or already closed
            }
            finally
            {
                _lastBoostedPid = 0;
                _lastBoostedName = "";
            }
        }

        // Returns the GUID of an Ultimate Performance plan that is actually present in the plan list,
        // creating our own copy of the template only if none exists. Returns "" if no usable plan can
        // be found or created, in which case Game Mode simply leaves the power plan alone.
        private string EnsureUltimatePlan()
        {
            string list = ListPowerPlans();
            if (list == null) return ""; // Can't tell what exists — don't create anything.

            string existing = FindUltimatePlanGuid(list);
            if (!string.IsNullOrEmpty(existing)) return existing;

            RunPowercfg($"-duplicatescheme {UltimateTemplateGuid} {AppUltimatePlanGuid}");

            list = ListPowerPlans();
            return list != null && list.Contains(AppUltimatePlanGuid, StringComparison.OrdinalIgnoreCase)
                ? AppUltimatePlanGuid
                : "";
        }

        // Prefers, in order: the copy this app created, the template itself (present on some
        // editions), then any plan the user already made from it. The last check matches the plan
        // name, which is only reliable on English Windows — elsewhere we fall through to creating
        // our own copy once, which is still a single plan rather than one per engine start.
        internal static string FindUltimatePlanGuid(string powercfgListOutput)
        {
            if (string.IsNullOrEmpty(powercfgListOutput)) return "";

            if (powercfgListOutput.Contains(AppUltimatePlanGuid, StringComparison.OrdinalIgnoreCase))
                return AppUltimatePlanGuid;
            if (powercfgListOutput.Contains(UltimateTemplateGuid, StringComparison.OrdinalIgnoreCase))
                return UltimateTemplateGuid;

            foreach (var line in powercfgListOutput.Split('\n'))
            {
                if (!line.Contains("(Ultimate Performance)", StringComparison.OrdinalIgnoreCase)) continue;
                var match = System.Text.RegularExpressions.Regex.Match(line,
                    @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                if (match.Success) return match.Value;
            }

            return "";
        }

        private static string ListPowerPlans()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = "/list",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                return output;
            }
            catch
            {
                return null;
            }
        }

        private static void RunPowercfg(string arguments)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit(10000);
            }
            catch { }
        }
    }
}
