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
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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

        private string _originalPowerPlanGuid = "";

        // e9a42b02-… is Windows' hidden Ultimate Performance *template*. `powercfg -duplicatescheme`
        // copies it to a brand-new random GUID, so the template GUID normally never appears in
        // `powercfg /list` and can't be activated. The old code checked for and activated the template
        // GUID, so it duplicated the plan on every enable and then failed to switch to it. We now copy
        // the template to a fixed GUID that belongs to this app, which we can find and activate.
        private const string UltimateTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        private const string AppUltimatePlanGuid = "5c6a6e1d-7b0a-4d8e-9f3a-52a1d0c7e4b9";
        private string _ultimatePlanGuid = ""; // Resolved plan to activate while a game is boosted
        private int _cachedSystemResponsiveness = 20;
        private int _cachedGpuPriority = 8;
        private int _cachedGamePriority = 2;

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
                        Console.WriteLine($"[GameMode] Core topology: {topology.DetectionMethod} | Hybrid: {topology.HasHybridCores}");
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
                // 1. MMCSS SystemResponsiveness
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("SystemResponsiveness");
                        if (val != null) _cachedSystemResponsiveness = (int)val;
                        key.SetValue("SystemResponsiveness", 0, RegistryValueKind.DWord);
                    }
                }

                // 2. MMCSS Game Tasks Priority
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", true))
                {
                    if (key != null)
                    {
                        object gpuVal = key.GetValue("GPU Priority");
                        object prioVal = key.GetValue("Priority");
                        if (gpuVal != null) _cachedGpuPriority = (int)gpuVal;
                        if (prioVal != null) _cachedGamePriority = (int)prioVal;

                        key.SetValue("GPU Priority", 8, RegistryValueKind.DWord);
                        key.SetValue("Priority", 6, RegistryValueKind.DWord);
                    }
                }

                // 3. Kernel Timer Resolution
                using (var key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel"))
                {
                    key.SetValue("GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord);
                }

                // 4. BCDEdit Dynamic Tick
                Process.Start(new ProcessStartInfo
                {
                    FileName = "bcdedit",
                    Arguments = "/set disabledynamictick yes",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit();
                
                // 5. Ultimate Power Plan — resolve (creating it at most once) the plan to activate
                // while a game is boosted.
                _ultimatePlanGuid = EnsureUltimatePlan();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GameMode] Failed to apply global tweaks: {ex.Message}");
            }
        }

        private void RevertGlobalTweaks()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true))
                {
                    key?.SetValue("SystemResponsiveness", _cachedSystemResponsiveness, RegistryValueKind.DWord);
                }

                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", true))
                {
                    key?.SetValue("GPU Priority", _cachedGpuPriority, RegistryValueKind.DWord);
                    key?.SetValue("Priority", _cachedGamePriority, RegistryValueKind.DWord);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "bcdedit",
                    Arguments = "/set disabledynamictick no",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GameMode] Failed to revert global tweaks: {ex.Message}");
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
                    Debug.WriteLine($"[GameMode] Error in check loop: {ex.Message}");
                }

                Thread.Sleep(1000); // Check active window every 1 second
            }
        }

        private void CheckForegroundProcess()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return;

            GetWindowThreadProcessId(hWnd, out uint pid);
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

                            // Use CoreDirector to get P-core mask (or all-minus-Core-0 on standard CPUs)
                            var topology = CoreDirector.GetTopology();
                            IntPtr gameMask;
                            if (topology.HasHybridCores)
                            {
                                // Hybrid CPU: restrict game to P-cores only for maximum single-thread perf
                                gameMask = topology.PCoresMask;
                                Console.WriteLine($"[GameMode] Pinning {procName} to {topology.PCoreCount} P-cores (mask: 0x{gameMask.ToInt64():X})");
                            }
                            else
                            {
                                // Standard CPU: all cores except Core 0 (reserved for OS interrupts)
                                gameMask = topology.PCoresMask; // PCoresMask = allCores & ~1 on standard
                                Console.WriteLine($"[GameMode] Pinning {procName} to all cores minus Core 0 (mask: 0x{gameMask.ToInt64():X})");
                            }
                            proc.ProcessorAffinity = gameMask;

                            _lastBoostedPid = proc.Id;
                            _lastBoostedName = procName;
                            
                            // Apply Active Enhancements
                            if (!string.IsNullOrEmpty(_ultimatePlanGuid))
                            {
                                _originalPowerPlanGuid = GetActivePowerPlanGuid();
                                SetPowerPlan(_ultimatePlanGuid);
                            }
                            NtSetTimerResolution(5000, true, out uint _); // 0.5ms Timer

                            OnGameBoosted?.Invoke(procName, proc.Id);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[GameMode] Failed to boost {procName}: {ex.Message}");
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
                if (!string.IsNullOrEmpty(_originalPowerPlanGuid))
                {
                    SetPowerPlan(_originalPowerPlanGuid);
                    _originalPowerPlanGuid = "";
                }

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

        private string GetActivePowerPlanGuid()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = "/getactivescheme",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                
                if (output.Contains("GUID:"))
                {
                    int startIndex = output.IndexOf("GUID:") + 5;
                    int endIndex = output.IndexOf("(", startIndex);
                    if (endIndex > startIndex)
                    {
                        return output.Substring(startIndex, endIndex - startIndex).Trim();
                    }
                }
            }
            catch { }
            return "";
        }

        private void SetPowerPlan(string guid)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = $"-setactive {guid}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit();
            }
            catch { }
        }
    }
}
