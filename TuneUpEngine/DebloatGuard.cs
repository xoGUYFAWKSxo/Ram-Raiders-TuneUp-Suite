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
        public string ServiceName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsRunning { get; set; }
        public bool IsDisabled { get; set; }
        public bool IsAvailable { get; set; } // Service exists on this machine
        // Registry Start value: 2 Automatic, 3 Manual, 4 Disabled (0/1 are drivers); -1 if unreadable.
        public int StartType { get; set; } = -1;
        public bool IsDelayedStart { get; set; }
    }

    public class AppxPackageInfo
    {
        public string Name { get; set; } = "";
        public string FriendlyName { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string PackageFullName { get; set; } = "";
        public string Category { get; set; } = ""; // "Telemetry", "OEM", "Microsoft Bloat", etc.
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
            // Labels corrected (GamingApp is the Xbox app, XboxGamingOverlay is Game Bar).
            // Microsoft.XboxIdentityProvider is deliberately NOT listed: it handles Xbox / Game Pass
            // sign-in inside PC games, and removing it breaks those games.
            ("Microsoft.XboxApp",                   "Xbox Console Companion (legacy)", "Microsoft Bloat"),
            ("Microsoft.GamingApp",                 "Xbox app",                   "Microsoft Bloat"),
            ("Microsoft.XboxGameOverlay",            "Xbox Game Overlay (legacy)", "Microsoft Bloat"),
            ("Microsoft.XboxGamingOverlay",          "Xbox Game Bar",              "Microsoft Bloat"),
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

        public static DebloatStatus GetStatus() => GetStatus(ClientContext.ForCurrentProcess());

        public static DebloatStatus GetStatus(ClientContext ctx)
        {
            var status = new DebloatStatus();
            status.Services = GetServiceStatuses();
            status.DetectedPackages = DetectInstalledPackages(ctx);
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
                        svc.StartType = startType;
                        svc.IsDisabled = startType == 4; // 4 = Disabled
                        svc.IsDelayedStart = startType == 2 && (int)(regKey.GetValue("DelayedAutostart") ?? 0) == 1;
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

        // Windows' own start type for each managed service (2 = Automatic, 3 = Manual). Only used to
        // re-enable a service that was disabled before the change journal existed.
        private static readonly Dictionary<string, int> _windowsDefaultStart = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DiagTrack"] = 2,
            ["dmwappushservice"] = 3,
            ["SysMain"] = 2,
        };

        internal static string JournalOwnerFor(string serviceName) => $"service:{serviceName}";

        // Start types 0-2 (boot, system, automatic) run without being asked; 3 (manual) doesn't.
        internal static bool ShouldStartAfterReEnable(int startType) => startType <= 2;

        public static string SetServiceState(string serviceName, bool disable, ChangeJournal journal)
        {
            if (!IsManagedService(serviceName))
                return $"Refused: '{serviceName}' is not a service this tool manages.";

            // Use the canonical spelling from our own list from here on.
            serviceName = _targetServices.First(s => s.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase)).Name;
            string serviceKey = $@"SYSTEM\CurrentControlSet\Services\{serviceName}";
            string owner = JournalOwnerFor(serviceName);

            try
            {
                using var sc = new ServiceController(serviceName);

                if (disable)
                {
                    // Record the user's own start type (and delayed-start flag) before changing it.
                    if (!journal.CaptureRegistryValue(owner, RegistryHive.LocalMachine, serviceKey, "Start") ||
                        !journal.CaptureRegistryValue(owner, RegistryHive.LocalMachine, serviceKey, "DelayedAutostart"))
                    {
                        journal.Release(owner);
                        return $"Failed to change '{serviceName}': its current start type couldn't be recorded, so nothing was changed.";
                    }

                    if (sc.Status == ServiceControllerStatus.Running)
                        sc.Stop();

                    using var key = Registry.LocalMachine.OpenSubKey(serviceKey, writable: true);
                    key?.SetValue("Start", 4, RegistryValueKind.DWord);
                    return $"Service '{serviceName}' stopped and disabled.";
                }

                // Re-enable: put back exactly the start type the user had. Previously this always
                // set Manual, so an Automatic service (e.g. SysMain) never came back on its own.
                if (journal.IsOwned(owner))
                {
                    if (journal.Release(owner) > 0)
                        return $"Failed to change '{serviceName}': its previous start type couldn't be restored yet.";
                }
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey(serviceKey, writable: true);
                    key?.SetValue("Start", _windowsDefaultStart.GetValueOrDefault(serviceName, 3), RegistryValueKind.DWord);
                }

                int startType;
                using (var key = Registry.LocalMachine.OpenSubKey(serviceKey))
                    startType = key?.GetValue("Start") as int? ?? 3;

                if (startType == 4)
                    return $"Service '{serviceName}' restored to its previous start type (Disabled).";

                // Only an Automatic service is started now. A Manual one is started by Windows when
                // something needs it; starting it here left e.g. WAP Push running after a
                // disable/re-enable when it had been stopped before (found in end-to-end testing).
                if (!ShouldStartAfterReEnable(startType))
                    return $"Service '{serviceName}' re-enabled with its previous start type (Manual); Windows starts it when needed.";

                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Stopped)
                    sc.Start();
                return $"Service '{serviceName}' re-enabled with its previous start type and started.";
            }
            catch (Exception ex)
            {
                return $"Failed to change '{serviceName}': {ex.Message}";
            }
        }

        // Installed packages are partly per-user, so the user's own hive is read (not the engine's).
        public static List<AppxPackageInfo> DetectInstalledPackages(ClientContext ctx)
        {
            using var userHive = ctx.OpenUserHive();
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
                    RegistryKey? root = hive.StartsWith("SOFTWARE\\Classes", StringComparison.OrdinalIgnoreCase)
                        ? userHive.Key?.OpenSubKey(hive)
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

        // Removes the package for the requesting user (-User <SID>) — the engine's own account
        // (SYSTEM when running as a service) has no such packages installed.
        public static string RemoveAppxPackage(string packageFullName, Action<string> progressCallback, ClientContext ctx)
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
                // The SID is validated by SecurityIdentifier, so it is a plain S-1-... string.
                string sid = new System.Security.Principal.SecurityIdentifier(ctx.Sid).Value;
                // -ErrorAction Stop + exit codes: previously errors were silenced, so a failed
                // removal still reported "Successfully removed". Exit 2 = nothing was installed.
                psi.ArgumentList.Add(
                    $"$p = @(Get-AppxPackage -User '{sid}' -Name '{identity}*'); " +
                    "if ($p.Count -eq 0) { exit 2 }; " +
                    $"try {{ $p | ForEach-Object {{ Remove-AppxPackage -Package $_.PackageFullName -User '{sid}' -ErrorAction Stop }}; exit 0 }} " +
                    "catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }");

                using var proc = System.Diagnostics.Process.Start(psi)!;
                var stderr = proc.StandardError.ReadToEndAsync();
                proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(60000))
                {
                    try { proc.Kill(); } catch { }
                    return $"Error removing {identity}: timed out.";
                }

                return proc.ExitCode switch
                {
                    0 => $"Successfully removed: {identity}",
                    2 => $"{identity} is not installed for this user.",
                    _ => $"Error removing {identity}: {stderr.Result.Trim()}"
                };
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
