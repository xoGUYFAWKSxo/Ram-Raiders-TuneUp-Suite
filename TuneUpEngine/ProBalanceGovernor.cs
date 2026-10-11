using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Management;

namespace TuneUpEngine
{
    public class ProBalanceGovernor
    {
        private class ProcessState
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public TimeSpan LastCpuTime { get; set; }
            public DateTime LastSampleTime { get; set; }
            public ProcessPriorityClass OriginalPriority { get; set; }
            public IntPtr OriginalAffinity { get; set; }
            public bool IsThrottled { get; set; }
            public int SpikeSamples { get; set; } // Consecutive samples exceeding threshold
        }

        private bool _isEnabled = false;
        private Thread _governorThread;
        private readonly object _lock = new object();
        private readonly Dictionary<int, ProcessState> _trackedProcesses = new Dictionary<int, ProcessState>();
        
        private ManagementEventWatcher _startWatcher;
        private ManagementEventWatcher _stopWatcher;
        
        // Critical system processes that should never be throttled
        private readonly HashSet<string> _whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Idle", "System", "Registry", "smss", "csrss", "wininit", "services", "lsass", 
            "svchost", "explorer", "TuneUpEngine", "TuneUpUI", "Taskmgr", "dwm", "spoolsv"
        };

        private readonly int _processorCount = Environment.ProcessorCount;
        private const int SampleIntervalMs = 500;
        private const int SpikeThresholdSamples = 4; // 4 * 500ms = 2000ms
        
        // Spike threshold is 95% of a single core's capacity.
        private readonly double _singleCoreSpikeThreshold;

        public event Action<string, int, string> OnProcessThrottled; // Name, ID, Reason
        public event Action<string, int> OnProcessRestored; // Name, ID

        // Extra processes that must never be throttled, supplied by the host. Program wires this to
        // the Game Mode booster so ProBalance can't demote the game Game Mode just elevated (games
        // routinely saturate a core, which is exactly what ProBalance's spike detector looks for).
        public Func<int, bool>? IsProcessExempt { get; set; }

        // The process that owns the window the user is interacting with. ProBalance exists to keep
        // the foreground app responsive, so that process is never the one it throttles. (Works in
        // service mode too: the UI reports it — see ForegroundTracker.)
        private static int GetForegroundProcessId() => ForegroundTracker.GetForegroundProcessId();

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
                        StartGovernor();
                    }
                    else
                    {
                        StopGovernor();
                    }
                }
            }
        }

        public ProBalanceGovernor()
        {
            _singleCoreSpikeThreshold = 95.0 / _processorCount;
        }

        public List<(int Id, string Name, double CpuUsage, bool IsThrottled)> GetActiveMonitoredProcesses()
        {
            lock (_lock)
            {
                var result = new List<(int, string, double, bool)>();
                var now = DateTime.UtcNow;
                foreach (var kp in _trackedProcesses.Values)
                {
                    if ((now - kp.LastSampleTime).TotalSeconds < 2.0)
                    {
                        result.Add((kp.Id, kp.Name, 0.0, kp.IsThrottled));
                    }
                }
                return result;
            }
        }

        private void StartGovernor()
        {
            InitializeWmiWatchers();

            // Pre-populate with existing processes
            lock (_lock)
            {
                _trackedProcesses.Clear();
                foreach (var proc in Process.GetProcesses())
                {
                    if (!_whitelist.Contains(proc.ProcessName))
                    {
                        try
                        {
                            _trackedProcesses[proc.Id] = new ProcessState
                            {
                                Id = proc.Id,
                                Name = proc.ProcessName,
                                LastCpuTime = proc.TotalProcessorTime,
                                LastSampleTime = DateTime.UtcNow,
                                OriginalPriority = proc.PriorityClass,
                                IsThrottled = false,
                                SpikeSamples = 0
                            };
                        }
                        catch { }
                    }
                    proc.Dispose();
                }
            }

            if (_governorThread != null && _governorThread.IsAlive) return;

            _governorThread = new Thread(GovernorLoop)
            {
                IsBackground = true,
                Name = "ProBalanceGovernor"
            };
            _governorThread.Start();
        }

        private void StopGovernor()
        {
            DisposeWmiWatchers();

            // Turning ProBalance off must hand back everything it took. Previously throttled
            // processes stayed at Below Normal (and pinned to E-cores) until they exited.
            lock (_lock)
            {
                foreach (var state in _trackedProcesses.Values)
                {
                    if (!state.IsThrottled) continue;
                    try
                    {
                        using var proc = Process.GetProcessById(state.Id);
                        // Guard against PID reuse: only touch the process we actually throttled.
                        if (proc.ProcessName.Equals(state.Name, StringComparison.OrdinalIgnoreCase))
                            RestoreProcess(proc, state);
                    }
                    catch
                    {
                        // Process already exited.
                    }
                    state.IsThrottled = false;
                    state.SpikeSamples = 0;
                }
            }
        }

        private void DisposeWmiWatchers()
        {
            try { _startWatcher?.Stop(); } catch { }
            try { _stopWatcher?.Stop(); } catch { }
            _startWatcher?.Dispose();
            _stopWatcher?.Dispose();
            _startWatcher = null;
            _stopWatcher = null;
        }

        private void RestoreProcess(Process proc, ProcessState state)
        {
            proc.PriorityClass = state.OriginalPriority;
            // Restore affinity if we changed it
            if (state.OriginalAffinity != IntPtr.Zero)
                proc.ProcessorAffinity = state.OriginalAffinity;
            state.IsThrottled = false;
            OnProcessRestored?.Invoke(state.Name, state.Id);
        }

        private void InitializeWmiWatchers()
        {
            // Re-enabling used to create a second pair of watchers without disposing the first.
            DisposeWmiWatchers();

            try
            {
                _startWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                _startWatcher.EventArrived += (sender, e) =>
                {
                    if (!_isEnabled) return;
                    try
                    {
                        int pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
                        string pName = e.NewEvent.Properties["ProcessName"].Value?.ToString() ?? "";

                        if (pName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            pName = pName.Substring(0, pName.Length - 4);
                        }

                        if (!_whitelist.Contains(pName))
                        {
                            lock (_lock)
                            {
                                if (!_trackedProcesses.ContainsKey(pid))
                                {
                                    using (var proc = Process.GetProcessById(pid))
                                    {
                                        _trackedProcesses[pid] = new ProcessState
                                        {
                                            Id = pid,
                                            Name = pName,
                                            LastCpuTime = proc.TotalProcessorTime,
                                            LastSampleTime = DateTime.UtcNow,
                                            OriginalPriority = proc.PriorityClass,
                                            IsThrottled = false,
                                            SpikeSamples = 0
                                        };
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                };
                _startWatcher.Start();

                _stopWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
                _stopWatcher.EventArrived += (sender, e) =>
                {
                    if (!_isEnabled) return;
                    try
                    {
                        int pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
                        lock (_lock)
                        {
                            _trackedProcesses.Remove(pid);
                        }
                    }
                    catch { }
                };
                _stopWatcher.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProBalance] Failed to initialize WMI Watchers: {ex.Message}");
            }
        }

        private void GovernorLoop()
        {
            while (true)
            {
                lock (_lock)
                {
                    if (!_isEnabled) break;
                }

                try
                {
                    MonitorProcesses();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProBalance] Error in monitor loop: {ex.Message}");
                }

                Thread.Sleep(SampleIntervalMs);
            }
        }

        private void MonitorProcesses()
        {
            var now = DateTime.UtcNow;
            List<int> deadProcesses = new List<int>();
            int foregroundPid = GetForegroundProcessId();
            var isExempt = IsProcessExempt;

            lock (_lock)
            {
                // Iterate over the locally tracked process dictionary, vastly reducing garbage collection vs GetProcesses()
                foreach (var kvp in _trackedProcesses)
                {
                    int pid = kvp.Key;
                    var state = kvp.Value;

                    Process proc = null;
                    try
                    {
                        proc = Process.GetProcessById(pid);
                        TimeSpan totalCpuTime = proc.TotalProcessorTime;
                        string pName = state.Name;

                        // Calculate CPU usage since last sample
                        double deltaMs = (now - state.LastSampleTime).TotalMilliseconds;
                        double cpuDeltaMs = (totalCpuTime - state.LastCpuTime).TotalMilliseconds;

                        state.LastCpuTime = totalCpuTime;
                        state.LastSampleTime = now;

                        if (deltaMs <= 0)
                        {
                            continue;
                        }

                        // The foreground app and the Game Mode-boosted game are never throttled. If
                        // one was throttled before it became exempt (e.g. the user alt-tabbed back to
                        // it), hand its priority back straight away.
                        if (pid == foregroundPid || (isExempt != null && isExempt(pid)))
                        {
                            state.SpikeSamples = 0;
                            if (state.IsThrottled)
                            {
                                try
                                {
                                    RestoreProcess(proc, state);
                                }
                                catch (Exception pEx)
                                {
                                    Debug.WriteLine($"[ProBalance] Failed to restore exempt process {pName} ({pid}): {pEx.Message}");
                                }
                            }
                            continue;
                        }

                        // Total CPU usage percent of the entire machine (0 - 100)
                        double cpuUsagePercent = (cpuDeltaMs / (deltaMs * _processorCount)) * 100.0;

                        // Check if it spikes a single core (> 95% of a core)
                        if (cpuUsagePercent > _singleCoreSpikeThreshold)
                        {
                            state.SpikeSamples++;
                            if (state.SpikeSamples >= SpikeThresholdSamples && !state.IsThrottled)
                            {
                                // Spike sustained for > 2000ms. Throttle priority.
                                try
                                {
                                    state.OriginalPriority = proc.PriorityClass;
                                    state.OriginalAffinity = proc.ProcessorAffinity;
                                    if (state.OriginalPriority == ProcessPriorityClass.Normal || 
                                        state.OriginalPriority == ProcessPriorityClass.AboveNormal || 
                                        state.OriginalPriority == ProcessPriorityClass.High)
                                    {
                                        proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                                        state.IsThrottled = true;

                                        // CoreDirector: if E-cores exist, exile this process there
                                        var topology = CoreDirector.GetTopology();
                                        if (topology.HasHybridCores && topology.ECoresMask != IntPtr.Zero)
                                        {
                                            proc.ProcessorAffinity = topology.ECoresMask;
                                            OnProcessThrottled?.Invoke(pName, pid, 
                                                $"Core spike: {cpuUsagePercent:F1}% → exiled to {topology.ECoreCount} E-cores");
                                        }
                                        else
                                        {
                                            OnProcessThrottled?.Invoke(pName, pid, $"Core spike: {cpuUsagePercent:F1}% (Threshold: {_singleCoreSpikeThreshold:F1}%)");
                                        }
                                    }
                                }
                                catch (Exception pEx)
                                {
                                    Debug.WriteLine($"[ProBalance] Failed to set priority for {pName} ({pid}): {pEx.Message}");
                                }
                            }
                        }
                        else
                        {
                            // Load is below threshold
                            if (state.SpikeSamples > 0)
                            {
                                state.SpikeSamples--;
                            }

                            if (state.SpikeSamples == 0 && state.IsThrottled)
                            {
                                // Load subsided. Restore priority.
                                try
                                {
                                    RestoreProcess(proc, state);
                                }
                                catch (Exception pEx)
                                {
                                    Debug.WriteLine($"[ProBalance] Failed to restore priority for {pName} ({pid}): {pEx.Message}");
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Process exited or access is denied
                        deadProcesses.Add(pid);
                    }
                    finally
                    {
                        proc?.Dispose();
                    }
                }

                // Cleanup dead processes
                foreach (var pid in deadProcesses)
                {
                    _trackedProcesses.Remove(pid);
                }
            }
        }
    }
}
