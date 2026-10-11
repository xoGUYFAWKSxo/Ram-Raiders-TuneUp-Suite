using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;

namespace TuneUpEngine
{
    public class ServiceStatus
    {
        public string ServiceName { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public bool IsRunning { get; set; }
        public bool IsDisabled { get; set; }
        public bool IsAvailable { get; set; } // Service exists on this machine
    }

    public class AppxPackageInfo
    {
        public string Name { get; set; }
        public string FriendlyName { get; set; }
        public string Publisher { get; set; }
        public string PackageFullName { get; set; }
        public string Category { get; set; } // "Telemetry", "OEM", "Microsoft Bloat", etc.
        public bool IsOem { get; set; }
    }

    public class DebloatStatus
    {
        public List<ServiceStatus> Services { get; set; } = new();
        public List<AppxPackageInfo> DetectedPackages { get; set; } = new();
    }

    public static class DebloatGuard
    {
        // ─── Curated services ──────────────────────────────────────────────────────
        private static readonly (string Name, string Display, string Desc)[] _targetServices =
        {
            ("DiagTrack",        "Connected User Experiences and Telemetry",
             "Sends usage and diagnostic data to Microsoft. Causes periodic disk & CPU spikes."),
            ("dmwappushservice", "WAP Push Message Routing Service",
             "Routes WAP push messages. Used by telemetry pipeline. Safe to disable on non-MDM systems."),
            ("SysMain",          "SysMain (Superfetch)",
             "Pre-loads frequently used apps into RAM. Beneficial on HDDs, counterproductive on NVMe SSDs.")
        };

        // ─── Curated Microsoft bloatware package family prefixes ──────────────────
        private static readonly (string FamilyPrefix, string FriendlyName, string Category)[] _curatedPackages =
        {
            ("Microsoft.XboxApp",                   "Xbox App",                   "Microsoft Bloat"),
            ("Microsoft.GamingApp",                 "Xbox Gaming Overlay",        "Microsoft Bloat"),
            ("Microsoft.XboxGameOverlay",            "Xbox Game Overlay",          "Microsoft Bloat"),
            ("Microsoft.XboxGamingOverlay",          "Xbox Gaming Services",       "Microsoft Bloat"),
            ("Microsoft.XboxIdentityProvider",       "Xbox Identity Provider",     "Microsoft Bloat"),
            ("Microsoft.XboxSpeechToTextOverlay",    "Xbox Speech Overlay",        "Microsoft Bloat"),
            ("Microsoft.BingWeather",                "MSN Weather",                "Microsoft Bloat"),
            ("Microsoft.BingNews",                   "Microsoft Start (Bing News)", "Microsoft Bloat"),
            ("Microsoft.BingFinance",                "MSN Finance",                "Microsoft Bloat"),
            ("Microsoft.BingSports",                 "MSN Sports",                 "Microsoft Bloat"),
            ("Microsoft.MicrosoftSolitaireCollection","Microsoft Solitaire",       "Microsoft Bloat"),
            ("Microsoft.WindowsFeedbackHub",         "Feedback Hub",               "Microsoft Bloat"),
            ("Microsoft.GetHelp",                    "Get Help",                   "Microsoft Bloat"),
            ("Microsoft.Todos",                      "Microsoft To Do",            "Microsoft Bloat"),
            ("Microsoft.People",                     "People App",                 "Microsoft Bloat"),
            ("Microsoft.Cortana",                    "Cortana / Copilot Hooks",    "Telemetry"),
            ("Microsoft.549981C3F5F10",              "Cortana (Store ID)",         "Telemetry"),
            ("Microsoft.WindowsCommunicationsApps",  "Mail and Calendar",          "Microsoft Bloat"),
            ("Microsoft.YourPhone",                  "Phone Link / Your Phone",    "Microsoft Bloat"),
            ("Microsoft.ZuneMusic",                  "Groove Music",               "Microsoft Bloat"),
            ("Microsoft.ZuneVideo",                  "Movies & TV",                "Microsoft Bloat"),
            ("Microsoft.MixedReality.Portal",        "Mixed Reality Portal",       "Microsoft Bloat"),
            ("Microsoft.MicrosoftOfficeHub",         "Office Hub",                 "Microsoft Bloat"),
            ("Microsoft.OneConnect",                 "Mobile Plans",               "Microsoft Bloat"),
            ("Microsoft.SkypeApp",                   "Skype",                      "Microsoft Bloat"),
        };

        // ─── Known OEM package publisher substrings → OEM label ──────────────────
        private static readonly (string PublisherSubstring, string OemLabel)[] _oemPublishers =
        {
            ("DELL INC",       "Dell"),
            ("HEWLETT-PACKARD", "HP"),
            ("HP INC",         "HP"),
            ("LENOVO",         "Lenovo"),
            ("ASUSTeK",        "ASUS"),
            ("ACER INCORPORATED", "Acer"),
            ("MICRO-STAR",     "MSI"),
            ("SAMSUNG ELECTRONICS", "Samsung"),
            ("TOSHIBA",        "Toshiba"),
            ("FUJITSU",        "Fujitsu"),
            ("HUAWEI",         "Huawei"),
            ("LG ELECTRONICS", "LG"),
            ("PANASONIC",      "Panasonic"),
        };

        // Known OEM package name prefixes
        private static readonly string[] _oemPackagePrefixes =
        {
            "DellInc.", "HPInc.", "HewlettPackard", "LenovoGroup", "ASUSTeK",
            "AcerIncorporated", "MSI.", "SamsungElectronics", "ToshibaCorporation",
            "FujitsuLimited", "HuaweiDevice", "LGElectronics"
        };

        // ─────────────────────────────────────────────────────────────────────────

        public static DebloatStatus GetStatus()
        {
            var status = new DebloatStatus();
            status.Services = GetServiceStatuses();
            status.DetectedPackages = DetectInstalledPackages();
            return status;
        }

        public static List<ServiceStatus> GetServiceStatuses()
        {
            var results = new List<ServiceStatus>();
            foreach (var (name, display, desc) in _targetServices)
            {
                var svc = new ServiceStatus
                {
                    ServiceName = name,
                    DisplayName = display,
                    Description = desc,
                    IsAvailable = false,
                    IsRunning = false,
                    IsDisabled = false
                };

                try
                {
                    using var sc = new ServiceController(name);
                    var status = sc.Status;
                    svc.IsAvailable = true;
                    svc.IsRunning = status == ServiceControllerStatus.Running ||
                                   status == ServiceControllerStatus.StartPending;

                    // Check startup type from registry
                    using var regKey = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{name}");
                    if (regKey != null)
                    {
                        int startType = (int)(regKey.GetValue("Start") ?? 2);
                        svc.IsDisabled = startType == 4; // 4 = Disabled
                    }
                }
                catch
                {
                    // Service not installed on this Windows edition
                    svc.IsAvailable = false;
                }

                results.Add(svc);
            }
            return results;
        }

        // Only the services this tool lists may be changed. The name arrives over IPC; without this
        // check any client could have the elevated engine stop and disable an arbitrary service
        // (Windows Defender, the firewall, event logging…), and a name containing '\' would even
        // address a different registry key under Services.
        internal static bool IsManagedService(string? serviceName) =>
            !string.IsNullOrEmpty(serviceName) &&
            _targetServices.Any(s => s.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase));

        public static string SetServiceState(string serviceName, bool disable)
        {
            if (!IsManagedService(serviceName))
                return $"Refused: '{serviceName}' is not a service this tool manages.";

            // Use the canonical spelling from our own list from here on.
            serviceName = _targetServices.First(s => s.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase)).Name;

            try
            {
                using var sc = new ServiceController(serviceName);

                if (disable)
                {
                    if (sc.Status == ServiceControllerStatus.Running)
                        sc.Stop();

                    // Change startup type to Disabled via registry
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
                    key?.SetValue("Start", 4, RegistryValueKind.DWord);
                    return $"Service '{serviceName}' stopped and disabled.";
                }
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
                    key?.SetValue("Start", 3, RegistryValueKind.DWord); // Manual
                    sc.Start();
                    return $"Service '{serviceName}' re-enabled and started.";
                }
            }
            catch (Exception ex)
            {
                return $"Failed to change '{serviceName}': {ex.Message}";
            }
        }

        public static List<AppxPackageInfo> DetectInstalledPackages()
        {
            var found = new List<AppxPackageInfo>();

            // Read installed packages from registry (works without WinRT reference)
            // HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore
            string[] registryHives =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications",
                @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"
            };

            var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var hive in registryHives)
            {
                try
                {
                    // Open from HKCU if it's the Local Settings hive, otherwise HKLM
                    RegistryKey root = hive.StartsWith("SOFTWARE\\Classes", StringComparison.OrdinalIgnoreCase)
                        ? Registry.CurrentUser.OpenSubKey(hive)
                        : Registry.LocalMachine.OpenSubKey(hive);

                    if (root == null) continue;


                    foreach (var subKeyName in root.GetSubKeyNames())
                    {
                        // Match against curated list
                        foreach (var (prefix, friendly, category) in _curatedPackages)
                        {
                            if (subKeyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                addedNames.Add(prefix))
                            {
                                found.Add(new AppxPackageInfo
                                {
                                    Name = prefix,
                                    FriendlyName = friendly,
                                    PackageFullName = subKeyName,
                                    Category = category,
                                    IsOem = false,
                                    Publisher = "Microsoft Corporation"
                                });
                                break;
                            }
                        }

                        // OEM detection: check package name prefixes
                        foreach (var oemPrefix in _oemPackagePrefixes)
                        {
                            if (subKeyName.StartsWith(oemPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                string oemName = subKeyName.Split('_')[0]; // Strip version
                                if (addedNames.Add(oemName))
                                {
                                    string oemLabel = GetOemLabel(subKeyName);
                                    found.Add(new AppxPackageInfo
                                    {
                                        Name = oemName,
                                        FriendlyName = $"{oemLabel} Companion App",
                                        PackageFullName = subKeyName,
                                        Category = $"OEM ({oemLabel})",
                                        IsOem = true,
                                        Publisher = oemLabel
                                    });
                                }
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            return found.OrderBy(p => p.Category).ThenBy(p => p.FriendlyName).ToList();
        }

        private static string GetOemLabel(string packageName)
        {
            string upper = packageName.ToUpperInvariant();
            foreach (var (sub, label) in _oemPublishers)
            {
                if (upper.Contains(sub)) return label;
            }
            // Extract OEM name from prefix
            foreach (var prefix in _oemPackagePrefixes)
            {
                if (packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // e.g. "DellInc." -> "Dell"
                    return prefix.TrimEnd('.').Replace("Inc", "").Replace("Group", "")
                                 .Replace("Incorporated", "").Replace("Electronics", "")
                                 .Replace("Corporation", "").Replace("Limited", "").Trim();
                }
            }
            return "OEM";
        }

        public static string RemoveAppxPackage(string packageFullName, Action<string> progressCallback)
        {
            // The package identity arrives over IPC and is interpolated into a PowerShell
            // command below, so it MUST be validated first. Without validation, a crafted
            // name (e.g. one containing a quote) could break out of the string literal and
            // execute arbitrary code in this ELEVATED process. Two independent gates:
            //   1. Allowlist — the identity must be a package this tool actually offers to
            //      remove (a curated Microsoft prefix or a known OEM prefix). Anything else
            //      is refused outright.
            //   2. Character whitelist — even an allowlisted value may only contain the
            //      characters legal in an AppX identity name, so it cannot carry shell or
            //      PowerShell metacharacters.
            if (string.IsNullOrWhiteSpace(packageFullName))
                return "Error: No package specified.";

            // The UI sends the full package name (e.g. "Microsoft.BingWeather_4.x_x64__8wek...").
            // The identity is everything before the first underscore.
            string identity = packageFullName.Split('_')[0];

            if (!IsSafePackageIdentity(identity) || !IsKnownRemovablePackage(identity))
                return $"Error: '{packageFullName}' is not a recognized removable package. Removal refused.";

            // Use PowerShell to remove the package (avoids WinRT/COM complexity while keeping elevation)
            try
            {
                progressCallback?.Invoke($"Removing {identity}...");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                // Build the argument vector explicitly (ArgumentList handles quoting), and embed
                // the identity only after the validation above. Because 'identity' is restricted
                // to [A-Za-z0-9.-], placing it inside a single-quoted PowerShell literal cannot
                // inject code.
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(
                    $"Get-AppxPackage '{identity}*' | Remove-AppxPackage -ErrorAction SilentlyContinue");

                using var proc = System.Diagnostics.Process.Start(psi);
                proc.WaitForExit(30000);
                return proc.ExitCode == 0
                    ? $"Successfully removed: {identity}"
                    : $"Removal returned exit code {proc.ExitCode} for: {identity}";
            }
            catch (Exception ex)
            {
                return $"Error removing {identity}: {ex.Message}";
            }
        }

        // Only the characters that are legal in an AppX package identity name. This deliberately
        // excludes quotes, spaces, semicolons, pipes, ampersands and every other shell/PowerShell
        // metacharacter, so a validated identity is safe to embed in a command string.
        internal static bool IsSafePackageIdentity(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return false;
            foreach (char c in identity)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                          (c >= '0' && c <= '9') || c == '.' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        // The identity must correspond to something this tool actually surfaces for removal:
        // a curated Microsoft package prefix (exact match) or a known OEM prefix. This prevents
        // the elevated engine from being asked to uninstall arbitrary packages.
        internal static bool IsKnownRemovablePackage(string identity)
        {
            foreach (var (prefix, _, _) in _curatedPackages)
            {
                if (identity.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            foreach (var oemPrefix in _oemPackagePrefixes)
            {
                if (identity.StartsWith(oemPrefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
