using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;

namespace TuneUpEngine
{
    class Program
    {
        static void Main(string[] args)
        {
            // 1. Check if launched in Windows Service mode
            bool isServiceMode = args.Length > 0 && args[0].Equals("--service", StringComparison.OrdinalIgnoreCase);

            // The record of every system setting the engine has changed (ProgramData, admin-only).
            // Created first: it locks the ProgramData folder down before the log folder is made in it.
            var journal = ChangeJournal.CreateDefault();

            // Log to %ProgramData%\RamRaidersTuneUpSuite\logs, and make sure a crash is recorded
            // there instead of vanishing (a service has no console to show it).
            EngineLog.Initialize();
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                EngineLog.Error($"[Engine] Unhandled exception: {e.ExceptionObject}");
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                EngineLog.Error($"[Engine] Unobserved task exception: {e.Exception}");
                e.SetObserved();
            };
            EngineLog.Info($"[Engine] Starting ({(isServiceMode ? "service" : "console")} mode), version {typeof(Program).Assembly.GetName().Version}, as {WindowsIdentity.GetCurrent().Name}.");

            // "--revert-all": undo every change this app has ever made, then exit. Used by the
            // uninstaller (stop the engine first).
            if (args.Any(a => a.Equals("--revert-all", StringComparison.OrdinalIgnoreCase)))
            {
                int failed = journal.ReleaseAll();
                EngineLog.Info(failed == 0
                    ? "[Engine] --revert-all: all changes reverted."
                    : $"[Engine] --revert-all: {failed} change(s) could not be reverted.");
                Environment.ExitCode = failed == 0 ? 0 : 1;
                return;
            }

            // 2. Initialize Engine Modules
            var governor = new ProBalanceGovernor();
            var memoryManager = new StandbyListManager();
            var cleaner = new Winapp2Parser();
            var integrityAgent = new SystemIntegrityAgent();
            var registryVerifier = new RegistryVerifier();
            var gameMode = new GameModeManager(journal);
            var downloader = new Winapp2Downloader();
            var latencyAuditor = new LatencyAuditor();

            // ProBalance must never throttle the game Game Mode has just boosted.
            governor.IsProcessExempt = pid => pid != 0 && pid == gameMode.BoostedProcessId;

            // Restore the user's last feature-toggle choices (falls back to sensible defaults on a
            // first run: ProBalance on, AutoPurge off, GameMode on). Enabling modules has side
            // effects (Game Mode writes registry and boot settings), so in console mode this only
            // happens after this instance has claimed the pipe — i.e. it is the only engine running.
            var engineSettings = EngineSettings.Load();
            void ApplySavedModuleStates()
            {
                // Crash recovery first: anything that was only meant to last while the previous
                // engine was running (e.g. the power plan switched for a boosted game) is undone,
                // and restores that failed last time (e.g. a user hive that wasn't loaded) retried.
                journal.ReleaseTransient();
                journal.RetryPendingRestores();

                governor.IsEnabled = engineSettings.ProBalanceEnabled;
                memoryManager.IsAutoPurging = engineSettings.AutoPurgeEnabled;
                gameMode.IsEnabled = engineSettings.GameModeEnabled;
            }

            // Load any previously synced winapp2.ini definitions on startup
            string winapp2Path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "winapp2.ini");
            if (File.Exists(winapp2Path))
            {
                cleaner.LoadExternalWinapp2Ini(winapp2Path);
            }

            // 3. Initialize IPC Server
            var ipcServer = new IpcServer(governor, memoryManager, cleaner, integrityAgent, registryVerifier, gameMode, downloader, latencyAuditor, engineSettings,
                allowedClientSids: ParseAllowedSids(args),
                allowInteractiveUsers: isServiceMode,
                isServiceMode: isServiceMode,
                journal: journal);

            // On any orderly exit, hand back everything temporary: the boosted game's priority and
            // the power plan, and every process ProBalance throttled. (Turning the governor off here
            // is not saved as the user's preference.) Persistent tweaks stay applied by design.
            int cleanedUp = 0;
            void ReleaseTemporaryChanges()
            {
                if (Interlocked.Exchange(ref cleanedUp, 1) == 1) return;
                gameMode.EndActiveBoost();
                governor.IsEnabled = false;
                journal.ReleaseTransient();
            }
            ipcServer.ShuttingDown += ReleaseTemporaryChanges;

            if (isServiceMode)
            {
                // Headless Windows Service Mode. Modules are enabled in OnStart, only after the pipe
                // has been claimed — a console engine left running would otherwise share the
                // machine with a second set of active modules.
                using (var service = new TuneUpService(governor, memoryManager, ipcServer, ReleaseTemporaryChanges, ApplySavedModuleStates))
                {
                    ServiceBase.Run(service);
                }
            }
            else
            {
                // Standard Console Mode
                Console.Title = "TuneUp Engine Background Service (2026)";
                Console.WriteLine("==================================================");
                Console.WriteLine("       TUNEUP ENGINE BACKGROUND SERVICE (2026)    ");
                Console.WriteLine("==================================================");
                Console.WriteLine($"Started at: {DateTime.Now}");

                bool isAdmin = IsRunningAsAdmin();
                if (!isAdmin)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    EngineLog.Warn("[Engine] Not running as Administrator — system-level features will fail or be skipped.");
                    Console.WriteLine("[Warning] Engine is not running as Administrator!");
                    Console.WriteLine("          Low-level memory APIs, process priority modifications,");
                    Console.WriteLine("          and system repairs (SFC/DISM) will fail or be skipped.");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("[Success] Engine is running with Administrator privileges.");
                    Console.ResetColor();
                }

                Console.WriteLine("[Engine] Initializing Named Pipe IPC Server...");
                try
                {
                    ipcServer.Start();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    // FirstPipeInstance failed: another engine — or some other program — already owns
                    // the pipe name. Never run alongside it.
                    Console.ForegroundColor = ConsoleColor.Red;
                    EngineLog.Error($"[Engine] The engine pipe is already in use (another TuneUp engine, or another program): {ex.Message}");
                    Console.ResetColor();
                    Environment.ExitCode = 1;
                    return;
                }

                ApplySavedModuleStates();

                Console.WriteLine("[Engine] Engine is fully loaded and running.");
                Console.WriteLine("[Engine] Press Ctrl+C to terminate the engine service.");
                Console.WriteLine("--------------------------------------------------");

                var keepAliveEvent = new ManualResetEvent(false);
                Console.CancelKeyPress += (sender, e) =>
                {
                    EngineLog.Info("[Engine] Ctrl+C received — shutting down.");
                    // Clean up before exiting rather than letting Ctrl+C kill the process mid-way.
                    e.Cancel = true;
                    ReleaseTemporaryChanges();
                    keepAliveEvent.Set();
                };

                keepAliveEvent.WaitOne();
            }
        }

        // "--allow-sid <SID>" (repeatable): extra accounts allowed to connect to the engine's pipe.
        // The UI passes its own user's SID when launching the engine, so it can still connect when
        // UAC elevation was done with a different administrator account. Malformed SIDs are ignored.
        internal static List<SecurityIdentifier> ParseAllowedSids(string[] args)
        {
            var sids = new List<SecurityIdentifier>();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!args[i].Equals("--allow-sid", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var sid = new SecurityIdentifier(args[i + 1]);
                    // Only real user accounts — never broad groups such as Everyone or Users.
                    if (sid.IsAccountSid()) sids.Add(sid);
                }
                catch (ArgumentException)
                {
                }
            }
            return sids;
        }

        private static bool IsRunningAsAdmin()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
