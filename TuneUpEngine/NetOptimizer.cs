using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TuneUpEngine
{
    public class NetSettings
    {
        public string AutoTuningLevel { get; set; } = "Unknown";
        public string EcnCapability { get; set; } = "Unknown";
        public string Timestamps { get; set; } = "Unknown";
        public string Rss { get; set; } = "Unknown";
        public bool TcpNoDelay { get; set; }
        public bool TcpAckFrequencyOne { get; set; }
        public bool IsOptimal { get; set; }
    }

    public static class NetOptimizer
    {
        private const string TcpParamsKey =
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
        private const string TcpInterfacesKey =
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

        public static NetSettings GetCurrentSettings()
        {
            var settings = new NetSettings();

            // Read netsh tcp global settings
            string netshOutput = RunNetsh("int tcp show global");
            if (!string.IsNullOrEmpty(netshOutput))
            {
                settings.AutoTuningLevel = ExtractValue(netshOutput, "Receive Window Auto-Tuning Level") ?? "Unknown";
                settings.EcnCapability   = ExtractValue(netshOutput, "ECN Capability") ?? "Unknown";
                settings.Timestamps      = ExtractValue(netshOutput, "RFC 1323 Timestamps") ?? "Unknown";
                settings.Rss            = ExtractValue(netshOutput, "Receive-Side Scaling State") ?? "Unknown";
            }

            // TcpNoDelay / TcpAckFrequency are per-interface settings: report them as on only when
            // every connected interface has them.
            try
            {
                // Each key is opened per read: reading through one shared list of keys disposed them
                // on the first pass, so the second check always failed and showed "not optimized".
                var interfaces = ConnectedInterfacePaths();
                settings.TcpNoDelay = interfaces.Count > 0 && interfaces.All(p => ReadDword(p, "TcpNoDelay") == 1);
                settings.TcpAckFrequencyOne = interfaces.Count > 0 && interfaces.All(p => ReadDword(p, "TcpAckFrequency") == 1);
            }
            catch { }

            settings.IsOptimal =
                settings.AutoTuningLevel.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                settings.EcnCapability.Equals("disabled", StringComparison.OrdinalIgnoreCase) &&
                settings.Timestamps.Equals("disabled", StringComparison.OrdinalIgnoreCase) &&
                settings.Rss.Equals("enabled", StringComparison.OrdinalIgnoreCase) &&
                settings.TcpNoDelay && settings.TcpAckFrequencyOne;

            return settings;
        }

        // All changes are recorded in the change journal first, so "Revert" restores the values the
        // user actually had — previously it forced ECN and timestamps ON, which isn't necessarily
        // how the PC was before.
        private const string JournalOwner = "net";

        private static readonly (string Setting, string Value, string Progress)[] TcpGlobals =
        {
            ("autotuninglevel", "normal", "Setting TCP auto-tuning level to normal..."),
            ("ecncapability", "disabled", "Disabling ECN capability..."),
            ("timestamps", "disabled", "Disabling TCP timestamps..."),
            ("rss", "enabled", "Enabling Receive Side Scaling (RSS)..."),
        };

        public static string ApplyOptimizations(Action<string> progress, ChangeJournal journal)
        {
            int changed = 0, skipped = 0;

            foreach (var (setting, value, message) in TcpGlobals)
            {
                progress?.Invoke(message);
                // If the current value can't be read (e.g. non-English Windows, where netsh's
                // labels are translated) it can't be restored later, so it is left alone.
                if (journal.CaptureNetshTcpGlobal(JournalOwner, setting) &&
                    journal.Commands.SetNetshTcpGlobal(setting, value))
                    changed++;
                else
                    skipped++;
            }

            // Windows only honours TcpNoDelay / TcpAckFrequency per interface; the copies earlier
            // versions wrote under Tcpip\Parameters had no effect and are no longer written.
            progress?.Invoke("Applying TcpNoDelay / TcpAckFrequency to connected network interfaces...");
            int interfaces = 0;
            foreach (string ifPath in ConnectedInterfacePaths())
            {
                bool captured =
                    journal.CaptureRegistryValue(JournalOwner, RegistryHive.LocalMachine, ifPath, "TcpNoDelay") &&
                    journal.CaptureRegistryValue(JournalOwner, RegistryHive.LocalMachine, ifPath, "TcpAckFrequency");
                if (!captured) continue;

                try
                {
                    using var ifKey = Registry.LocalMachine.OpenSubKey(ifPath, writable: true);
                    if (ifKey == null) continue;
                    ifKey.SetValue("TcpNoDelay", 1, RegistryValueKind.DWord);
                    ifKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                    interfaces++;
                }
                catch (Exception ex)
                {
                    EngineLog.Warn($"[NetOptimizer] Per-interface tweak failed for {ifPath}: {ex.Message}");
                }
            }

            string result = $"Network optimizations applied: {changed} TCP setting(s) and {interfaces} interface(s) changed.";
            if (skipped > 0)
                result += $" {skipped} TCP setting(s) were left unchanged because their current value couldn't be read.";
            return result;
        }

        public static string RevertToDefaults(Action<string> progress, ChangeJournal journal)
        {
            if (journal.IsOwned(JournalOwner))
            {
                progress?.Invoke("Restoring your previous network settings...");
                int failed = journal.Release(JournalOwner);
                return failed == 0
                    ? "Network settings restored to exactly how they were before optimization."
                    : $"Network settings restored, except {failed} that will be retried on the next engine start.";
            }

            // Optimized by a version without the journal: the previous values are unknown, so fall
            // back to the old behaviour of resetting to Windows defaults.
            progress?.Invoke("No record of your earlier values — restoring Windows defaults...");
            RunNetsh("int tcp set global autotuninglevel=normal");
            RunNetsh("int tcp set global ecncapability=enabled");
            RunNetsh("int tcp set global timestamps=enabled");
            RunNetsh("int tcp set global rss=enabled");
            RemoveLegacyRegistryTweaks();
            return "Network settings reset to Windows defaults (no record of earlier values was available).";
        }

        // Interfaces that have an IP address (real, configured adapters).
        private static List<string> ConnectedInterfacePaths()
        {
            var paths = new List<string>();
            try
            {
                using var interfacesKey = Registry.LocalMachine.OpenSubKey(TcpInterfacesKey);
                if (interfacesKey == null) return paths;

                foreach (string subKeyName in interfacesKey.GetSubKeyNames())
                {
                    using var ifKey = interfacesKey.OpenSubKey(subKeyName);
                    if (ifKey?.GetValue("DhcpIPAddress") != null || ifKey?.GetValue("IPAddress") != null)
                        paths.Add($@"{TcpInterfacesKey}\{subKeyName}");
                }
            }
            catch (Exception ex)
            {
                EngineLog.Warn($"[NetOptimizer] Could not enumerate interfaces: {ex.Message}");
            }
            return paths;
        }

        private static int? ReadDword(string keyPath, string name)
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(name) as int?;
        }

        // Only for state left by versions without the change journal.
        private static void RemoveLegacyRegistryTweaks()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(TcpParamsKey, writable: true);
                key?.DeleteValue("TcpNoDelay", throwOnMissingValue: false);
                key?.DeleteValue("TcpAckFrequency", throwOnMissingValue: false);
            }
            catch { }

            foreach (string ifPath in ConnectedInterfacePaths())
            {
                try
                {
                    using var ifKey = Registry.LocalMachine.OpenSubKey(ifPath, writable: true);
                    ifKey?.DeleteValue("TcpNoDelay", throwOnMissingValue: false);
                    ifKey?.DeleteValue("TcpAckFrequency", throwOnMissingValue: false);
                }
                catch { }
            }
        }

        private static string RunNetsh(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return "";
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10000);
                return output;
            }
            catch (Exception ex)
            {
                return $"netsh error: {ex.Message}";
            }
        }

        private static string? ExtractValue(string output, string key)
        {
            var match = Regex.Match(output, $@"{Regex.Escape(key)}\s*[:\.]?\s*(.+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
    }
}
