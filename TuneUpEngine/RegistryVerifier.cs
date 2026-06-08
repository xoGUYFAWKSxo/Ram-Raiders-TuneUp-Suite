using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TuneUpEngine
{
    public class RegistryIssue
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string KeyPath { get; set; }
        public string ValueName { get; set; }
        public string TargetPath { get; set; }
        public string IssueType { get; set; } // "Startup", "ShellExtension", "COM"
    }

    public class RegistryVerifier
    {
        public List<RegistryIssue> Scan(Action<string, int>? progressCallback = null)
        {
            var issues = new List<RegistryIssue>();
            
            progressCallback?.Invoke("Scanning Startup Keys...", 10);
            ScanStartupKeys(issues);

            progressCallback?.Invoke("Scanning Shell Extensions...", 40);
            ScanShellExtensions(issues);

            progressCallback?.Invoke("Scanning User OLE/COM Registrations...", 70);
            ScanUserComRegistrations(issues);

            progressCallback?.Invoke("Registry Scan Complete", 100);
            return issues;
        }

        private void ScanStartupKeys(List<RegistryIssue> issues)
        {
            string[] runKeys = new string[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
            };

            // Scan HKCU Run keys
            foreach (var subKeyPath in runKeys)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(subKeyPath, false))
                {
                    if (key == null) continue;
                    foreach (var valueName in key.GetValueNames())
                    {
                        var val = key.GetValue(valueName)?.ToString();
                        if (string.IsNullOrEmpty(val)) continue;

                        string exePath = ExtractExecutablePath(val);
                        if (!string.IsNullOrEmpty(exePath) && !File.Exists(exePath) && !exePath.Contains("System32"))
                        {
                            issues.Add(new RegistryIssue
                            {
                                Id = Guid.NewGuid().ToString(),
                                Title = $"Broken Startup Command: {valueName}",
                                KeyPath = @"HKCU\" + subKeyPath,
                                ValueName = valueName,
                                TargetPath = exePath,
                                IssueType = "Startup"
                            });
                        }
                    }
                }
            }

            // Scan HKLM Run keys
            foreach (var subKeyPath in runKeys)
            {
                using (var key = Registry.LocalMachine.OpenSubKey(subKeyPath, false))
                {
                    if (key == null) continue;
                    foreach (var valueName in key.GetValueNames())
                    {
                        var val = key.GetValue(valueName)?.ToString();
                        if (string.IsNullOrEmpty(val)) continue;

                        string exePath = ExtractExecutablePath(val);
                        if (!string.IsNullOrEmpty(exePath) && !File.Exists(exePath) && !exePath.Contains("System32"))
                        {
                            issues.Add(new RegistryIssue
                            {
                                Id = Guid.NewGuid().ToString(),
                                Title = $"Broken System Startup Command: {valueName}",
                                KeyPath = @"HKLM\" + subKeyPath,
                                ValueName = valueName,
                                TargetPath = exePath,
                                IssueType = "Startup"
                            });
                        }
                    }
                }
            }
        }

        private void ScanShellExtensions(List<RegistryIssue> issues)
        {
            string approvedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";

            using (var key = Registry.LocalMachine.OpenSubKey(approvedKeyPath, false))
            {
                if (key == null) return;

                foreach (var valueName in key.GetValueNames())
                {
                    // valueName is the CLSID GUID
                    string clsid = valueName;
                    
                    // Look up CLSID path
                    string path = GetClsidPath(clsid);
                    if (string.IsNullOrEmpty(path)) continue;

                    string expanded = Environment.ExpandEnvironmentVariables(path);
                    if (!string.IsNullOrEmpty(expanded) && !File.Exists(expanded) && !expanded.Contains("System32"))
                    {
                        issues.Add(new RegistryIssue
                        {
                            Id = Guid.NewGuid().ToString(),
                            Title = $"Broken Shell Extension: {key.GetValue(valueName)}",
                            KeyPath = @"HKLM\" + approvedKeyPath,
                            ValueName = clsid,
                            TargetPath = expanded,
                            IssueType = "ShellExtension"
                        });
                    }
                }
            }
        }

        private void ScanUserComRegistrations(List<RegistryIssue> issues)
        {
            // Scanning HKEY_CURRENT_USER\Software\Classes\CLSID is fast and safe
            string userClsidPath = @"Software\Classes\CLSID";

            using (var key = Registry.CurrentUser.OpenSubKey(userClsidPath, false))
            {
                if (key == null) return;

                var subkeyNames = key.GetSubKeyNames();
                int total = subkeyNames.Length;
                int count = 0;

                foreach (var subkeyName in subkeyNames)
                {
                    count++;
                    using (var clsidKey = key.OpenSubKey(subkeyName))
                    {
                        if (clsidKey == null) continue;

                        string serverPath = null;
                        string valueName = "";
                        string serverKeyName = "";

                        using (var inproc = clsidKey.OpenSubKey("InprocServer32"))
                        {
                            if (inproc != null)
                            {
                                serverPath = inproc.GetValue("")?.ToString();
                                serverKeyName = "InprocServer32";
                            }
                        }

                        if (string.IsNullOrEmpty(serverPath))
                        {
                            using (var local = clsidKey.OpenSubKey("LocalServer32"))
                            {
                                if (local != null)
                                {
                                    serverPath = local.GetValue("")?.ToString();
                                    serverKeyName = "LocalServer32";
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(serverPath)) continue;

                        string expanded = Environment.ExpandEnvironmentVariables(serverPath);
                        string exePath = ExtractExecutablePath(expanded);

                        if (!string.IsNullOrEmpty(exePath) && !File.Exists(exePath) && !exePath.Contains("System32"))
                        {
                            issues.Add(new RegistryIssue
                            {
                                Id = Guid.NewGuid().ToString(),
                                Title = $"Dead OLE/COM Server: {subkeyName}",
                                KeyPath = $@"HKCU\{userClsidPath}\{subkeyName}\{serverKeyName}",
                                ValueName = valueName,
                                TargetPath = exePath,
                                IssueType = "COM"
                            });
                        }
                    }
                }
            }
        }

        private string GetClsidPath(string clsid)
        {
            // First check user classes CLSID
            string path = $@"Software\Classes\CLSID\{clsid}\InprocServer32";
            using (var k = Registry.CurrentUser.OpenSubKey(path))
            {
                if (k != null) return k.GetValue("")?.ToString();
            }

            // Then check global machine classes CLSID
            path = $@"CLSID\{clsid}\InprocServer32";
            using (var k = Registry.ClassesRoot.OpenSubKey(path))
            {
                if (k != null) return k.GetValue("")?.ToString();
            }

            path = $@"CLSID\{clsid}\LocalServer32";
            using (var k = Registry.ClassesRoot.OpenSubKey(path))
            {
                if (k != null) return k.GetValue("")?.ToString();
            }

            return null;
        }

        public string ExtractExecutablePath(string rawCommand)
        {
            if (string.IsNullOrEmpty(rawCommand)) return string.Empty;

            rawCommand = rawCommand.Trim();

            // If quoted, the executable is exactly what is inside the first pair of quotes.
            if (rawCommand.StartsWith("\""))
            {
                int endQuoteIndex = rawCommand.IndexOf("\"", 1);
                if (endQuoteIndex > 0)
                {
                    return Environment.ExpandEnvironmentVariables(rawCommand.Substring(1, endQuoteIndex - 1));
                }
            }

            // Unquoted commands frequently contain spaces in the path itself, e.g.
            //   C:\Program Files\App\app.exe --flag
            // The old code split on the first space and returned "C:\Program", which does not exist,
            // so a perfectly valid startup entry got reported as "broken" — and could then be deleted
            // by the user. Instead, cut at the end of the first ".exe" token when one is present.
            string expanded = Environment.ExpandEnvironmentVariables(rawCommand);
            int exeIdx = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx >= 0)
            {
                return expanded.Substring(0, exeIdx + 4);
            }

            // No ".exe" found — fall back to the first whitespace-delimited token.
            int spaceIdx = expanded.IndexOf(' ');
            return spaceIdx > 0 ? expanded.Substring(0, spaceIdx) : expanded;
        }

        public bool Repair(List<RegistryIssue> issues, Action<string, int>? progressCallback = null)
        {
            int count = 0;

            // Back up to the documented, user-visible location (the same folder the rest of the suite
            // uses), NOT next to the executable where it would be wiped on reinstall.
            string backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RamRaidersTuneUpSuite", "RegistryBackups");
            Directory.CreateDirectory(backupDir);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string manifestFile = Path.Combine(backupDir, $"RegistryRepair_{stamp}.txt");
            using (var writer = new StreamWriter(manifestFile, false))
            {
                writer.WriteLine($"; Ram Raiders TuneUp Registry Repair - Executed {DateTime.Now}");
                writer.WriteLine("; Each repaired key was exported to a .reg file in this folder before deletion.");
                writer.WriteLine("; To undo a repair, double-click the matching .reg file to re-import the key.");
                writer.WriteLine();

                foreach (var issue in issues)
                {
                    progressCallback?.Invoke($"Repairing {issue.Title}", (count * 100) / issues.Count);
                    count++;

                    try
                    {
                        // Parse Registry Key Path
                        string hive = issue.KeyPath.Substring(0, 4);
                        string subkey = issue.KeyPath.Substring(5);

                        RegistryKey rootKey = null;
                        if (hive == "HKCU") rootKey = Registry.CurrentUser;
                        else if (hive == "HKLM") rootKey = Registry.LocalMachine;
                        if (rootKey == null) continue;

                        if (issue.IssueType == "COM")
                        {
                            // For a dead COM server we remove the whole CLSID key (not just the
                            // InprocServer32/LocalServer32 child), so export the CLSID key itself.
                            string clsidKeyPath = subkey.Substring(0, subkey.LastIndexOf('\\'));
                            string fullClsidPath = $@"{hive}\{clsidKeyPath}";

                            string regBackup = Path.Combine(backupDir, $"{stamp}_{count}_COM.reg");
                            ExportKeyToReg(fullClsidPath, regBackup);
                            writer.WriteLine($"[DELETE TREE] {fullClsidPath}  ->  {Path.GetFileName(regBackup)}");

                            rootKey.DeleteSubKeyTree(clsidKeyPath, false);
                        }
                        else
                        {
                            // Export the containing key before removing the offending value.
                            string regBackup = Path.Combine(backupDir, $"{stamp}_{count}_{issue.IssueType}.reg");
                            ExportKeyToReg(issue.KeyPath, regBackup);
                            writer.WriteLine($"[DELETE VALUE] {issue.KeyPath}\\{issue.ValueName}  ->  {Path.GetFileName(regBackup)}");

                            using (var key = rootKey.OpenSubKey(subkey, true))
                            {
                                key?.DeleteValue(string.IsNullOrEmpty(issue.ValueName) ? "" : issue.ValueName, false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        writer.WriteLine($"; ERROR: Failed to repair '{issue.Title}' - {ex.Message}");
                    }
                }
            }

            progressCallback?.Invoke("Repair Complete", 100);
            return true;
        }

        // Exports a registry key to a .reg file via reg.exe so the repair is genuinely reversible
        // (double-clicking the .reg re-imports the key). reg.exe accepts the short hive names
        // (HKLM, HKCU, ...) used in our KeyPath strings.
        private void ExportKeyToReg(string fullKeyPath, string regFilePath)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "reg.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("export");
                psi.ArgumentList.Add(fullKeyPath);
                psi.ArgumentList.Add(regFilePath);
                psi.ArgumentList.Add("/y");

                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(10000);
            }
            catch
            {
                // Export is best-effort; a failed export must not block the repair, but the manifest
                // line still records what was removed.
            }
        }
    }
}
