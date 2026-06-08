using System;
using System.IO;
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

            // 2. Initialize Engine Modules
            var governor = new ProBalanceGovernor();
            var memoryManager = new StandbyListManager();
            var cleaner = new Winapp2Parser();
            var integrityAgent = new SystemIntegrityAgent();
            var registryVerifier = new RegistryVerifier();
            var gameMode = new GameModeManager();
            var downloader = new Winapp2Downloader();
            var latencyAuditor = new LatencyAuditor();

            // Restore the user's last feature-toggle choices (falls back to sensible defaults on a
            // first run: ProBalance on, AutoPurge off, GameMode on).
            var engineSettings = EngineSettings.Load();
            governor.IsEnabled = engineSettings.ProBalanceEnabled;
            memoryManager.IsAutoPurging = engineSettings.AutoPurgeEnabled;
            gameMode.IsEnabled = engineSettings.GameModeEnabled;

            // Load any previously synced winapp2.ini definitions on startup
            string winapp2Path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "winapp2.ini");
            if (File.Exists(winapp2Path))
            {
                cleaner.LoadExternalWinapp2Ini(winapp2Path);
            }

            // 3. Initialize IPC Server
            var ipcServer = new IpcServer(governor, memoryManager, cleaner, integrityAgent, registryVerifier, gameMode, downloader, latencyAuditor, engineSettings);

            if (isServiceMode)
            {
                // Headless Windows Service Mode
                using (var service = new TuneUpService(governor, memoryManager, ipcServer))
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
                ipcServer.Start();

                Console.WriteLine("[Engine] Engine is fully loaded and running.");
                Console.WriteLine("[Engine] Press Ctrl+C to terminate the engine service.");
                Console.WriteLine("--------------------------------------------------");

                var keepAliveEvent = new ManualResetEvent(false);
                Console.CancelKeyPress += (sender, e) =>
                {
                    Console.WriteLine("[Engine] Terminating engine background service...");
                    keepAliveEvent.Set();
                };

                keepAliveEvent.WaitOne();
            }
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
