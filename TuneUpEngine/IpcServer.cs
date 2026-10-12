using System;
using System.Collections.Concurrent;
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
        public List<string> EventLogs { get; set; } = new();
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
        private Thread? _serverThread;
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
            IpcCommands.SetGovernorState, IpcCommands.SetAutoPurgeState, IpcCommands.SetGameModeState,
            IpcCommands.PurgeStandbyList, IpcCommands.ProvisionMSIInterrupts, IpcCommands.RevertMSIInterrupts,
            IpcCommands.UpdateCleanerDefinitions, IpcCommands.RunSystemRepair, IpcCommands.SetServiceState,
            IpcCommands.RemoveAppxPackage, IpcCommands.ApplyNetworkOptimizations, IpcCommands.RevertNetworkOptimizations,
            IpcCommands.MigrateDirectory, IpcCommands.UndoMigration, IpcCommands.StartLatencyAudit,
            IpcCommands.StopLatencyAudit, IpcCommands.ShutdownEngine,
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
                LogEvent($"[LatencyAuditor] ⚠️ {record.Name} spent {record.WindowDpcPercent + record.WindowInterruptPercent:F1}% of its time on driver work over the last {LatencyAuditor.WindowSamples} seconds (DPC {record.WindowDpcPercent:F1}%, interrupt {record.WindowInterruptPercent:F1}%)");
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
                        EngineLog.Warn($"[Engine IPC] Rejected connection from untrusted client: {clientImage ?? "(unknown process)"}");
                        LogEvent($"[Security] Rejected a connection from {Path.GetFileName(clientImage ?? "an unknown process")}");
                        try { pipeServer.Disconnect(); } catch { }
                        pipeServer.Dispose();
                        continue;
                    }

                    EngineLog.Debug("[Engine IPC] A client has connected.");

                    // Handle this client session in a separate background thread/Task
                    Task.Run(() => HandleClientSession(pipeServer));
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    EngineLog.Warn($"[Engine IPC] Server connection error: {ex.Message}");
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

                    // Background jobs started on this connection, by RequestId, so the client can
                    // cancel them — and so they're cancelled if the client goes away mid-job.
                    var jobs = new ConcurrentDictionary<string, CancellationTokenSource>();

                    try
                    {
                    while (_isRunning && pipeServer.IsConnected)
                    {
                        string? requestLine = ReadLineBounded(reader, MaxRequestChars, out bool tooLong);
                        if (tooLong)
                        {
                            SendResponse(writer, new IpcResponse { Status = IpcStatus.Error, Message = "Request too large." });
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
                                EngineLog.Warn($"[Engine IPC] Could not identify client: {ex.Message}");
                                SendResponse(writer, new IpcResponse { Status = IpcStatus.Error, Message = "The engine could not identify your user account. Please update TuneUp." });
                                break;
                            }
                        }

                        try
                        {
                            var request = JsonSerializer.Deserialize<IpcRequest>(requestLine);
                            if (string.IsNullOrEmpty(request?.Command))
                                throw new JsonException("Missing command.");

                            if (request.Command == IpcCommands.CancelRequest)
                            {
                                // No reply of its own: the cancelled job sends its final reply.
                                if (request.Payload != null && jobs.TryGetValue(request.Payload, out var cts))
                                    cts.Cancel();
                                continue;
                            }

                            HandleRequest(request, writer, ctx, jobs);
                        }
                        catch (Exception ex)
                        {
                            SendResponse(writer, new IpcResponse
                            {
                                Status = IpcStatus.Error,
                                Message = $"Malformed request: {ex.Message}"
                            });
                        }
                    }
                    }
                    finally
                    {
                        // The client disconnected (or the session ended): stop its jobs. Every
                        // cancellable job stops at a safe point (see each operation).
                        foreach (var cts in jobs.Values)
                        {
                            try { cts.Cancel(); } catch (ObjectDisposedException) { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    EngineLog.Warn($"[Engine IPC] Client session error: {ex.Message}");
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
                    EngineLog.Debug($"[Engine IPC] Sending response: {response.Status}");
                    writer.WriteLine(json);
                    writer.Flush();
                }
                catch (Exception ex) { EngineLog.Warn($"[Engine IPC] SendResponse Error: {ex.Message}"); }
            }
        }

        // Runs a long operation off the session's read loop. The client blocks until it receives a
        // final (non-Progress) reply, so an exception inside the job must still produce one —
        // otherwise the UI would wait on the pipe forever.
        // The job receives a token that fires when the client sends CancelRequest for this
        // request's ID, or disconnects.
        private void RunInBackground(IpcRequest request, StreamWriter writer,
            ConcurrentDictionary<string, CancellationTokenSource> jobs, string operation, Action<CancellationToken> job)
        {
            var cts = new CancellationTokenSource();
            string? jobId = request.RequestId;
            if (!string.IsNullOrEmpty(jobId)) jobs[jobId] = cts;

            Task.Run(() =>
            {
                try
                {
                    job(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    LogEvent($"[{operation}] Cancelled.");
                    SendResponse(writer, new IpcResponse { Status = IpcStatus.Error, Message = $"{operation} cancelled.", RequestId = jobId });
                }
                catch (Exception ex)
                {
                    LogEvent($"[{operation}] Failed: {ex.Message}");
                    SendResponse(writer, new IpcResponse { Status = IpcStatus.Error, Message = $"{operation} failed: {ex.Message}", RequestId = jobId });
                }
                finally
                {
                    if (!string.IsNullOrEmpty(jobId)) jobs.TryRemove(jobId, out _);
                    cts.Dispose();
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

        private void HandleRequest(IpcRequest request, StreamWriter writer, ClientContext ctx,
            ConcurrentDictionary<string, CancellationTokenSource> jobs)
        {
            // Every reply to this request carries its RequestId.
            void Send(IpcResponse response)
            {
                response.RequestId = request.RequestId;
                SendResponse(writer, response);
            }

            EngineLog.Debug($"[Engine IPC] Handling request: {request.Command}");

            if (AdministratorCommands.Contains(request.Command) && !ctx.IsAdministrator)
            {
                Send(new IpcResponse { Status = IpcStatus.Error, Message = "This needs an administrator account." });
                return;
            }

            switch (request.Command)
            {
                // ── Foreground reporting (service mode) ──────────────────────────────
                // A service can't see the user's desktop, so the UI reports which process is in
                // front. Only processes in the reporting user's own session are accepted.
                case IpcCommands.ReportForeground:
                    if (int.TryParse(request.Payload, out int fgPid) && IsInSession(fgPid, ctx.SessionId))
                    {
                        ForegroundTracker.Report(fgPid);
                        Send(new IpcResponse { Status = IpcStatus.Success });
                    }
                    else
                    {
                        Send(new IpcResponse { Status = IpcStatus.Error, Message = "Invalid foreground process." });
                    }
                    break;

                case IpcCommands.GetEngineStats:
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

                    Send(new IpcResponse
                    {
                        Status = IpcStatus.Success,
                        Payload = JsonSerializer.Serialize(stats)
                    });
                    break;

                case IpcCommands.SetGovernorState:
                    if (!bool.TryParse(request.Payload, out bool govEnabled))
                    {
                        Send(new IpcResponse { Status = IpcStatus.Error, Message = "Invalid payload for SetGovernorState" });
                        break;
                    }
                    _governor.IsEnabled = govEnabled;
                    _settings.ProBalanceEnabled = govEnabled;
                    _settings.Save();
                    LogEvent($"ProBalance governor state set to: {(govEnabled ? "ENABLED" : "DISABLED")}");
                    Send(new IpcResponse { Status = IpcStatus.Success });
                    break;

                case IpcCommands.SetAutoPurgeState:
                    if (!bool.TryParse(request.Payload, out bool purgeEnabled))
                    {
                        Send(new IpcResponse { Status = IpcStatus.Error, Message = "Invalid payload for SetAutoPurgeState" });
                        break;
                    }
                    _memoryManager.IsAutoPurging = purgeEnabled;
                    _settings.AutoPurgeEnabled = purgeEnabled;
                    _settings.Save();
                    LogEvent($"Standby list auto-purging set to: {(purgeEnabled ? "ENABLED" : "DISABLED")}");
                    Send(new IpcResponse { Status = IpcStatus.Success });
                    break;

                case IpcCommands.SetGameModeState:
                    if (!bool.TryParse(request.Payload, out bool gameEnabled))
                    {
                        Send(new IpcResponse { Status = IpcStatus.Error, Message = "Invalid payload for SetGameModeState" });
                        break;
                    }
                    _gameMode.IsEnabled = gameEnabled;
                    _settings.GameModeEnabled = gameEnabled;
                    _settings.Save();
                    LogEvent($"Game Mode Booster set to: {(gameEnabled ? "ENABLED" : "DISABLED")}");
                    Send(new IpcResponse { Status = IpcStatus.Success });
                    break;

                case IpcCommands.CheckMsiCompliance:
                    string complianceResult = HardwareController.CheckMsiCompliance();
                    Send(new IpcResponse { Status = IpcStatus.Success, Payload = complianceResult });
                    break;

                case IpcCommands.ProvisionMSIInterrupts:
                    string provisionResult = HardwareController.EnableMSI(_journal);
                    LogEvent($"MSI Provisioning Executed: {provisionResult}");
                    Send(new IpcResponse { Status = IpcStatus.Success, Payload = provisionResult });
                    break;

                case IpcCommands.RevertMSIInterrupts:
                    string msiRevertResult = HardwareController.RevertMSI(_journal);
                    LogEvent($"MSI Revert Executed: {msiRevertResult}");
                    Send(new IpcResponse
                    {
                        Status = msiRevertResult.StartsWith("Error") ? IpcStatus.Error : IpcStatus.Success,
                        Payload = msiRevertResult,
                        Message = msiRevertResult
                    });
                    break;

                case IpcCommands.UpdateCleanerDefinitions:
                    string winapp2Path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "winapp2.ini");
                    RunInBackground(request, writer, jobs, "Definitions sync", ct =>
                    {
                        bool success = _downloader.SyncLatestDefinitionsAsync(winapp2Path).GetAwaiter().GetResult();
                        if (success)
                        {
                            _cleaner.LoadExternalWinapp2Ini(winapp2Path);
                        }

                        Send(new IpcResponse
                        {
                            Status = success ? IpcStatus.Success : IpcStatus.Error,
                            Message = success ? "Community definitions successfully synced." : "Online sync failed."
                        });
                    });
                    break;

                case IpcCommands.PurgeStandbyList:
                    var purge = _memoryManager.PurgeStandbyList();
                    if (!purge.Success)
                    {
                        LogEvent($"[Manual Purge] {purge.Error}");
                        Send(new IpcResponse { Status = IpcStatus.Error, Message = purge.Error });
                        break;
                    }
                    Interlocked.Add(ref _totalMemoryFreed, purge.FreedBytes);
                    LogEvent($"[Manual Purge] Standby list emptied. Freed {purge.FreedBytes / (1024.0 * 1024.0):F1} MB");
                    Send(new IpcResponse
                    {
                        Status = IpcStatus.Success,
                        Payload = purge.FreedBytes.ToString()
                    });
                    break;

                case IpcCommands.ScanJunk:
                    RunInBackground(request, writer, jobs, "Junk scan", ct =>
                    {
                        Send(new IpcResponse { Status = IpcStatus.Progress, ProgressPercent = 5.0, Message = "Scanning file structures..." });
                        var scanResults = _cleaner.Scan(ctx, (ruleName, percent) =>
                        {
                            Send(new IpcResponse
                            {
                                Status = IpcStatus.Progress,
                                ProgressPercent = percent,
                                Message = $"Scanning {ruleName}..."
                            });
                        }, ct);

                        _scanResults.Replace("junk", scanResults, ctx.Sid);
                        Send(new IpcResponse
                        {
                            Status = IpcStatus.Success,
                            Payload = JsonSerializer.Serialize(scanResults)
                        });
                    });
                    break;

                case IpcCommands.CleanJunk:
                    // The payload is a list of result IDs from an earlier scan — never paths. Only
                    // items the engine itself found can be cleaned; unknown IDs match nothing.
                    var cleanItems = _scanResults.Take(JsonSerializer.Deserialize<List<string>>(request.Payload ?? "[]"), ctx.Sid);
                    if (cleanItems.Count == 0)
                    {
                        Send(new IpcResponse { Status = IpcStatus.Error, Message = "Nothing to clean. The results may be out of date — please scan again." });
                        break;
                    }
                    RunInBackground(request, writer, jobs, "Clean", ct =>
                    {
                        var cleanBytes = _cleaner.Clean(cleanItems, ctx, (ruleName, percent) =>
                        {
                            Send(new IpcResponse
                            {
                                Status = IpcStatus.Progress,
                                ProgressPercent = percent,
                                Message = $"Cleaning {ruleName}..."
                            });
                        }, ct);

                        // Cleaning stops between files when cancelled; report what was freed.
                        Send(ct.IsCancellationRequested
                            ? new IpcResponse { Status = IpcStatus.Error, Message = $"Clean cancelled after freeing {cleanBytes / (1024.0 * 1024.0):F1} MB.", Payload = cleanBytes.ToString() }
                            : new IpcResponse { Status = IpcStatus.Success, Payload = cleanBytes.ToString() });
                    });
                    break;

                case IpcCommands.ScanAdvancedEmptyFolders:
                    RunInBackground(request, writer, jobs, "Empty folder scan", ct =>
                    {
                        var emptyFoldersResults = new AdvancedScanner().ScanEmptyFolders(ctx.Folders,
                            msg => Send(new IpcResponse { Status = IpcStatus.Progress, Message = msg }), ct);
                        _scanResults.Replace("emptyFolders", emptyFoldersResults, ctx.Sid);
                        Send(new IpcResponse
                        {
                            Status = IpcStatus.Success,
                            Payload = JsonSerializer.Serialize(emptyFoldersResults)
                        });
                    });
                    break;

                case IpcCommands.ScanAdvancedDuplicates:
                    RunInBackground(request, writer, jobs, "Duplicate scan", ct =>
                    {
                        var dupResults = new AdvancedScanner().ScanDuplicateFiles(ctx.Folders,
                            msg => Send(new IpcResponse { Status = IpcStatus.Progress, Message = msg }), ct);
                        _scanResults.Replace("duplicates", dupResults, ctx.Sid);
                        Send(new IpcResponse
                        {
                            Status = IpcStatus.Success,
                            Payload = JsonSerializer.Serialize(dupResults)
                        });
                    });
                    break;

                case IpcCommands.ScanRegistry:
                    var regIssues = _registryVerifier.Scan(ctx, (step, percent) =>
                    {
                        Send(new IpcResponse
                        {
                            Status = IpcStatus.Progress,
                            ProgressPercent = percent,
                            Message = step
                        });
                    });

                    _registryResults.Replace("registry", regIssues, ctx.Sid);
                    Send(new IpcResponse
                    {
                        Status = IpcStatus.Success,
                        Payload = JsonSerializer.Serialize(regIssues)
                    });
                    break;

                case IpcCommands.RepairRegistry:
                    // As with CleanJunk: IDs from the engine's own scan only, never raw registry paths.
                    var repairIssues = _registryResults.Take(JsonSerializer.Deserialize<List<string>>(request.Payload ?? "[]"), ctx.Sid);
                    if (repairIssues.Count == 0)
                    {
                        Send(new IpcResponse { Status = IpcStatus.Error, Message = "Nothing to repair. The results may be out of date — please scan again." });
                        break;
                    }
                    var repairProblems = _registryVerifier.Repair(repairIssues, ctx, (step, percent) =>
                    {
                        Send(new IpcResponse
                        {
                            Status = IpcStatus.Progress,
                            ProgressPercent = percent,
                            Message = step
                        });
                    });

                    foreach (var problem in repairProblems) LogEvent($"[RegistryVerifier] Not repaired: {problem}");
                    Send(repairProblems.Count == 0
                        ? new IpcResponse { Status = IpcStatus.Success }
                        : new IpcResponse
                        {
                            Status = IpcStatus.Error,
                            Message = $"{repairIssues.Count - repairProblems.Count} of {repairIssues.Count} repaired. Not repaired: {string.Join("; ", repairProblems)}"
                        });
                    break;

                case IpcCommands.GetSystemIntegrityStatus:
                    var integrityReport = _integrityAgent.ScanIntegrityLogs();
                    Send(new IpcResponse
                    {
                        Status = IpcStatus.Success,
                        Payload = JsonSerializer.Serialize(integrityReport)
                    });
                    break;

                case IpcCommands.RunSystemRepair:
                    // Off the read loop (it can take an hour), but deliberately not cancellable:
                    // stopping DISM part-way through isn't something to offer.
                    RunInBackground(request, writer, jobs, "System repair", _ =>
                    {
                        var repair = _integrityAgent.RunIntegrityRepair((step, percent) =>
                        {
                            Send(new IpcResponse
                            {
                                Status = IpcStatus.Progress,
                                ProgressPercent = percent,
                                Message = step
                            });
                        });

                        LogEvent($"[Repair] {repair.Message}");
                        Send(new IpcResponse
                        {
                            Status = repair.Success ? IpcStatus.Success : IpcStatus.Error,
                            Message = repair.Message
                        });
                    });
                    break;

                case IpcCommands.GetRegistryTweaks:
                    var tweaks = RegistryTweaker.GetTweakStatuses(ctx);
                    var tweaksJson = JsonSerializer.Serialize(tweaks);
                    Send(new IpcResponse { Status = IpcStatus.Success, Payload = tweaksJson });
                    break;

                case IpcCommands.SetRegistryTweak:
                    if (IpcPayload.Deserialize<SetTweakRequest>(request.Payload) is { Name: { Length: > 0 } } tweakRequest)
                    {
                        if (RegistryTweaker.RequiresAdministrator(tweakRequest.Name) && !ctx.IsAdministrator)
                        {
                            Send(new IpcResponse { Status = IpcStatus.Error, Message = "This tweak changes the whole PC and needs an administrator account." });
                            break;
                        }
                        bool ok = RegistryTweaker.SetTweakStatus(tweakRequest.Name, tweakRequest.Enable, _journal, ctx);
                        Send(ok
                            ? new IpcResponse { Status = IpcStatus.Success }
                            : new IpcResponse { Status = IpcStatus.Error, Message = $"Could not {(tweakRequest.Enable ? "apply" : "revert")} '{tweakRequest.Name}' (unknown tweak, or the registry write failed; see the engine log)." });
                        break;
                    }
                    Send(new IpcResponse { Status = IpcStatus.Error, Message = "Invalid payload for SetRegistryTweak" });
                    break;

                // ── CoreDirector ────────────────────────────────────────────────────
                case IpcCommands.GetCoreTopology:
                    var topo = CoreDirector.GetTopology();
                    Send(new IpcResponse
                    {
                        Status = IpcStatus.Success,
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
                case IpcCommands.GetDebloatStatus:
                    var debloat = DebloatGuard.GetStatus(ctx);
                    Send(new IpcResponse
                    {
                        Status = IpcStatus.Success,
                        Payload = JsonSerializer.Serialize(debloat)
                    });
                    break;

                case IpcCommands.SetServiceState:
                    {
                        if (IpcPayload.Deserialize<SetServiceRequest>(request.Payload) is { Name: { Length: > 0 } } serviceRequest)
                        {
                            string result = DebloatGuard.SetServiceState(serviceRequest.Name, serviceRequest.Disable, _journal);
                            LogEvent($"[DebloatGuard] {result}");
                            bool changed = !result.StartsWith("Refused") && !result.StartsWith("Failed");
                            Send(new IpcResponse { Status = changed ? IpcStatus.Success : IpcStatus.Error, Message = result });
                        }
                        else
                            Send(new IpcResponse { Status = IpcStatus.Error, Message = "Invalid payload" });
                    }
                    break;

                case IpcCommands.RemoveAppxPackage:
                    RunInBackground(request, writer, jobs, "AppX removal", ct =>
                    {
                        string r = DebloatGuard.RemoveAppxPackage(request.Payload ?? "",
                            msg => Send(new IpcResponse { Status = IpcStatus.Progress, Message = msg }), ctx);
                        LogEvent($"[DebloatGuard] {r}");
                        // Refusals and failures used to come back as Success with an "Error..." message.
                        Send(new IpcResponse { Status = r.StartsWith("Error", StringComparison.Ordinal) ? IpcStatus.Error : IpcStatus.Success, Message = r });
                    });
                    break;

                // ── NetOptimizer ────────────────────────────────────────────────────
                case IpcCommands.GetNetworkOptimizationStatus:
                    var netSettings = NetOptimizer.GetCurrentSettings();
                    Send(new IpcResponse
                    {
                        Status = IpcStatus.Success,
                        Payload = JsonSerializer.Serialize(netSettings)
                    });
                    break;

                case IpcCommands.ApplyNetworkOptimizations:
                    RunInBackground(request, writer, jobs, "Network optimization", ct =>
                    {
                        string netResult = NetOptimizer.ApplyOptimizations(
                            msg => Send(new IpcResponse { Status = IpcStatus.Progress, Message = msg }), _journal);
                        LogEvent($"[NetOptimizer] {netResult}");
                        Send(new IpcResponse { Status = IpcStatus.Success, Message = netResult });
                    });
                    break;

                case IpcCommands.RevertNetworkOptimizations:
                    RunInBackground(request, writer, jobs, "Network revert", ct =>
                    {
                        string revertResult = NetOptimizer.RevertToDefaults(
                            msg => Send(new IpcResponse { Status = IpcStatus.Progress, Message = msg }), _journal);
                        LogEvent($"[NetOptimizer] {revertResult}");
                        Send(new IpcResponse { Status = IpcStatus.Success, Message = revertResult });
                    });
                    break;

                // ── StorageMigrator ─────────────────────────────────────────────────
                case IpcCommands.AnalyzeMigration:
                    var analysis = StorageMigrator.Analyze(request.Payload ?? "");
                    Send(new IpcResponse
                    {
                        Status = IpcStatus.Success,
                        Payload = JsonSerializer.Serialize(analysis)
                    });
                    break;

                case IpcCommands.MigrateDirectory:
                    {
                        if (IpcPayload.Deserialize<MigrateRequest>(request.Payload) is { Source: { Length: > 0 }, Destination: { Length: > 0 } } migration)
                        {
                            RunInBackground(request, writer, jobs, "Migration", ct =>
                            {
                                // Cancellable while copying; once the swap starts it runs to the end.
                                string migResult = StorageMigrator.Migrate(
                                    migration.Source, migration.Destination,
                                    (msg, pct) => Send(new IpcResponse
                                    {
                                        Status = IpcStatus.Progress,
                                        ProgressPercent = pct,
                                        Message = msg
                                    }), ct);
                                LogEvent($"[StorageMigrator] {migResult}");
                                Send(new IpcResponse
                                {
                                    Status = migResult.StartsWith("Success") ? IpcStatus.Success : IpcStatus.Error,
                                    Message = migResult
                                });
                            });
                        }
                        else
                            Send(new IpcResponse { Status = IpcStatus.Error, Message = "Invalid payload for MigrateDirectory" });
                    }
                    break;

                case IpcCommands.UndoMigration:
                    RunInBackground(request, writer, jobs, "Undo migration", ct =>
                    {
                        string undoResult = StorageMigrator.UndoMigration(request.Payload ?? "",
                            (msg, pct) => Send(new IpcResponse { Status = IpcStatus.Progress, ProgressPercent = pct, Message = msg }), ct);
                        LogEvent($"[StorageMigrator] {undoResult}");
                        Send(new IpcResponse
                        {
                            Status = undoResult.StartsWith("Success") ? IpcStatus.Success : IpcStatus.Error,
                            Message = undoResult
                        });
                    });
                    break;

                case IpcCommands.GetMigrationHistory:
                    var history = StorageMigrator.GetHistory();
                    Send(new IpcResponse
                    {
                        Status = IpcStatus.Success,
                        Payload = JsonSerializer.Serialize(history)
                    });
                    break;

                // ── LatencyAuditor ──────────────────────────────────────────────────
                case IpcCommands.StartLatencyAudit:
                    _latencyAuditor.Start();
                    Send(new IpcResponse { Status = IpcStatus.Success, Message = "Latency audit started." });
                    break;

                case IpcCommands.StopLatencyAudit:
                    _latencyAuditor.Stop();
                    Send(new IpcResponse { Status = IpcStatus.Success, Message = "Latency audit stopped." });
                    break;

                case IpcCommands.GetLatencyReport:
                    var latReport = _latencyAuditor.GetReport();
                    Send(new IpcResponse
                    {
                        Status = IpcStatus.Success,
                        Payload = JsonSerializer.Serialize(latReport)
                    });
                    break;

                case IpcCommands.ShutdownEngine:
                    // The service is shared by every user on the PC and keeps working in the
                    // background (ProBalance, Game Mode); closing one user's UI must not stop it.
                    if (_isServiceMode)
                    {
                        Send(new IpcResponse { Status = IpcStatus.Error, Message = "The engine runs as a Windows service and keeps running in the background." });
                        break;
                    }
                    EngineLog.Info("[Engine] Shutdown requested by the UI.");
                    Send(new IpcResponse { Status = IpcStatus.Success, Message = "Engine shutting down." });
                    Task.Run(() =>
                    {
                        Thread.Sleep(100);
                        try { ShuttingDown?.Invoke(); }
                        catch (Exception ex) { EngineLog.Warn($"[Engine] Shutdown cleanup failed: {ex.Message}"); }
                        Environment.Exit(0);
                    });
                    break;

                default:
                    Send(new IpcResponse
                    {
                        Status = IpcStatus.Error,
                        Message = $"Unknown command: {request.Command}"
                    });
                    break;
            }
        }
    }
}

