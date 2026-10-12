using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TuneUpEngine
{
    public class RegistryIssue : System.ComponentModel.INotifyPropertyChanged
    {
        // Bound to the "Select" checkbox in the Registry Guard list. Only selected issues are sent
        // for repair. Unchecked by default, matching the cleaner, so nothing is removed unless the
        // user opts in.
        private bool _isSelected = false;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string KeyPath { get; set; } = "";
        public string ValueName { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public string IssueType { get; set; } = ""; // "Startup", "ShellExtension", "COM"
    }

    public class RegistryVerifier
    {
        // "HKCU" in a scan always means the requesting user's own hive (HKEY_USERS\<their SID>),
        // never the engine's - which is SYSTEM's when running as a service.
        public List<RegistryIssue> Scan(ClientContext ctx, Action<string, int>? progressCallback = null)
        {
            var issues = new List<RegistryIssue>();
            using var userHive = ctx.OpenUserHive();
            
            progressCallback?.Invoke("Scanning Startup Keys...", 10);
            ScanStartupKeys(issues, userHive.Key);

            progressCallback?.Invoke("Scanning Shell Extensions...", 40);
            ScanShellExtensions(issues, userHive.Key);

            progressCallback?.Invoke("Scanning User OLE/COM Registrations...", 70);
            ScanUserComRegistrations(issues, userHive.Key);

            progressCallback?.Invoke("Registry Scan Complete", 100);
            return issues;
        }

        private void ScanStartupKeys(List<RegistryIssue> issues, RegistryKey? userHive)
        {
            string[] runKeys = new string[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
            };

            // Scan HKCU Run keys
            foreach (var subKeyPath in runKeys)
            {
                using (var key = userHive?.OpenSubKey(subKeyPath, false))
                {
                    if (key == null) continue;
                    foreach (var valueName in key.GetValueNames())
                    {
                        var val = key.GetValue(valueName)?.ToString();
                        if (string.IsNullOrEmpty(val)) continue;

                        string exePath = ExtractExecutablePath(val);
                        if (IsMissingTarget(exePath))
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
                        if (IsMissingTarget(exePath))
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

        private void ScanShellExtensions(List<RegistryIssue> issues, RegistryKey? userHive)
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
                    string? path = GetClsidPath(clsid, userHive);
                    if (string.IsNullOrEmpty(path)) continue;

                    string expanded = ExtractServerPath(path, isCommandLine: false);
                    if (IsMissingTarget(expanded))
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

        private void ScanUserComRegistrations(List<RegistryIssue> issues, RegistryKey? userHive)
        {
            // Scanning HKEY_CURRENT_USER\Software\Classes\CLSID is fast and safe
            string userClsidPath = @"Software\Classes\CLSID";

            using (var key = userHive?.OpenSubKey(userClsidPath, false))
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

                        string? serverPath = null;
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

                        string exePath = ExtractServerPath(serverPath, isCommandLine: serverKeyName == "LocalServer32");

                        if (IsMissingTarget(exePath))
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

        // Resolves a CLSID the way Windows does for this user: their own classes first, then the
        // machine's. (HKEY_CLASSES_ROOT can't be used - in a service it merges SYSTEM's classes.)
        private string? GetClsidPath(string clsid, RegistryKey? userHive)
        {
            foreach (var server in new[] { "InprocServer32", "LocalServer32" })
            {
                using (var k = userHive?.OpenSubKey($@"Software\Classes\CLSID\{clsid}\{server}"))
                {
                    if (k != null) return k.GetValue("")?.ToString();
                }

                using (var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsid}\{server}"))
                {
                    if (k != null) return k.GetValue("")?.ToString();
                }
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

        // COM InprocServer32 / shell-extension values hold a plain (possibly quoted) file path, which
        // may legitimately contain spaces and has no ".exe" — so it must not go through the
        // command-line splitter, which would truncate "C:\Program Files\x.dll" to "C:\Program".
        // LocalServer32 values are command lines and do.
        internal string ExtractServerPath(string rawValue, bool isCommandLine)
        {
            if (string.IsNullOrWhiteSpace(rawValue)) return string.Empty;
            if (isCommandLine) return ExtractExecutablePath(Environment.ExpandEnvironmentVariables(rawValue));

            string value = rawValue.Trim();
            if (value.StartsWith("\""))
            {
                int endQuote = value.IndexOf('"', 1);
                value = endQuote > 0 ? value.Substring(1, endQuote - 1) : value.Trim('"');
            }
            return Environment.ExpandEnvironmentVariables(value);
        }

        // Only report a target as missing when we can be certain. Repair deletes what the scan
        // reports, so a false positive removes a working startup entry or COM registration.
        //   * Bare names ("rundll32.exe", "mscoree.dll") are resolved by Windows through the search
        //     path / App Paths — File.Exists on them is meaningless, so they are never flagged.
        //   * A path without an extension is usually a truncated command line, not a real target.
        //   * Anything under the Windows directory belongs to the OS and is never ours to remove
        //     (the old check was a case-sensitive "System32", so "system32" paths slipped through).
        internal static bool IsMissingTarget(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!Path.IsPathFullyQualified(path)) return false;
            if (!Path.HasExtension(path)) return false;

            string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windowsDir) &&
                path.StartsWith(Path.TrimEndingDirectorySeparator(windowsDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;

            return !File.Exists(path);
        }

        // Machine-wide (HKLM) entries are only repaired for administrators; HKCU entries are the
        // requesting user's own. Backups go to that user's AppData and are written AS that user.
        // Returns one line per issue that was NOT repaired (empty = all repaired). Previously
        // failures only went into the backup manifest and the UI always said the repair worked,
        // so an entry that was never deleted looked as if it had "come back".
        public List<string> Repair(List<RegistryIssue> issues, ClientContext ctx, Action<string, int>? progressCallback = null)
        {
            var problems = new List<string>();
            int count = 0;
            string backupDir = Path.Combine(ctx.Folders.RoamingAppData, "RamRaidersTuneUpSuite", "RegistryBackups");
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var manifest = new System.Text.StringBuilder();
            var exports = new List<(string FileName, byte[] Content)>();

            manifest.AppendLine($"; Ram Raiders TuneUp Registry Repair - Executed {DateTime.Now}");
            manifest.AppendLine("; Each repaired key was exported to a .reg file in this folder before deletion.");
            manifest.AppendLine("; To undo a repair, double-click the matching .reg file to re-import the key.");
            manifest.AppendLine();

            using var userHive = ctx.OpenUserHive(writable: true);

            foreach (var issue in issues)
            {
                progressCallback?.Invoke($"Repairing {issue.Title}", (count * 100) / Math.Max(1, issues.Count));
                count++;

                try
                {
                    string hive = issue.KeyPath.Substring(0, 4);
                    string subkey = issue.KeyPath.Substring(5);

                    RegistryKey? rootKey;
                    string exportRoot;
                    if (hive == "HKCU")
                    {
                        rootKey = userHive.Key;
                        exportRoot = $@"HKU\{ctx.Sid}";
                    }
                    else if (hive == "HKLM")
                    {
                        if (!ctx.IsAdministrator)
                        {
                            manifest.AppendLine($@"; SKIPPED (administrator account required): {issue.KeyPath}\{issue.ValueName}");
                            problems.Add($"{issue.Title}: needs an administrator account.");
                            continue;
                        }
                        rootKey = Registry.LocalMachine;
                        exportRoot = "HKLM";
                    }
                    else continue;
                    if (rootKey == null) continue;

                    if (issue.IssueType == "COM")
                    {
                        // For a dead COM server we remove the whole CLSID key (not just the
                        // InprocServer32/LocalServer32 child), so export the CLSID key itself.
                        string clsidKeyPath = subkey.Substring(0, subkey.LastIndexOf('\\'));
                        string fileName = $"{stamp}_{count}_COM.reg";
                        byte[] backup = ExportKeyToReg($@"{exportRoot}\{clsidKeyPath}");
                        if (!BackupOk(backup, issue, manifest, problems)) continue;
                        exports.Add((fileName, backup));
                        manifest.AppendLine($@"[DELETE TREE] {hive}\{clsidKeyPath}  ->  {fileName}");

                        rootKey.DeleteSubKeyTree(clsidKeyPath, false);

                        using (var check = rootKey.OpenSubKey(clsidKeyPath))
                            if (check != null) Failed(issue, "the key is still there after deleting it", manifest, problems);
                    }
                    else
                    {
                        // Export the containing key before removing the offending value.
                        string fileName = $"{stamp}_{count}_{issue.IssueType}.reg";
                        byte[] backup = ExportKeyToReg($@"{exportRoot}\{subkey}");
                        if (!BackupOk(backup, issue, manifest, problems)) continue;
                        exports.Add((fileName, backup));
                        manifest.AppendLine($@"[DELETE VALUE] {issue.KeyPath}\{issue.ValueName}  ->  {fileName}");

                        string valueName = string.IsNullOrEmpty(issue.ValueName) ? "" : issue.ValueName;
                        using (var key = rootKey.OpenSubKey(subkey, true))
                        {
                            key?.DeleteValue(valueName, false);
                        }
                        using (var check = rootKey.OpenSubKey(subkey))
                            if (check?.GetValue(valueName) != null) Failed(issue, "the value is still there after deleting it", manifest, problems);
                    }
                }
                catch (Exception ex)
                {
                    Failed(issue, ex.Message, manifest, problems);
                }
            }

            // Written as the user, so a link planted in their AppData can't redirect the engine's
            // own (administrator/SYSTEM) rights to create files elsewhere.
            try
            {
                ctx.RunAsUser(() =>
                {
                    Directory.CreateDirectory(backupDir);
                    foreach (var (fileName, content) in exports)
                    {
                        if (content.Length > 0) File.WriteAllBytes(Path.Combine(backupDir, fileName), content);
                    }
                    File.WriteAllText(Path.Combine(backupDir, $"RegistryRepair_{stamp}.txt"), manifest.ToString());
                });
            }
            catch (Exception ex)
            {
                EngineLog.Warn($"[RegistryVerifier] Could not write backups to {backupDir}: {ex.Message}");
            }

            progressCallback?.Invoke("Repair Complete", 100);
            return problems;
        }

        private static void Failed(RegistryIssue issue, string reason, System.Text.StringBuilder manifest, List<string> problems)
        {
            manifest.AppendLine($"; ERROR: Failed to repair '{issue.Title}' - {reason}");
            problems.Add($"{issue.Title}: {reason}");
        }

        // Nothing is deleted without a backup to restore it from (a failed export used to be
        // ignored, so the entry was deleted with no way back).
        private static bool BackupOk(byte[] backup, RegistryIssue issue, System.Text.StringBuilder manifest, List<string> problems)
        {
            if (backup.Length > 0) return true;
            Failed(issue, "its backup couldn't be made, so it was left in place", manifest, problems);
            return false;
        }

        // Exports a registry key with reg.exe and returns the .reg file's bytes, so the caller can
        // save them as the user. reg.exe runs with the engine's rights, so it writes into the
        // engine's own protected ProgramData folder, never into a user-controlled path.
        private static byte[] ExportKeyToReg(string fullKeyPath)
        {
            string tempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RamRaidersTuneUpSuite", "tmp");
            string tempFile = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".reg");
            try
            {
                Directory.CreateDirectory(tempDir);
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
                psi.ArgumentList.Add(tempFile);
                psi.ArgumentList.Add("/y");

                using (var p = System.Diagnostics.Process.Start(psi))
                    p?.WaitForExit(10000);

                return File.Exists(tempFile) ? File.ReadAllBytes(tempFile) : Array.Empty<byte>();
            }
            catch
            {
                // Empty = no backup; Repair then leaves the entry in place (see BackupOk).
                return Array.Empty<byte>();
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }
}
