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
}
