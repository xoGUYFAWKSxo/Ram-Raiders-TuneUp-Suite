using System;
using System.Collections.Generic;
using System.Linq;
using TuneUpEngine;

namespace TuneUpUI.Presentation
{
    // Display logic pulled out of the window so it can be unit-tested without a running UI.
    // Everything here is plain data in, plain data out — no WPF types.

    // ── Dashboard ───────────────────────────────────────────────────────────────

    public record DashboardMemoryText(string FreePercent, string AvailableOfTotal, string Standby, string Reclaimed);

    public static class DashboardFormatter
    {
        private const double MB = 1024.0 * 1024.0;
        private const double GB = MB * 1024.0;

        public static DashboardMemoryText Format(EngineStatsPayload stats) => new(
            FreePercent: $"{stats.MemoryFreePercent:F0} %",
            AvailableOfTotal: $"{stats.AvailableMemoryBytes / GB:F1} GB / {stats.TotalMemoryBytes / GB:F1} GB",
            // The engine reports the real standby size, or -1 if it couldn't be read — never a guess.
            Standby: stats.StandbyBytes >= 0 ? $"{stats.StandbyBytes / MB:F0} MB" : "n/a",
            Reclaimed: $"{stats.TotalMemoryFreedBytes / MB:F0} MB");
    }

    // ── Event log lists ─────────────────────────────────────────────────────────

    public static class EventLogView
    {
        // Whether the shown list must be replaced by the engine's latest log. Comparing counts alone
        // stopped working once the engine's log hit its 100-entry cap (the count stays at 100, so
        // the lists froze); every entry is timestamped, so a different newest line means new events.
        public static bool NeedsRefresh(int shownCount, string? shownNewest, IReadOnlyList<string> logs)
        {
            string? newest = logs.Count > 0 ? logs[logs.Count - 1] : null;
            return shownCount != logs.Count || shownNewest != newest;
        }
    }

    // ── Tray notifications ──────────────────────────────────────────────────────

    public static class TrayToastPolicy
    {
        // A new engine log line becomes a tray notification only while the window is hidden, and
        // only for the kinds of event the user asked to be told about.
        public static bool ShouldNotify(string logLine, bool windowVisible, bool notificationsEnabled,
            bool proBalanceToasts, bool memoryToasts)
        {
            if (windowVisible || !notificationsEnabled) return false;

            bool isProBalance = logLine.Contains("Throttled") || logLine.Contains("ProBalance");
            bool isMemory = logLine.Contains("purged") || logLine.Contains("Freed") || logLine.Contains("Standby");
            return (isProBalance && proBalanceToasts) || (isMemory && memoryToasts);
        }
    }

    // ── DebloatGuard services ───────────────────────────────────────────────────

    public static class ServiceStatusText
    {
        // One line under each service: how Windows starts it and whether it is running now.
        public static string Describe(ServiceStatus svc)
        {
            string start = svc.StartType switch
            {
                2 when svc.IsDelayedStart => "Automatic (delayed start)",
                2 => "Automatic",
                3 => "Manual",
                4 => "Disabled",
                0 or 1 => "Boot / System",
                _ => "Unknown",
            };
            return $"Startup: {start}  ·  {(svc.IsRunning ? "Running" : "Stopped")}";
        }
    }

    // ── Cleaner ─────────────────────────────────────────────────────────────────

    public static class CleanConfirmation
    {
        // The question asked before cleaning: what and how much will be deleted, that duplicate
        // sets keep one copy, and any cautions the rule authors attached.
        public static string Build(IReadOnlyList<ScanItem> selected)
        {
            long bytes = selected.Sum(i => i.TotalSizeBytes);
            string text = $"Permanently delete {selected.Count} selected item(s) (about {bytes / (1024.0 * 1024.0):F1} MB)?";

            if (selected.Any(i => i.Category == AdvancedScanner.DuplicateFilesCategory))
                text += "\n\nFor each duplicate set, the first copy listed is kept and only the extra copies are deleted.";

            var warnings = selected
                .Where(i => !string.IsNullOrWhiteSpace(i.Warning))
                .Select(i => $"• {i.RuleName}: {i.Warning}")
                .ToList();
            if (warnings.Count > 0)
                text += "\n\nPlease note:\n" + string.Join("\n", warnings);

            return text;
        }
    }

    // ── Live Monitor: saved DPC traces ──────────────────────────────────────────

    // Writes the DPC monitor's results to a file the user can keep, share, or compare after a
    // driver update. Only measured numbers; no computer or user name, since these get posted to forums.
    public static class LatencyTraceFile
    {
        public static string DefaultFileName(DateTime savedAt) => $"DPC trace {savedAt:yyyy-MM-dd HHmm}";

        public static string ToText(LatencyReport report, DateTime savedAt, string windowsVersion)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Galactic Tune-Up: DPC & interrupt trace");
            sb.AppendLine($"Saved:          {savedAt.ToString("yyyy-MM-dd HH:mm:ss", inv)}");
            sb.AppendLine($"Windows:        {windowsVersion}");
            sb.AppendLine($"Measured for:   {report.SampleCount} seconds{(report.IsRunning ? " (still running when saved)" : "")}");
            sb.AppendLine();
            sb.AppendLine(report.Summary);
            sb.AppendLine();

            sb.AppendLine("DRIVERS, longest single run first");
            if (!report.DriverTraceAvailable)
            {
                sb.AppendLine(string.IsNullOrEmpty(report.DriverTraceMessage) ? "The driver trace wasn't running." : report.DriverTraceMessage);
            }
            else
            {
                sb.AppendLine(string.Format(inv, "{0,-28} {1,-30} {2,12} {3,10} {4,10} {5,10}",
                    "Driver", "What it is", "Longest µs", "Runs", "Total ms", "Status"));
                foreach (var d in report.Drivers)
                    sb.AppendLine(string.Format(inv, "{0,-28} {1,-30} {2,12:N0} {3,10:N0} {4,10:N1} {5,10}",
                        d.Driver, d.Description, d.HighestMicroseconds, d.RunCount, d.TotalMilliseconds, d.Status));
                if (report.DriverEventsLost > 0)
                    sb.AppendLine($"{report.DriverEventsLost.ToString("N0", inv)} runs were missed because the PC was too busy to record them.");
            }
            sb.AppendLine();

            sb.AppendLine("CPU CORES, highest 5-second driver load first");
            sb.AppendLine(string.Format(inv, "{0,-10} {1,12} {2,8} {3,8} {4,10}", "Core", "Peak load %", "DPC %", "ISR %", "Status"));
            foreach (var c in report.Cores)
                sb.AppendLine(string.Format(inv, "{0,-10} {1,12:F1} {2,8:F1} {3,8:F1} {4,10}",
                    c.Name, c.PeakTotalPercent, c.PeakDpcPercent, c.PeakInterruptPercent, c.Status));

            var advice = report.Drivers.Where(d => d.Status != "OK").ToList();
            if (advice.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("WHAT TO DO");
                foreach (var d in advice)
                    sb.AppendLine($"{d.Driver} ({d.Description}): {d.Advice}");
            }
            return sb.ToString();
        }

        // One table for spreadsheets: driver rows, then core rows, told apart by the Type column.
        public static string ToCsv(LatencyReport report)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Type,Name,Description,LongestMicroseconds,Runs,DpcRuns,IsrRuns,TotalMilliseconds,PeakLoadPercent,PeakDpcPercent,PeakIsrPercent,Status");
            foreach (var d in report.Drivers)
                sb.AppendLine(string.Join(",", "Driver", Csv(d.Driver), Csv(d.Description),
                    d.HighestMicroseconds.ToString("F1", inv), d.RunCount.ToString(inv), d.DpcCount.ToString(inv),
                    d.IsrCount.ToString(inv), d.TotalMilliseconds.ToString("F3", inv), "", "", "", d.Status));
            foreach (var c in report.Cores)
                sb.AppendLine(string.Join(",", "Core", Csv(c.Name), "", "", "", "", "", "",
                    c.PeakTotalPercent.ToString("F2", inv), c.PeakDpcPercent.ToString("F2", inv),
                    c.PeakInterruptPercent.ToString("F2", inv), c.Status));
            return sb.ToString();
        }

        private static string Csv(string value)
        {
            // Leading = + - @ would run as a formula in Excel.
            if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value;
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
