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
            
            // If quoted, extract content inside quotes
            if (rawCommand.StartsWith("\""))
            {
                int endQuoteIndex = rawCommand.IndexOf("\"", 1);
                if (endQuoteIndex > 0)
                {
                    return rawCommand.Substring(1, endQuoteIndex - 1);
                }
            }

            // Otherwise, grab up to the first space or argument switch
            string[] parts = rawCommand.Split(' ');
            if (parts.Length > 0)
            {
                string path = parts[0];
                // Check if it's a valid environment variable like %SystemRoot%
                if (path.StartsWith("%"))
                {
                    path = Environment.ExpandEnvironmentVariables(path);
                }
                return path;
            }

            return rawCommand;
        }

        public bool Repair(List<RegistryIssue> issues, Action<string, int>? progressCallback = null)
        {
            int count = 0;
            string backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RegistryBackups");
            if (!Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }

            string backupFile = Path.Combine(backupDir, $"backup_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            using (var writer = new StreamWriter(backupFile, false))
            {
                writer.WriteLine($"; TuneUp Registry Repair Backup - Executed {DateTime.Now}");
                writer.WriteLine("; Revert changes by manually adding these back if required.");
                writer.WriteLine();

                foreach (var issue in issues)
                {
                    progressCallback?.Invoke($"Repairing {issue.Title}", (count * 100) / issues.Count);
                    count++;

                    // Back up to log file
                    writer.WriteLine($"[{issue.KeyPath}]");
                    writer.WriteLine($"\"{issue.ValueName}\"=\"{issue.TargetPath}\"");
                    writer.WriteLine();

                    try
                    {
                        // Parse Registry Key Path
                        string hive = issue.KeyPath.Substring(0, 4);
                        string subkey = issue.KeyPath.Substring(5);

                        RegistryKey rootKey = null;
                        if (hive == "HKCU") rootKey = Registry.CurrentUser;
                        else if (hive == "HKLM") rootKey = Registry.LocalMachine;

                        if (rootKey != null)
                        {
                            if (issue.IssueType == "COM")
                            {
                                // COM keys have the target as default property in subkey (e.g. InprocServer32). 
                                // We can delete the entire InprocServer32 / LocalServer32 subkey or the CLSID itself.
                                // It's safer to delete the CLSID key.
                                string clsidKeyPath = subkey.Substring(0, subkey.LastIndexOf('\\'));
                                rootKey.DeleteSubKeyTree(clsidKeyPath, false);
                            }
                            else
                            {
                                using (var key = rootKey.OpenSubKey(subkey, true))
                                {
                                    if (key != null)
                                    {
                                        if (string.IsNullOrEmpty(issue.ValueName))
                                        {
                                            // Delete the default value
                                            key.DeleteValue("", false);
                                        }
                                        else
                                        {
                                            key.DeleteValue(issue.ValueName, false);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        writer.WriteLine($"; ERROR: Failed to repair - {ex.Message}");
                    }
                }
            }

            progressCallback?.Invoke("Repair Complete", 100);
            return true;
        }
    }
}
