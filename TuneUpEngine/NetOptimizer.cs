using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            // Read registry tweaks
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(TcpParamsKey);
                if (key != null)
                {
                    int noDelay = (int)(key.GetValue("TcpNoDelay") ?? 0);
                    int ackFreq = (int)(key.GetValue("TcpAckFrequency") ?? 2);
                    settings.TcpNoDelay = noDelay == 1;
                    settings.TcpAckFrequencyOne = ackFreq == 1;
                }
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

        public static string ApplyOptimizations(Action<string> progress)
        {
            var results = new List<string>();

            progress?.Invoke("Setting TCP auto-tuning level to normal...");
            results.Add(RunNetsh("int tcp set global autotuninglevel=normal"));

            progress?.Invoke("Disabling ECN capability...");
            results.Add(RunNetsh("int tcp set global ecncapability=disabled"));

            progress?.Invoke("Disabling TCP timestamps...");
            results.Add(RunNetsh("int tcp set global timestamps=disabled"));

            progress?.Invoke("Enabling Receive Side Scaling (RSS)...");
            results.Add(RunNetsh("int tcp set global rss=enabled"));

            progress?.Invoke("Setting TcpNoDelay (disable Nagle algorithm)...");
            ApplyRegistryTweaks();

            progress?.Invoke("Applying per-interface TcpNoDelay...");
            ApplyPerInterfaceNoDelay();

            return $"Network optimizations applied. ({results.Count} netsh commands executed)";
        }

        public static string RevertToDefaults(Action<string> progress)
        {
            progress?.Invoke("Restoring TCP auto-tuning to normal...");
            RunNetsh("int tcp set global autotuninglevel=normal");

            progress?.Invoke("Re-enabling ECN capability...");
            RunNetsh("int tcp set global ecncapability=enabled");

            progress?.Invoke("Re-enabling TCP timestamps...");
            RunNetsh("int tcp set global timestamps=enabled");

            progress?.Invoke("Confirming RSS is enabled...");
            RunNetsh("int tcp set global rss=enabled");

            progress?.Invoke("Removing registry overrides...");
            RemoveRegistryTweaks();

            return "Network settings restored to Windows defaults.";
        }

        private static void ApplyRegistryTweaks()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(TcpParamsKey, writable: true);
                if (key != null)
                {
                    key.SetValue("TcpNoDelay", 1, RegistryValueKind.DWord);
                    key.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NetOptimizer] Registry tweak failed: {ex.Message}");
            }
        }

        private static void ApplyPerInterfaceNoDelay()
        {
            // Apply TcpNoDelay and TcpAckFrequency to every network adapter interface
            try
            {
                using var interfacesKey = Registry.LocalMachine.OpenSubKey(TcpInterfacesKey);
                if (interfacesKey == null) return;

                foreach (string subKeyName in interfacesKey.GetSubKeyNames())
                {
                    try
                    {
                        using var ifKey = Registry.LocalMachine.OpenSubKey(
                            $@"{TcpInterfacesKey}\{subKeyName}", writable: true);
                        // Only touch interfaces that have an IP address (real adapters)
                        var dhcp = ifKey?.GetValue("DhcpIPAddress");
                        var ip   = ifKey?.GetValue("IPAddress");
                        if (dhcp != null || ip != null)
                        {
                            ifKey.SetValue("TcpNoDelay", 1, RegistryValueKind.DWord);
                            ifKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NetOptimizer] Per-interface tweak failed: {ex.Message}");
            }
        }

        private static void RemoveRegistryTweaks()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(TcpParamsKey, writable: true);
                key?.DeleteValue("TcpNoDelay", throwOnMissingValue: false);
                key?.DeleteValue("TcpAckFrequency", throwOnMissingValue: false);
            }
            catch { }

            try
            {
                using var interfacesKey = Registry.LocalMachine.OpenSubKey(TcpInterfacesKey);
                if (interfacesKey == null) return;
                foreach (string subKeyName in interfacesKey.GetSubKeyNames())
                {
                    try
                    {
                        using var ifKey = Registry.LocalMachine.OpenSubKey(
                            $@"{TcpInterfacesKey}\{subKeyName}", writable: true);
                        ifKey?.DeleteValue("TcpNoDelay", throwOnMissingValue: false);
                        ifKey?.DeleteValue("TcpAckFrequency", throwOnMissingValue: false);
                    }
                    catch { }
                }
            }
            catch { }
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
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10000);
                return output;
            }
            catch (Exception ex)
            {
                return $"netsh error: {ex.Message}";
            }
        }

        private static string ExtractValue(string output, string key)
        {
            var match = Regex.Match(output, $@"{Regex.Escape(key)}\s*[:\.]?\s*(.+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
    }
}
