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
        // Real page-list sizes from Windows; -1 when they couldn't be read (never an estimate).
        public long StandbyBytes { get; set; } = -1;
        public long UnusedBytes { get; set; } = -1;   // free + zeroed pages
        public long ModifiedBytes { get; set; } = -1;
        public List<string> EventLogs { get; set; }
    }

    public class IpcServer
    {
        public const string DefaultPipeName = "TuneUpEnginePipe";
        private readonly string _pipeName;
        private readonly ProBalanceGovernor _governor;
        private readonly StandbyListManager _memoryManager;
        private readonly Winapp2Parser _cleaner;
        private readonly SystemIntegrityAgent _integrityAgent;
        private readonly RegistryVerifier _registryVerifier;
        private readonly GameModeManager _gameMode;
        private readonly Winapp2Downloader _downloader;
        private readonly LatencyAuditor _latencyAuditor;
        private readonly EngineSettings _settings;
        // Records the original value of every system setting the engine changes, so each change
        // can be undone exactly (see ChangeJournal).
        private readonly ChangeJournal _journal;

        // Raised just before the engine exits on a ShutdownEngine request, so the host can hand
        // back anything temporary (boosted game priority, power plan, throttled processes).
        public event Action? ShuttingDown;
        private Thread _serverThread;
        private volatile bool _isRunning = false;

        // ── Connection security ────────────────────────────────────────────────
        // Extra accounts (besides the engine's own) allowed to open the pipe. The UI passes its own
        // SID with --allow-sid when it launches the engine, which covers "over-the-shoulder" UAC,
        // where the engine runs as a different (admin) account from the logged-in user.
        private readonly IReadOnlyList<SecurityIdentifier> _allowedClientSids;
        // In service mode the engine runs as SYSTEM, so "the engine's own account" doesn't include
        // the desktop user; fall back to interactive logons (still filtered by ClientVerifier).
        private readonly bool _allowInteractiveUsers;
        private readonly bool _isServiceMode;
        private readonly Func<NamedPipeServerStream, ClientContext> _contextFactory;

        // Commands that change machine-wide state. Since every signed-in user can reach the
        // service's pipe, these require an administrator account — otherwise a standard user could
        // use the engine to make system changes they aren't allowed to make themselves.
        // (Per-user work — scanning and cleaning your own files, your own HKCU tweaks — is open to
        // everyone; SetRegistryTweak, CleanJunk and RepairRegistry check per item.)
        internal static readonly IReadOnlySet<string> AdministratorCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "SetGovernorState", "SetAutoPurgeState", "SetGameModeState", "PurgeStandbyList",
            "ProvisionMSIInterrupts", "RevertMSIInterrupts", "UpdateCleanerDefinitions",
            "RunSystemRepair", "SetServiceState", "RemoveAppxPackage",
            "ApplyNetworkOptimizations", "RevertNetworkOptimizations", "MigrateDirectory",
            "StartLatencyAudit", "StopLatencyAudit", "ShutdownEngine",
        };
        private readonly IReadOnlyList<string> _trustedClientPaths;
        private NamedPipeServerStream? _firstInstance;

        // Requests are single JSON lines. Destructive commands now carry only IDs, so nothing
        // legitimate comes close to this; it stops a client from making the elevated engine buffer
        // an unbounded line in memory.
        internal const int MaxRequestChars = 1024 * 1024;

        // Results of the engine's own scans. Destructive commands refer to these by ID only.
        private readonly ResultStore<ScanItem> _scanResults = new(item => item.Id);
        private readonly ResultStore<RegistryIssue> _registryResults = new(issue => issue.Id);

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
            EngineSettings settings,
            IReadOnlyList<SecurityIdentifier>? allowedClientSids = null,
            bool allowInteractiveUsers = false,
            bool isServiceMode = false,
            string pipeName = DefaultPipeName,  // Overridable so tests never touch the real engine's pipe
            IReadOnlyList<string>? trustedClientPaths = null, // Test-only override of the trusted UI location(s)
            ChangeJournal? journal = null,
            Func<NamedPipeServerStream, ClientContext>? contextFactory = null) // Test-only: simulate other users
        {
            _contextFactory = contextFactory ?? ClientContext.FromPipe;
            _journal = journal ?? new ChangeJournal(null);
            _journal.OnLog += msg => LogEvent(msg);
            _pipeName = pipeName;
            _allowedClientSids = allowedClientSids ?? Array.Empty<SecurityIdentifier>();
            _allowInteractiveUsers = allowInteractiveUsers;
            _isServiceMode = isServiceMode;
            _trustedClientPaths = trustedClientPaths ?? ClientVerifier.TrustedClientPaths(AppContext.BaseDirectory);

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
                LogEvent($"[Memory] Standby list auto-purged. Freed {bytesFreed / (1024.0 * 1024.0):F1} MB");
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
                LogEvent($"[LatencyAuditor] ⚠️ {record.Name} spent {record.CurrentDpcPercent + record.CurrentInterruptPercent:F1}% of its time on driver DPCs/interrupts");
            };
        }

        public void Start()
        {
            if (_isRunning) return;

            // Claim the pipe name before anything else can. FirstPipeInstance makes creation FAIL if
            // a pipe with this name already exists — otherwise another program could create
            // "TuneUpEnginePipe" first and the UI would unknowingly send its commands to it. A
            // failure here also means a second engine is already running. Either way we must not
            // start, so the exception propagates to the caller.
            _firstInstance = CreatePipeInstance(firstInstance: true);

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

        // This pipe belongs to the ELEVATED engine, which deletes files, writes HKLM, changes
        // services and runs DISM/SFC on request, so access is kept to the minimum:
        //   * the engine's own account — FullControl, so it can create further pipe instances;
        //   * any account the launching UI vouched for via --allow-sid — ReadWrite;
        //   * in service mode only, interactive logons — ReadWrite (the engine runs as SYSTEM).
        // Previously every interactive user could connect, i.e. any other person signed in to the
        // same PC could drive this user's elevated engine. Low-integrity (sandboxed) processes are
        // still blocked by the pipe's default medium mandatory label, and every connection is then
        // checked by ClientVerifier.
        private PipeSecurity BuildPipeSecurity()
        {
            var security = new PipeSecurity();

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
                security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));

            foreach (var sid in _allowedClientSids)
                security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));

            if (_allowInteractiveUsers)
            {
                var interactiveSid = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
                security.AddAccessRule(new PipeAccessRule(interactiveSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            }

            return security;
        }

        private NamedPipeServerStream CreatePipeInstance(bool firstInstance)
        {
            var options = PipeOptions.Asynchronous;
            if (firstInstance) options |= PipeOptions.FirstPipeInstance;

            return NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                options,
                0,
                0,
                BuildPipeSecurity());
        }

        private void ServerLoop()
        {
            while (_isRunning)
            {
                try
                {
                    NamedPipeServerStream pipeServer = _firstInstance ?? CreatePipeInstance(firstInstance: false);
                    _firstInstance = null;

                    pipeServer.WaitForConnection();

                    // Only the genuine UI may issue commands; anything else is dropped unanswered.
                    string? clientImage = ClientVerifier.GetClientImagePath(pipeServer);
                    if (!ClientVerifier.IsTrustedClient(clientImage, _trustedClientPaths))
                    {
                        Console.WriteLine($"[Engine IPC] Rejected connection from untrusted client: {clientImage ?? "(unknown process)"}");
                        LogEvent($"[Security] Rejected a connection from {Path.GetFileName(clientImage ?? "an unknown process")}");
                        try { pipeServer.Disconnect(); } catch { }
                        pipeServer.Dispose();
                        continue;
                    }

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

        // Reads one '\n'-terminated line, refusing lines longer than maxChars. Returns null at end
        // of stream or when the limit is exceeded (tooLong tells the two apart).
        internal static string? ReadLineBounded(TextReader reader, int maxChars, out bool tooLong)
        {
            tooLong = false;
            var sb = new StringBuilder();
            while (true)
            {
                int c = reader.Read();
                if (c == -1) return sb.Length == 0 ? null : sb.ToString();
                if (c == '\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                if (sb.Length >= maxChars)
                {
                    tooLong = true;
                    return null;
                }
                sb.Append((char)c);
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

                    // Who is this connection for? Everything per-user (HKCU, personal folders, file
                    // deletion inside the profile) is done for — and as — this user. Windows only
                    // allows a pipe server to act as its client after reading from the pipe, so this
                    // is resolved once the first request line has arrived.
                    ClientContext? ctx = null;

                    while (_isRunning && pipeServer.IsConnected)
                    {
                        string? requestLine = ReadLineBounded(reader, MaxRequestChars, out bool tooLong);
                        if (tooLong)
                        {
                            SendResponse(writer, new IpcResponse { Status = "Error", Message = "Request too large." });
                            break;
                        }
                        if (string.IsNullOrEmpty(requestLine)) break;

                        if (ctx == null)
                        {
                            try
                            {
                                ctx = _contextFactory(pipeServer);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Engine IPC] Could not identify client: {ex.Message}");
                                SendResponse(writer, new IpcResponse { Status = "Error", Message = "The engine could not identify your user account. Please update TuneUp." });
                                break;
                            }
                        }

                        try
                        {
                            var request = JsonSerializer.Deserialize<IpcRequest>(requestLine);
                            if (request?.Command == null)
                                throw new JsonException("Missing command.");
                            HandleRequest(request, writer, ctx);
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

        // Runs a long operation off the session's read loop. The client blocks until it receives a
        // final (non-Progress) reply, so an exception inside the job must still produce one —
        // otherwise the UI would wait on the pipe forever.
        private void RunInBackground(StreamWriter writer, string operation, Action job)
        {
            Task.Run(() =>
            {
                try
                {
                    job();
                }
                catch (Exception ex)
                {
                    LogEvent($"[{operation}] Failed: {ex.Message}");
                    SendResponse(writer, new IpcResponse { Status = "Error", Message = $"{operation} failed: {ex.Message}" });
                }
            });
        }

        private static bool IsInSession(int pid, int sessionId)
        {
            if (pid <= 0) return false;
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                return sessionId < 0 || proc.SessionId == sessionId;
            }
            catch
            {
                return false;
            }
        }

        private void HandleRequest(IpcRequest request, StreamWriter writer, ClientContext ctx)
        {
            Console.WriteLine($"[Engine IPC] Handling request: {request.Command}");

            if (AdministratorCommands.Contains(request.Command) && !ctx.IsAdministrator)
            {
                SendResponse(writer, new IpcResponse { Status = "Error", Message = "This needs an administrator account." });
                return;
            }

            switch (request.Command)
            {
                // ── Foreground reporting (service mode) ──────────────────────────────
                // A service can't see the user's desktop, so the UI reports which process is in
                // front. Only processes in the reporting user's own session are accepted.
                case "ReportForeground":
                    if (int.TryParse(request.Payload, out int fgPid) && IsInSession(fgPid, ctx.SessionId))
                    {
                        ForegroundTracker.Report(fgPid);
                        SendResponse(writer, new IpcResponse { Status = "Success" });
                    }
                    else
                    {
                        SendResponse(writer, new IpcResponse { Status = "Error", Message = "Invalid foreground process." });
                    }
                    break;

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
                    if (_memoryManager.GetMemoryLists() is MemoryLists lists)
                    {
                        stats.StandbyBytes = lists.StandbyBytes;
                        stats.UnusedBytes = lists.UnusedBytes;
                        stats.ModifiedBytes = lists.ModifiedBytes;
                    }

                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(stats)
                    });
                    break;

                case "SetGovernorState":
                    if (!bool.TryParse(request.Payload, out bool govEnabled))
                    {
                        SendResponse(writer, new IpcResponse { Status = "Error", Message = "Invalid payload for SetGovernorState" });
                        break;
                    }
                    _governor.IsEnabled = govEnabled;
                    _settings.ProBalanceEnabled = govEnabled;
                    _settings.Save();
                    LogEvent($"ProBalance governor state set to: {(govEnabled ? "ENABLED" : "DISABLED")}");
                    SendResponse(writer, new IpcResponse { Status = "Success" });
                    break;

                case "SetAutoPurgeState":
                    if (!bool.TryParse(request.Payload, out bool purgeEnabled))
                    {
                        SendResponse(writer, new IpcResponse { Status = "Error", Message = "Invalid payload for SetAutoPurgeState" });
                        break;
                    }
                    _memoryManager.IsAutoPurging = purgeEnabled;
                    _settings.AutoPurgeEnabled = purgeEnabled;
                    _settings.Save();
                    LogEvent($"Standby list auto-purging set to: {(purgeEnabled ? "ENABLED" : "DISABLED")}");
                    SendResponse(writer, new IpcResponse { Status = "Success" });
                    break;

                case "SetGameModeState":
                    if (!bool.TryParse(request.Payload, out bool gameEnabled))
                    {
                        SendResponse(writer, new IpcResponse { Status = "Error", Message = "Invalid payload for SetGameModeState" });
                        break;
                    }
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
                    string provisionResult = HardwareController.EnableMSI(_journal);
                    LogEvent($"MSI Provisioning Executed: {provisionResult}");
                    SendResponse(writer, new IpcResponse { Status = "Success", Payload = provisionResult });
                    break;

                case "RevertMSIInterrupts":
                    string msiRevertResult = HardwareController.RevertMSI(_journal);
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
                    RunInBackground(writer, "Definitions sync", () =>
                    {
                        bool success = _downloader.SyncLatestDefinitionsAsync(winapp2Path).GetAwaiter().GetResult();
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
                    var purge = _memoryManager.PurgeStandbyList();
                    if (!purge.Success)
                    {
                        LogEvent($"[Manual Purge] {purge.Error}");
                        SendResponse(writer, new IpcResponse { Status = "Error", Message = purge.Error });
                        break;
                    }
                    Interlocked.Add(ref _totalMemoryFreed, purge.FreedBytes);
                    LogEvent($"[Manual Purge] Standby list emptied. Freed {purge.FreedBytes / (1024.0 * 1024.0):F1} MB");
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = purge.FreedBytes.ToString()
                    });
                    break;

                case "ScanJunk":
                    SendResponse(writer, new IpcResponse { Status = "Progress", ProgressPercent = 5.0, Message = "Scanning file structures..." });
                    var scanResults = _cleaner.Scan(ctx, (ruleName, percent) =>
                    {
                        SendResponse(writer, new IpcResponse
                        {
                            Status = "Progress",
                            ProgressPercent = percent,
                            Message = $"Scanning {ruleName}..."
                        });
                    });

                    _scanResults.Replace("junk", scanResults, ctx.Sid);
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(scanResults)
                    });
                    break;

                case "CleanJunk":
                    // The payload is a list of result IDs from an earlier scan — never paths. Only
                    // items the engine itself found can be cleaned; unknown IDs match nothing.
                    var cleanItems = _scanResults.Take(JsonSerializer.Deserialize<List<string>>(request.Payload ?? "[]"), ctx.Sid);
                    if (cleanItems.Count == 0)
                    {
                        SendResponse(writer, new IpcResponse { Status = "Error", Message = "Nothing to clean. The results may be out of date — please scan again." });
                        break;
                    }
                    var cleanBytes = _cleaner.Clean(cleanItems, ctx, (ruleName, percent) =>
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
                    var emptyFoldersResults = emptyFoldersScanner.ScanEmptyFolders(ctx.Folders, (msg) =>
                    {
                        SendResponse(writer, new IpcResponse
                        {
                            Status = "Progress",
                            Message = msg
                        });
                    });
                    _scanResults.Replace("emptyFolders", emptyFoldersResults, ctx.Sid);
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(emptyFoldersResults)
                    });
                    break;

                case "ScanAdvancedDuplicates":
                    var dupScanner = new AdvancedScanner();
                    var dupResults = dupScanner.ScanDuplicateFiles(ctx.Folders, (msg) =>
                    {
                        SendResponse(writer, new IpcResponse
                        {
                            Status = "Progress",
                            Message = msg
                        });
                    });
                    _scanResults.Replace("duplicates", dupResults, ctx.Sid);
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(dupResults)
                    });
                    break;

                case "ScanRegistry":
                    var regIssues = _registryVerifier.Scan(ctx, (step, percent) =>
                    {
                        SendResponse(writer, new IpcResponse
                        {
                            Status = "Progress",
                            ProgressPercent = percent,
                            Message = step
                        });
                    });

                    _registryResults.Replace("registry", regIssues, ctx.Sid);
                    SendResponse(writer, new IpcResponse
                    {
                        Status = "Success",
                        Payload = JsonSerializer.Serialize(regIssues)
                    });
                    break;

                case "RepairRegistry":
                    // As with CleanJunk: IDs from the engine's own scan only, never raw registry paths.
                    var repairIssues = _registryResults.Take(JsonSerializer.Deserialize<List<string>>(request.Payload ?? "[]"), ctx.Sid);
                    if (repairIssues.Count == 0)
                    {
                        SendResponse(writer, new IpcResponse { Status = "Error", Message = "Nothing to repair. The results may be out of date — please scan again." });
                        break;
                    }
                    _registryVerifier.Repair(repairIssues, ctx, (step, percent) =>
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
                    var tweaks = RegistryTweaker.GetTweakStatuses(ctx);
                    var tweaksJson = JsonSerializer.Serialize(tweaks);
                    SendResponse(writer, new IpcResponse { Status = "Success", Payload = tweaksJson });
                    break;

                case "SetRegistryTweak":
                    if (!string.IsNullOrEmpty(request.Payload))
                    {
                        var parts = request.Payload.Split('|');
                        if (parts.Length == 2 && bool.TryParse(parts[1], out bool enable))
                        {
                            if (RegistryTweaker.RequiresAdministrator(parts[0]) && !ctx.IsAdministrator)
                            {
                                SendResponse(writer, new IpcResponse { Status = "Error", Message = "This tweak changes the whole PC and needs an administrator account." });
                                break;
                            }
                            bool ok = RegistryTweaker.SetTweakStatus(parts[0], enable, _journal, ctx);
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
                    var debloat = DebloatGuard.GetStatus(ctx);
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
                            string result = DebloatGuard.SetServiceState(parts2[0], disable, _journal);
                            LogEvent($"[DebloatGuard] {result}");
                            bool changed = !result.StartsWith("Refused") && !result.StartsWith("Failed");
                            SendResponse(writer, new IpcResponse { Status = changed ? "Success" : "Error", Message = result });
                        }
                        else
                            SendResponse(writer, new IpcResponse { Status = "Error", Message = "Invalid payload" });
                    }
                    break;

                case "RemoveAppxPackage":
                    RunInBackground(writer, "AppX removal", () =>
                    {
                        string r = DebloatGuard.RemoveAppxPackage(request.Payload,
                            msg => SendResponse(writer, new IpcResponse { Status = "Progress", Message = msg }), ctx);
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
                    RunInBackground(writer, "Network optimization", () =>
                    {
                        string netResult = NetOptimizer.ApplyOptimizations(
                            msg => SendResponse(writer, new IpcResponse { Status = "Progress", Message = msg }), _journal);
                        LogEvent($"[NetOptimizer] {netResult}");
                        SendResponse(writer, new IpcResponse { Status = "Success", Message = netResult });
                    });
                    break;

                case "RevertNetworkOptimizations":
                    RunInBackground(writer, "Network revert", () =>
                    {
                        string revertResult = NetOptimizer.RevertToDefaults(
                            msg => SendResponse(writer, new IpcResponse { Status = "Progress", Message = msg }), _journal);
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
                            RunInBackground(writer, "Migration", () =>
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
                    // The service is shared by every user on the PC and keeps working in the
                    // background (ProBalance, Game Mode); closing one user's UI must not stop it.
                    if (_isServiceMode)
                    {
                        SendResponse(writer, new IpcResponse { Status = "Error", Message = "The engine runs as a Windows service and keeps running in the background." });
                        break;
                    }
                    SendResponse(writer, new IpcResponse { Status = "Success", Message = "Engine shutting down." });
                    Task.Run(() =>
                    {
                        Thread.Sleep(100);
                        try { ShuttingDown?.Invoke(); }
                        catch (Exception ex) { Console.WriteLine($"[Engine] Shutdown cleanup failed: {ex.Message}"); }
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

