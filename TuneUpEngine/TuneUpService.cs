using System;
using System.ServiceProcess;

namespace TuneUpEngine
{
    public class TuneUpService : ServiceBase
    {
        private readonly ProBalanceGovernor _governor;
        private readonly StandbyListManager _memoryManager;
        private readonly IpcServer _ipcServer;
        private readonly Action? _releaseTemporaryChanges;
        private readonly Action? _applySavedModuleStates;

        public TuneUpService(
            ProBalanceGovernor governor, 
            StandbyListManager memoryManager, 
            IpcServer ipcServer,
            Action? releaseTemporaryChanges = null,
            Action? applySavedModuleStates = null)
        {
            ServiceName = "TuneUpEngineService";
            
            _governor = governor;
            _memoryManager = memoryManager;
            _ipcServer = ipcServer;
            _releaseTemporaryChanges = releaseTemporaryChanges;
            _applySavedModuleStates = applySavedModuleStates;

            CanStop = true;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                // Claim the pipe first (fails if another engine is already running), then restore
                // the saved module states — never the other way round.
                _ipcServer.Start();
                _applySavedModuleStates?.Invoke();

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
                // Hand back temporary changes (boosted game, power plan, throttled processes)
                _releaseTemporaryChanges?.Invoke();

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
