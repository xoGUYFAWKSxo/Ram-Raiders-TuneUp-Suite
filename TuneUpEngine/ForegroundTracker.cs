using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TuneUpEngine
{
    // Which process owns the window the user is looking at. Game Mode boosts it and ProBalance
    // never throttles it.
    //
    // A Windows service runs in session 0, which has no access to the user's desktop, so
    // GetForegroundWindow there returns nothing. The UI (running on the user's desktop) therefore
    // reports the foreground process every second; a report older than a few seconds is ignored
    // (UI closed), and when the engine itself runs on the desktop (console mode) it simply asks
    // Windows directly.
    public static class ForegroundTracker
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static readonly TimeSpan ReportLifetime = TimeSpan.FromSeconds(3);
        private static int _reportedPid;
        private static long _reportedAtTicks;

        public static void Report(int pid)
        {
            Interlocked.Exchange(ref _reportedPid, pid);
            Interlocked.Exchange(ref _reportedAtTicks, DateTime.UtcNow.Ticks);
        }

        public static int GetForegroundProcessId()
        {
            long reportedAt = Interlocked.Read(ref _reportedAtTicks);
            if (reportedAt != 0 && DateTime.UtcNow - new DateTime(reportedAt, DateTimeKind.Utc) < ReportLifetime)
                return Volatile.Read(ref _reportedPid);

            try
            {
                IntPtr hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return 0;
                GetWindowThreadProcessId(hWnd, out uint pid);
                return (int)pid;
            }
            catch
            {
                return 0;
            }
        }

        // For tests.
        internal static void Reset()
        {
            Interlocked.Exchange(ref _reportedPid, 0);
            Interlocked.Exchange(ref _reportedAtTicks, 0);
        }
    }
}
