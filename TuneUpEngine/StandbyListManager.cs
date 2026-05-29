using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TuneUpEngine
{
    public class StandbyListManager
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        private bool _isAutoPurging = false;
        private Thread _monitorThread;
        private double _autoPurgeThreshold = 10.0; // Purge when free RAM < 10%
        private readonly object _lock = new object();

        public event Action<long> OnAutoPurgeTriggered;

        public bool IsAutoPurging
        {
            get => _isAutoPurging;
            set
            {
                lock (_lock)
                {
                    if (_isAutoPurging == value) return;
                    _isAutoPurging = value;
                    if (_isAutoPurging)
                    {
                        StartMonitoring();
                    }
                }
            }
        }

        public (ulong TotalPhys, ulong AvailPhys, double FreePercent) GetMemoryStatus()
        {
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                double freePercent = (double)memStatus.ullAvailPhys / memStatus.ullTotalPhys * 100.0;
                return (memStatus.ullTotalPhys, memStatus.ullAvailPhys, freePercent);
            }
            return (0, 0, 0);
        }

        public long PurgeStandbyList()
        {
            lock (_lock)
            {
                // Get memory before purge
                var statusBefore = GetMemoryStatus();
                if (statusBefore.TotalPhys == 0) return 0;

                // We need SeProfileSingleProcessPrivilege or SeIncreaseQuotaPrivilege to flush memory
                bool priv1 = NativeMethods.EnablePrivilege(NativeMethods.SE_PROFILE_SINGLE_PROCESS_NAME);
                bool priv2 = NativeMethods.EnablePrivilege(NativeMethods.SE_INCREASE_QUOTA_NAME);

                int command = (int)NativeMethods.SYSTEM_MEMORY_LIST_COMMAND.MemoryPurgeStandbyList;
                IntPtr pCommand = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(pCommand, command);

                int ntStatus = NativeMethods.NtSetSystemInformation(
                    NativeMethods.SystemMemoryListInformation,
                    pCommand,
                    sizeof(int)
                );

                Marshal.FreeHGlobal(pCommand);

                // Wait a brief moment for Windows to process page transitions
                Thread.Sleep(100);

                // Get memory after purge
                var statusAfter = GetMemoryStatus();
                long freedBytes = (long)statusAfter.AvailPhys - (long)statusBefore.AvailPhys;

                return freedBytes > 0 ? freedBytes : 0;
            }
        }

        private void StartMonitoring()
        {
            if (_monitorThread != null && _monitorThread.IsAlive) return;

            _monitorThread = new Thread(() =>
            {
                while (true)
                {
                    lock (_lock)
                    {
                        if (!_isAutoPurging) break;
                    }

                    var status = GetMemoryStatus();
                    if (status.TotalPhys > 0 && status.FreePercent < _autoPurgeThreshold)
                    {
                        long freed = PurgeStandbyList();
                        if (freed > 0)
                        {
                            OnAutoPurgeTriggered?.Invoke(freed);
                        }
                    }

                    Thread.Sleep(5000); // Check every 5 seconds
                }
            })
            {
                IsBackground = true,
                Name = "StandbyListMonitor"
            };
            _monitorThread.Start();
        }
    }
}
