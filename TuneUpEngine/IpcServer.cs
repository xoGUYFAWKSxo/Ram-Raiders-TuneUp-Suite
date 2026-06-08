using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TuneUpEngine
{
    public class IpcRequest
    {
        public string Command { get; set; }
        public string Payload { get; set; }
    }

    public class IpcResponse
    {
        public string Status { get; set; } // "Success", "Error", "Progress"
        public double ProgressPercent { get; set; }
        public string Message { get; set; }
        public string Payload { get; set; }
    }

    public class EngineStatsPayload
    {
        public int CpuCount { get; set; }
        public ulong TotalMemoryBytes { get; set; }
        public ulong AvailableMemoryBytes { get; set; }
        public double MemoryFreePercent { get; set; }
        public bool ProBalanceEnabled { get; set; }
        public bool AutoPurgeEnabled { get; set; }
        public bool GameModeEnabled { get; set; }
        public long TotalMemoryFreedBytes { get; set; }
        public List<string> EventLogs { get; set; }
    }

    public class IpcServer
    {
        private const string PipeName = "TuneUpEnginePipe";
        private readonly ProBalanceGovernor _governor;
        private readonly StandbyListManager _memoryManager;
        private readonly Winapp2Parser _cleaner;
        private readonly SystemIntegrityAgent _integrityAgent;
        private readonly RegistryVerifier _registryVerifier;
        private readonly GameModeManager _gameMode;
        private readonly Winapp2Downloader _downloader;
        private readonly LatencyAuditor _latencyAuditor;
        private readonly EngineSettings _settings;
        private Thread _serverThread;
        private bool _isRunning = false;

        private readonly List<string> _eventLogs = new List<string>();
        private readonly object _logLock = new object();
        private long _totalMemoryFreed = 0;

        // One write-gate per client connection, keyed by that connection's StreamWriter. Several
        // command handlers (MigrateDirectory, RemoveAppxPackage, the network ops, definition sync)
        // stream progress from a background Task while the session's read loop is still running, so
        // SendResponse can be entered concurrently for the same connection. Serializing per writer
        // guarantees whole JSON lines are never interleaved on the pipe. ConditionalWeakTable keys on
        // reference identity and drops the entry when the writer is collected — nothing to clean up.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<StreamWriter, object> _writerLocks = new();

        public IpcServer(
            ProBalanceGovernor governor, 
            StandbyListManager memoryManager, 
            Winapp2Parser cleaner, 
            SystemIntegrityAgent integrityAgent, 
            RegistryVerifier registryVerifier,
            GameModeManager gameMode,
            Winapp2Downloader downloader,
            LatencyAuditor latencyAuditor,
            EngineSettings settings)
        {
            _governor = governor;
            _memoryManager = memoryManager;
            _cleaner = cleaner;
            _integrityAgent = integrityAgent;
            _registryVerifier = registryVerifier;
            _gameMode = gameMode;
            _downloader = downloader;
            _latencyAuditor = latencyAuditor;
            _settings = settings;

            // Register events to push to event log
            _governor.OnProcessThrottled += (name, pid, reason) =>
            {
                LogEvent($"[ProBalance] Throttled process: {name} (PID: {pid}) - {reason}");
            };
            _governor.OnProcessRestored += (name, pid) =>
            {
                LogEvent($"[ProBalance] Restored priority for process: {name} (PID: {pid})");
            };
            _memoryManager.OnAutoPurgeTriggered += (bytesFreed) =>
            {
                Interlocked.Add(ref _totalMemoryFreed, bytesFreed);
                LogEvent($"[Memory] Standby list auto-purged. Freed {bytesFreed / (1024 * 1024):F1} MB");
            };
            _gameMode.OnGameBoosted += (name, pid) =>
            {
                LogEvent($"[GameMode] Elevated active game priority to High: {name} (PID: {pid})");
            };
            _gameMode.OnGameBoostRestored += (name, pid) =>
            {
                LogEvent($"[GameMode] Restored active game priority: {name} (PID: {pid})");
            };
            _downloader.OnDownloadLog += (msg) =>
            {
                LogEvent(msg);
            };
            _latencyAuditor.OnLogMessage += (msg) =>
            {
                LogEvent(msg);
            };
            _latencyAuditor.OnSpikeDetected += (record) =>
            {
                LogEvent($"[LatencyAuditor] ⚠️ SPIKE: {record.FriendlyName} peaked at {record.MaxDpcMicroseconds:F0}µs DPC time");
            };
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "IpcServerThread"
            };
            _serverThread.Start();
        }

        private void LogEvent(string msg)
        {
            lock (_logLock)
            {
                _eventLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
                if (_eventLogs.Count > 100)
                {
                    _eventLogs.RemoveAt(0);
                }
            }
        }

        private void ServerLoop()
        {
            while (_isRunning)
            {
                try
                {
                    NamedPipeServerStream pipeServer;
                    if (OperatingSystem.IsWindows())
                    {
                        var security = new PipeSecurity();

                        // This pipe is owned by the ELEVATED engine and accepts commands that
                        // delete files, write HKLM, disable services, remove AppX packages and
                        // run DISM/SFC. Granting access to 'Everyone' (as a previous revision did)
                        // would let ANY local process — including a low-privilege or sandboxed one —
                        // drive the elevated engine, which is a local privilege-escalation vector.
                        //
                        // Instead we grant the minimum needed for the legitimate UI to connect:
                        //   1. The engine's own identity (the user who launched/elevated it, or
                        //      SYSTEM in service mode) FullControl, so the server can spin up
                        //      additional concurrent pipe instances.
                        //   2. Interactive logon users ReadWrite, so the desktop UI — which runs at
                        //      the user's normal (non-elevated) integrity — can connect. The
                        //      Interactive SID excludes remote, network, service, batch and
                        //      anonymous logons. Low-integrity / sandboxed processes are additionally
                        //      blocked by the pipe's default medium mandatory label (no write-up).
                        var currentUser = WindowsIdentity.GetCurrent().User;
                        if (currentUser != null)
                        {
                            security.AddAccessRule(new PipeAccessRule(
                                currentUser,
                                PipeAccessRights.FullControl,
                                AccessControlType.Allow));
                        }

                        var interactiveSid = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
                        security.AddAccessRule(new PipeAccessRule(
                            interactiveSid,
                            PipeAccessRights.ReadWrite,
                            AccessControlType.Allow));

                        pipeServer = NamedPipeServerStreamAcl.Create(
                            PipeName,
                            PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous,
                            0,
                            0,
                            security);
                    }
                    else
                    {
                        pipeServer = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);
                    }

                    pipeServer.WaitForConnection();

                    Console.WriteLine("[Engine IPC] A client has connected.");

                    // Handle this client session in a separate background thread/Task
                    Task.Run(() => HandleClientSession(pipeServer));
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Console.WriteLine($"[Engine IPC] Server connection error: {ex.Message}");
                    Thread.Sleep(250);
                }
            }
        }

        private void HandleClientSession(NamedPipeServerStream pipeServer)
        {
            using (pipeServer)
            {
                try
                {
                    var reader = new StreamReader(pipeServer, Encoding.UTF8);
                    var writer = new StreamWriter(pipeServer, Encoding.UTF8);

                    while (_isRunning && pipeServer.IsConnected)
                    {
                        string requestLine = reader.ReadLine();
                        if (string.IsNullOrEmpty(requestLine)) break;

                        try
                        {
                            var request = JsonSerializer.Deserialize<IpcRequest>(requestLine);
                            HandleRequest(request, writer);
                        }
                        catch (Exception ex)
                        {
                            SendResponse(writer, new IpcResponse
                            {
                                Status = "Error",
                                Message = $"Malformed request: {ex.Message}"
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Engine IPC] Client session error: {ex.Message}");
                }
            }
        }

        private void SendResponse(StreamWriter writer, IpcResponse response)
        {
            // Serialize all writes to this connection so a background handler's progress update can
            // never interleave mid-line with another response on the same pipe.
            object gate = _writerLocks.GetValue(writer, _ => new object());
            lock (gate)
            {
                try
                {
                    string json = JsonSerializer.Serialize(response);
                    Console.WriteLine($"[Engine IPC] Sending response: {response.Status}");
                    writer.WriteLine(json);
                    writer.Flush();
                }
                catch (Exception ex) { Console.WriteLine($"[Engine IPC] SendResponse Error: {ex.Message}"); }
            }
        }

        private void HandleRequest(IpcRequest request, StreamWriter writer)
        {
            Console.WriteLine($"[Engine IPC] Handling request: {request.Command}");
            switch (request.Command)
            {
                case "GetEngineStats":
                    var mem = _memoryManager.GetMemoryStatus();
                    List<string> logs;
                    lock (_logLock)
                    {
                        logs = new List<string>(_eventLogs);
                    }

                    var stats = new EngineStatsPayload
                    {
                        CpuCount = Environment.ProcessorCount,
                        TotalMemoryBytes = mem.TotalPhys,
                        AvailableMemoryBytes = mem.AvailPhys,
                        MemoryFreePercent = mem.FreePercent,
                        ProBalanceEnabled = _governor.IsEnabled,
                        AutoPurgeEnabled = _memoryManager.IsAutoPurging,
                        GameModeEnabled = _gameMode.IsEnabled,
                        TotalMemoryFreedBytes = Interlocked.Read(ref _totalMemoryFreed),
                        EventLogs = logs
                    };

                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(stats)
                    });
                    break;

                case "SetGovernorState":
                    bool govEnabled = bool.Parse(request.Payload);
                    _governor.IsEnabled = govEnabled;
                    _settings.ProBalanceEnabled = govEnabled;
                    _settings.Save();
                    LogEvent($"ProBalance governor state set to: {(govEnabled ? "ENABLED" : "DISABLED")}");
                    SendResponse(writer, new IpcResponse { Status = "Success" });
                    break;

                case "SetAutoPurgeState":
                    bool purgeEnabled = bool.Parse(request.Payload);
                    _memoryManager.IsAutoPurging = purgeEnabled;
                    _settings.AutoPurgeEnabled = purgeEnabled;
                    _settings.Save();
                    LogEvent($"Standby list auto-purging set to: {(purgeEnabled ? "ENABLED" : "DISABLED")}");
                    SendResponse(writer, new IpcResponse { Status = "Success" });
                    break;

                case "SetGameModeState":
                    bool gameEnabled = bool.Parse(request.Payload);
                    _gameMode.IsEnabled = gameEnabled;
                    _settings.GameModeEnabled = gameEnabled;
                    _settings.Save();
                    LogEvent($"Game Mode Booster set to: {(gameEnabled ? "ENABLED" : "DISABLED")}");
                    SendResponse(writer, new IpcResponse { Status = "Success" });
                    break;

                case "CheckMsiCompliance":
                    string complianceResult = HardwareController.CheckMsiCompliance();
                    SendResponse(writer, new IpcResponse { Status = "Success", Payload = complianceResult });
                    break;

                case "ProvisionMSIInterrupts":
                    string provisionResult = HardwareController.EnableMSI();
                    LogEvent($"MSI Provisioning Executed: {provisionResult}");
                    SendResponse(writer, new IpcResponse { Status = "Success", Payload = provisionResult });
                    break;

                case "RevertMSIInterrupts":
                    string msiRevertResult = HardwareController.RevertMSI();
                    LogEvent($"MSI Revert Executed: {msiRevertResult}");
                    SendResponse(writer, new IpcResponse
                    {
                        Status = msiRevertResult.StartsWith("Error") ? "Error" : "Success",
                        Payload = msiRevertResult,
                        Message = msiRevertResult
                    });
                    break;

                case "UpdateCleanerDefinitions":
                    string winapp2Path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "winapp2.ini");
                    Task.Run(async () =>
                    {
                        bool success = await _downloader.SyncLatestDefinitionsAsync(winapp2Path);
                        if (success)
                        {
                            _cleaner.LoadExternalWinapp2Ini(winapp2Path);
                        }

                        SendResponse(writer, new IpcResponse
                        {
                            Status = success ? "Success" : "Error",
                            Message = success ? "Community definitions successfully synced." : "Online sync failed."
                        });
                    });
                    break;

                case "PurgeStandbyList":
                    long freedBytes = _memoryManager.PurgeStandbyList();
                    Interlocked.Add(ref _totalMemoryFreed, freedBytes);
                    LogEvent($"[Manual Purge] Purged standby list pages. Freed {freedBytes / (1024 * 1024):F1} MB");
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = freedBytes.ToString()
                    });
                    break;

                case "ScanJunk":
                    SendResponse(writer, new IpcResponse { Status = "Progress", ProgressPercent = 5.0, Message = "Scanning file structures..." });
                    var scanResults = _cleaner.Scan((ruleName, percent) =>
                    {
                        SendResponse(writer, new IpcResponse
                        {
                            Status = "Progress",
                            ProgressPercent = percent,
                            Message = $"Scanning {ruleName}..."
                        });
                    });

                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(scanResults)
                    });
                    break;

                case "CleanJunk":
                    var cleanItems = JsonSerializer.Deserialize<List<ScanItem>>(request.Payload);
                    var cleanBytes = _cleaner.Clean(cleanItems, (ruleName, percent) =>
                    {
                        SendResponse(writer, new IpcResponse
                        {
                            Status = "Progress",
                            ProgressPercent = percent,
                            Message = $"Cleaning {ruleName}..."
                        });
                    });

                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = cleanBytes.ToString()
                    });
                    break;

                case "ScanAdvancedEmptyFolders":
                    var emptyFoldersScanner = new AdvancedScanner();
                    var emptyFoldersResults = emptyFoldersScanner.ScanEmptyFolders((msg) =>
                    {
                        SendResponse(writer, new IpcResponse
                        {
                            Status = "Progress",
                            Message = msg
                        });
                    });
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(emptyFoldersResults)
                    });
                    break;

                case "ScanAdvancedDuplicates":
                    var dupScanner = new AdvancedScanner();
                    var dupResults = dupScanner.ScanDuplicateFiles((msg) =>
                    {
                        SendResponse(writer, new IpcResponse
                        {
                            Status = "Progress",
                            Message = msg
                        });
                    });
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(dupResults)
                    });
                    break;

                case "ScanRegistry":
                    var regIssues = _registryVerifier.Scan((step, percent) =>
                    {
                        SendResponse(writer, new IpcResponse
                        {
                            Status = "Progress",
                            ProgressPercent = percent,
                            Message = step
                        });
                    });

                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(regIssues)
                    });
                    break;

                case "RepairRegistry":
                    var repairIssues = JsonSerializer.Deserialize<List<RegistryIssue>>(request.Payload);
                    _registryVerifier.Repair(repairIssues, (step, percent) =>
                    {
                        SendResponse(writer, new IpcResponse
                        {
                            Status = "Progress",
                            ProgressPercent = percent,
                            Message = step
                        });
                    });

                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success"
                    });
                    break;

                case "GetSystemIntegrityStatus":
                    var integrityReport = _integrityAgent.ScanIntegrityLogs();
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(integrityReport)
                    });
                    break;

                case "RunSystemRepair":
                    _integrityAgent.RunIntegrityRepair((step, percent) =>
                    {
                        SendResponse(writer, new IpcResponse
                        {
                            Status = "Progress",
                            ProgressPercent = percent,
                            Message = step
                        });
                    });

                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success"
                    });
                    break;

                case "GetRegistryTweaks":
                    var tweaks = RegistryTweaker.GetTweakStatuses();
                    var tweaksJson = JsonSerializer.Serialize(tweaks);
                    SendResponse(writer, new IpcResponse { Status = "Success", Payload = tweaksJson });
                    break;

                case "SetRegistryTweak":
                    if (!string.IsNullOrEmpty(request.Payload))
                    {
                        var parts = request.Payload.Split('|');
                        if (parts.Length == 2 && bool.TryParse(parts[1], out bool enable))
                        {
                            bool ok = RegistryTweaker.SetTweakStatus(parts[0], enable);
                            SendResponse(writer, new IpcResponse { Status = ok ? "Success" : "Error" });
                            break;
                        }
                    }
                    SendResponse(writer, new IpcResponse { Status = "Error", Message = "Invalid payload for SetRegistryTweak" });
                    break;

                // ── CoreDirector ────────────────────────────────────────────────────
                case "GetCoreTopology":
                    var topo = CoreDirector.GetTopology();
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(new
                        {
                            topo.CpuModel,
                            topo.HasHybridCores,
                            topo.PCoreCount,
                            topo.ECoreCount,
                            Architecture = topo.Architecture.ToString(),
                            topo.DetectionMethod
                        })
                    });
                    break;

                // ── DebloatGuard ────────────────────────────────────────────────────
                case "GetDebloatStatus":
                    var debloat = DebloatGuard.GetStatus();
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(debloat)
                    });
                    break;

                case "SetServiceState":
                    {
                        var parts2 = request.Payload?.Split('|');
                        if (parts2?.Length == 2 && bool.TryParse(parts2[1], out bool disable))
                        {
                            string result = DebloatGuard.SetServiceState(parts2[0], disable);
                            LogEvent($"[DebloatGuard] {result}");
                            SendResponse(writer, new IpcResponse { Status = "Success", Message = result });
                        }
                        else
                            SendResponse(writer, new IpcResponse { Status = "Error", Message = "Invalid payload" });
                    }
                    break;

                case "RemoveAppxPackage":
                    Task.Run(() =>
                    {
                        string r = DebloatGuard.RemoveAppxPackage(request.Payload,
                            msg => SendResponse(writer, new IpcResponse { Status = "Progress", Message = msg }));
                        LogEvent($"[DebloatGuard] {r}");
                        SendResponse(writer, new IpcResponse { Status = "Success", Message = r });
                    });
                    break;

                // ── NetOptimizer ────────────────────────────────────────────────────
                case "GetNetworkOptimizationStatus":
                    var netSettings = NetOptimizer.GetCurrentSettings();
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(netSettings)
                    });
                    break;

                case "ApplyNetworkOptimizations":
                    Task.Run(() =>
                    {
                        string netResult = NetOptimizer.ApplyOptimizations(
                            msg => SendResponse(writer, new IpcResponse { Status = "Progress", Message = msg }));
                        LogEvent($"[NetOptimizer] {netResult}");
                        SendResponse(writer, new IpcResponse { Status = "Success", Message = netResult });
                    });
                    break;

                case "RevertNetworkOptimizations":
                    Task.Run(() =>
                    {
                        string revertResult = NetOptimizer.RevertToDefaults(
                            msg => SendResponse(writer, new IpcResponse { Status = "Progress", Message = msg }));
                        LogEvent($"[NetOptimizer] {revertResult}");
                        SendResponse(writer, new IpcResponse { Status = "Success", Message = revertResult });
                    });
                    break;

                // ── StorageMigrator ─────────────────────────────────────────────────
                case "AnalyzeMigration":
                    var analysis = StorageMigrator.Analyze(request.Payload);
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(analysis)
                    });
                    break;

                case "MigrateDirectory":
                    {
                        var migParts = request.Payload?.Split('|');
                        if (migParts?.Length == 2)
                        {
                            Task.Run(() =>
                            {
                                string migResult = StorageMigrator.Migrate(
                                    migParts[0], migParts[1],
                                    (msg, pct) => SendResponse(writer, new IpcResponse
                                    {
                                        Status = "Progress",
                                        ProgressPercent = pct,
                                        Message = msg
                                    }));
                                LogEvent($"[StorageMigrator] {migResult}");
                                SendResponse(writer, new IpcResponse
                                {
                                    Status = migResult.StartsWith("Success") ? "Success" : "Error",
                                    Message = migResult
                                });
                            });
                        }
                        else
                            SendResponse(writer, new IpcResponse { Status = "Error", Message = "Invalid payload. Expected 'source|destination'" });
                    }
                    break;

                case "GetMigrationHistory":
                    var history = StorageMigrator.GetHistory();
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(history)
                    });
                    break;

                // ── LatencyAuditor ──────────────────────────────────────────────────
                case "StartLatencyAudit":
                    _latencyAuditor.Start();
                    SendResponse(writer, new IpcResponse { Status = "Success", Message = "Latency audit started." });
                    break;

                case "StopLatencyAudit":
                    _latencyAuditor.Stop();
                    SendResponse(writer, new IpcResponse { Status = "Success", Message = "Latency audit stopped." });
                    break;

                case "GetLatencyReport":
                    var latReport = _latencyAuditor.GetReport();
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(latReport)
                    });
                    break;

                case "ShutdownEngine":
                    SendResponse(writer, new IpcResponse { Status = "Success", Message = "Engine shutting down." });
                    Task.Run(() =>
                    {
                        Thread.Sleep(100);
                        Environment.Exit(0);
                    });
                    break;

                default:
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Error",
                        Message = $"Unknown command: {request.Command}"
                    });
                    break;
            }
        }
    }
}

