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
        
        private int _lastBoostedPid = 0;
        private string _lastBoostedName = "";
        private ProcessPriorityClass _lastOriginalPriority = ProcessPriorityClass.Normal;
        private IntPtr _lastOriginalAffinity;
        
        private string _originalPowerPlanGuid = "";
        private const string UltimatePowerPlanGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
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
                
                // 5. Ultimate Power Plan Import
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = $"-duplicatescheme {UltimatePowerPlanGuid}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit();
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
                            _originalPowerPlanGuid = GetActivePowerPlanGuid();
                            SetPowerPlan(UltimatePowerPlanGuid);
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
