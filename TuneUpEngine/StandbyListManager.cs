using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TuneUpEngine
{
    // Windows' physical-memory page lists, in bytes (what RAMMap shows).
    public readonly record struct MemoryLists(long ZeroBytes, long FreeBytes, long ModifiedBytes, long StandbyBytes)
    {
        // Memory that is truly unused right now (free + zeroed pages).
        public long UnusedBytes => ZeroBytes + FreeBytes;
    }

    public readonly record struct PurgeResult(bool Success, long FreedBytes, string? Error);

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

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

        // SYSTEM_MEMORY_LIST_INFORMATION is 22 pointer-sized counters:
        //   ZeroPageCount, FreePageCount, ModifiedPageCount, ModifiedNoWritePageCount, BadPageCount,
        //   PageCountByPriority[8] (the standby list, by priority), RepurposedPagesByPriority[8],
        //   ModifiedPageCountPageFile.
        private const int MemoryListFieldCount = 22;
        private const int StandbyFirstField = 5;
        private const int StandbyFieldCount = 8;

        // Auto-purge policy. Purging the standby list only helps when memory that is truly FREE is
        // short while the standby cache holds plenty that could be given back. The old trigger was
        // "available < 10%" — but "available" already includes the standby cache, so by the time it
        // fired there was almost nothing left to purge, and it could fire every 5 seconds.
        internal const double AutoPurgeUnusedBelowFraction = 0.10;   // free+zero pages < 10% of RAM
        internal const double AutoPurgeStandbyAtLeastFraction = 0.10; // and standby >= 10% of RAM
        internal static readonly TimeSpan AutoPurgeCooldown = TimeSpan.FromSeconds(60);
        private DateTime _lastAutoPurge = DateTime.MinValue;

        private bool _isAutoPurging = false;
        private Thread _monitorThread;
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

        // Reads the real page lists. Needs administrator rights (the engine has them); returns null
        // when they can't be read — callers then show "unknown" rather than a guess.
        public MemoryLists? GetMemoryLists()
        {
            NativeMethods.EnablePrivilege(NativeMethods.SE_PROFILE_SINGLE_PROCESS_NAME);

            int size = MemoryListFieldCount * IntPtr.Size;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(NativeMethods.SystemMemoryListInformation, buffer, size, out _);
                if (status != 0) return null;

                long[] fields = new long[MemoryListFieldCount];
                for (int i = 0; i < MemoryListFieldCount; i++)
                    fields[i] = Marshal.ReadIntPtr(buffer, i * IntPtr.Size).ToInt64();

                return FromPageCounts(fields, Environment.SystemPageSize);
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static MemoryLists FromPageCounts(long[] fields, long pageSize)
        {
            long standbyPages = 0;
            for (int i = StandbyFirstField; i < StandbyFirstField + StandbyFieldCount; i++)
                standbyPages += fields[i];

            return new MemoryLists(
                ZeroBytes: fields[0] * pageSize,
                FreeBytes: fields[1] * pageSize,
                ModifiedBytes: fields[2] * pageSize,
                StandbyBytes: standbyPages * pageSize);
        }

        internal static bool ShouldAutoPurge(MemoryLists lists, ulong totalPhys, DateTime now, DateTime lastPurge)
        {
            if (totalPhys == 0) return false;
            if (now - lastPurge < AutoPurgeCooldown) return false;
            return lists.UnusedBytes < totalPhys * AutoPurgeUnusedBelowFraction &&
                   lists.StandbyBytes >= totalPhys * AutoPurgeStandbyAtLeastFraction;
        }

        // Empties the standby list. "Freed" is how much the standby list actually shrank, measured
        // from Windows' page lists — the old figure compared "available" memory before and after,
        // and since available memory already counts the standby list it reported roughly zero.
        public PurgeResult PurgeStandbyList()
        {
            lock (_lock)
            {
                var before = GetMemoryLists();

                NativeMethods.EnablePrivilege(NativeMethods.SE_PROFILE_SINGLE_PROCESS_NAME);
                NativeMethods.EnablePrivilege(NativeMethods.SE_INCREASE_QUOTA_NAME);

                int command = (int)NativeMethods.SYSTEM_MEMORY_LIST_COMMAND.MemoryPurgeStandbyList;
                IntPtr pCommand = Marshal.AllocHGlobal(sizeof(int));
                int ntStatus;
                try
                {
                    Marshal.WriteInt32(pCommand, command);
                    ntStatus = NativeMethods.NtSetSystemInformation(
                        NativeMethods.SystemMemoryListInformation, pCommand, sizeof(int));
                }
                finally
                {
                    Marshal.FreeHGlobal(pCommand);
                }

                if (ntStatus != 0)
                {
                    // 0xC0000061 = STATUS_PRIVILEGE_NOT_HELD: the engine isn't running elevated.
                    string reason = unchecked((uint)ntStatus) == 0xC0000061
                        ? "the engine is not running with administrator rights"
                        : $"Windows returned 0x{ntStatus:X8}";
                    return new PurgeResult(false, 0, $"Standby purge failed: {reason}.");
                }

                // Give Windows a moment to move the pages to the free list.
                Thread.Sleep(100);
                var after = GetMemoryLists();

                long freed = before.HasValue && after.HasValue
                    ? Math.Max(0, before.Value.StandbyBytes - after.Value.StandbyBytes)
                    : 0;
                return new PurgeResult(true, freed, null);
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
                    var lists = GetMemoryLists();
                    var now = DateTime.UtcNow;
                    if (lists.HasValue && ShouldAutoPurge(lists.Value, status.TotalPhys, now, _lastAutoPurge))
                    {
                        _lastAutoPurge = now;
                        var result = PurgeStandbyList();
                        if (result.Success && result.FreedBytes > 0)
                        {
                            OnAutoPurgeTriggered?.Invoke(result.FreedBytes);
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
