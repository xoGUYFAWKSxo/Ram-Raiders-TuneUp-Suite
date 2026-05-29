using System;
using System.ServiceProcess;

namespace TuneUpEngine
{
    public class TuneUpService : ServiceBase
    {
        private readonly ProBalanceGovernor _governor;
        private readonly StandbyListManager _memoryManager;
        private readonly IpcServer _ipcServer;

        public TuneUpService(
            ProBalanceGovernor governor, 
            StandbyListManager memoryManager, 
            IpcServer ipcServer)
        {
            ServiceName = "TuneUpEngineService";
            
            _governor = governor;
            _memoryManager = memoryManager;
            _ipcServer = ipcServer;

            CanStop = true;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                // Enable modules
                _governor.IsEnabled = true;
                _memoryManager.IsAutoPurging = false; // Keep auto-purge off by default until UI configures it

                // Start IPC Pipe Server
                _ipcServer.Start();
                
                Console.WriteLine("[Service] TuneUp background service successfully started.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Service] Failed to start service: {ex.Message}");
                throw;
            }
        }

        protected override void OnStop()
        {
            try
            {
                // Stop modules
                _governor.IsEnabled = false;
                _memoryManager.IsAutoPurging = false;

                Console.WriteLine("[Service] TuneUp background service successfully stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Service] Error stopping service: {ex.Message}");
            }
        }

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }
    }
}
