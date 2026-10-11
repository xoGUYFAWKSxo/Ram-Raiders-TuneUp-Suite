using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TuneUpEngine
{
    public class CleanRule
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public List<string> DetectPaths { get; set; } = new List<string>();
        public List<string> FileKeys { get; set; } = new List<string>(); // "Path|Pattern|Flags"
        public List<string> ExcludeKeys { get; set; } = new List<string>(); // "FILE|Path|Pattern" or "PATH|Path|Pattern"
        public bool IsDefault { get; set; }
        public bool IsExternal { get; set; } // Loaded from winapp2.ini (replaced wholesale on reload)
    }

    // A parsed winapp2 ExcludeKey. Files matching one of a rule's exclusions are never scanned or
    // cleaned — the rule author uses these to protect settings/state that lives next to the cache.
    public record ExcludeRule(bool IsPathRule, string Directory, string? Pattern);

    public class ScanItem : System.ComponentModel.INotifyPropertyChanged
    {
        // Identifies this result to the engine's ResultStore. Cleaning sends back only these IDs.
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string RuleName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public List<string> FilePaths { get; set; } = new List<string>();
        public long TotalSizeBytes { get; set; }

        private bool _isSelected = false; // Unchecked by default
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

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public string SizeDisplay
        {
            get
            {
                if (TotalSizeBytes >= 1024L * 1024L * 1024L)
                    return $"{TotalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
                if (TotalSizeBytes >= 1024L * 1024L)
                    return $"{TotalSizeBytes / (1024.0 * 1024.0):F1} MB";
                if (TotalSizeBytes >= 1024L)
                    return $"{TotalSizeBytes / 1024.0:F0} KB";
                return $"{TotalSizeBytes} Bytes";
            }
        }
    }

    public class Winapp2Parser
    {

        private readonly List<CleanRule> _rules = new List<CleanRule>();
        private readonly object _rulesLock = new object();
        private readonly AdvancedScanner _hasher = new AdvancedScanner();

        public Winapp2Parser()
        {
            LoadBuiltInRules();
        }

        private void LoadBuiltInRules()
        {
            // Category: Web Browsers
            _rules.Add(new CleanRule
            {
                Name = "Google Chrome - Cache",
                Category = "Web Browsers",
                IsDefault = true,
                DetectPaths = new List<string> { "%LocalAppData%\\Google\\Chrome\\User Data" },
                FileKeys = new List<string>
                {
                    "%LocalAppData%\\Google\\Chrome\\User Data\\Default\\Cache|*.*|RECURSE",
                    "%LocalAppData%\\Google\\Chrome\\User Data\\Default\\Code Cache\\js|*.*|RECURSE"
                }
            });

            _rules.Add(new CleanRule
            {
                Name = "Microsoft Edge - Cache",
                Category = "Web Browsers",
                IsDefault = true,
                DetectPaths = new List<string> { "%LocalAppData%\\Microsoft\\Edge\\User Data" },
                FileKeys = new List<string>
                {
                    "%LocalAppData%\\Microsoft\\Edge\\User Data\\Default\\Cache|*.*|RECURSE"
                }
            });

            // Category: System
            _rules.Add(new CleanRule
            {
                Name = "Windows Temporary Files",
                Category = "System",
                IsDefault = true,
                FileKeys = new List<string>
                {
                    "%Temp%|*.*|RECURSE"
                }
            });

            _rules.Add(new CleanRule
            {
                Name = "Windows Error Reporting & Logs",
                Category = "System",
                IsDefault = true,
                FileKeys = new List<string>
                {
                    // NOTE: deliberately does NOT target %WinDir%\System32\winevt\Logs\*.evtx. Those are
                    // the Windows Event Logs (Security/System/Application history) — deleting them wipes
                    // audit and troubleshooting data and is not "junk" cleaning. (The correct way to clear
                    // an event log is `wevtutil cl`, which is out of scope for a file cleaner.)
                    "%LocalAppData%\\Microsoft\\Windows\\WER|*.*|RECURSE",
                    "%WinDir%\\Logs\\CBS|*.log|RECURSE",
                    "%WinDir%\\Logs\\DISM|*.log|RECURSE"
                }
            });

            _rules.Add(new CleanRule
            {
                Name = "DirectX Shader Cache",
                Category = "System",
                IsDefault = true,
                FileKeys = new List<string>
                {
                    "%LocalAppData%\\D3DSCache|*.*|RECURSE"
                }
            });

            _rules.Add(new CleanRule
            {
                Name = "Prefetch Data",
                Category = "System",
                IsDefault = false,
                DetectPaths = new List<string> { "%WinDir%\\Prefetch" },
                FileKeys = new List<string>
                {
                    "%WinDir%\\Prefetch|*.*"
                }
            });

            _rules.Add(new CleanRule
            {
                Name = "Delivery Optimization Files",
                Category = "System",
                IsDefault = true,
                FileKeys = new List<string>
                {
                    "%CommonAppData%\\Microsoft\\Windows\\DeliveryOptimization\\Cache|*.*|RECURSE"
                }
            });

            _rules.Add(new CleanRule
            {
                Name = "Recycle Bin",
                Category = "System",
                IsDefault = true,
                FileKeys = new List<string>
                {
                    "RECYCLE_BIN|*.*"
                }
            });
        }

        public void LoadExternalWinapp2Ini(string filePath)
        {
            if (!File.Exists(filePath)) return;

            try
            {
                string[] lines = File.ReadAllLines(filePath);
                var loadedRules = new List<CleanRule>();
                CleanRule currentRule = null;

                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("#"))
                        continue;

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        if (currentRule != null && currentRule.FileKeys.Count > 0)
                        {
                            loadedRules.Add(currentRule);
                        }

                        string name = trimmed.Substring(1, trimmed.Length - 2);
                        currentRule = new CleanRule
                        {
                            Name = name,
                            Category = "Third-Party Apps",
                            IsDefault = true,
                            IsExternal = true
                        };
                    }
                    else if (currentRule != null)
                    {
                        var match = Regex.Match(trimmed, @"^(Detect|DetectFile|FileKey\d+|ExcludeKey\d+)=(.*)$", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            string key = match.Groups[1].Value;
                            string val = match.Groups[2].Value;

                            if (key.Equals("Detect", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("DetectFile", StringComparison.OrdinalIgnoreCase))
                            {
                                currentRule.DetectPaths.Add(val);
                            }
                            else if (key.StartsWith("FileKey", StringComparison.OrdinalIgnoreCase))
                            {
                                currentRule.FileKeys.Add(val);
                            }
                            else if (key.StartsWith("ExcludeKey", StringComparison.OrdinalIgnoreCase))
                            {
                                currentRule.ExcludeKeys.Add(val);
                            }
                        }
                    }
                }

                if (currentRule != null && currentRule.FileKeys.Count > 0)
                {
                    loadedRules.Add(currentRule);
                }

                // Replace (not append to) the previous external set, so re-syncing definitions does
                // not stack a second copy of every rule on top of the first.
                lock (_rulesLock)
                {
                    _rules.RemoveAll(r => r.IsExternal);
                    _rules.AddRange(loadedRules);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Winapp2] Failed to parse {filePath}: {ex.Message}");
            }
        }

        public string ExpandSpecialFolder(string path) => ExpandSpecialFolder(path, UserFolders.ForCurrentProcess());

        // Translates winapp2 tokens to real folders. Per-user tokens (%LocalAppData%, %Temp%, ...)
        // resolve to the REQUESTING user's folders - not the engine's, which would be SYSTEM's
        // profile when running as a service. (Plain string replacement: a folder path containing
        // '$' must not be treated as a regex substitution.)
        public static string ExpandSpecialFolder(string path, UserFolders folders)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            string R(string value, string token, string replacement) =>
                value.Replace(token, replacement, StringComparison.OrdinalIgnoreCase);

            path = R(path, "%LocalAppData%", folders.LocalAppData);
            path = R(path, "%AppData%", folders.RoamingAppData);
            path = R(path, "%UserProfile%", folders.Profile);
            path = R(path, "%Documents%", folders.Documents);
            path = R(path, "%Temp%", folders.Temp);
            path = R(path, "%CommonAppData%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            path = R(path, "%WinDir%", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            path = R(path, "%SystemDrive%", Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");

            return path;
        }

        private static bool IsRuleDetected(CleanRule rule, UserFolders folders)
        {
            if (rule.DetectPaths.Count == 0) return true; // Always scan if no specific detection path is defined

            foreach (var detectPath in rule.DetectPaths)
            {
                string expanded = ExpandSpecialFolder(detectPath, folders);
                if (Directory.Exists(expanded) || File.Exists(expanded))
                {
                    return true;
                }
            }

            return false;
        }

        public List<ScanItem> Scan(Action<string, int>? progressCallback = null) =>
            Scan(ClientContext.ForCurrentProcess(), progressCallback);

        public List<ScanItem> Scan(ClientContext ctx, Action<string, int>? progressCallback = null)
        {
            var results = new List<ScanItem>();
            int count = 0;

            // Snapshot so a definitions sync running concurrently can't modify the list mid-scan.
            List<CleanRule> rules;
            lock (_rulesLock)
            {
                rules = new List<CleanRule>(_rules);
            }

            foreach (var rule in rules)
            {
                progressCallback?.Invoke(rule.Name, (count * 100) / rules.Count);
                count++;

                if (!IsRuleDetected(rule, ctx.Folders)) continue;

                var item = new ScanItem
                {
                    RuleName = rule.Name,
                    Category = rule.Category
                };

                var excludes = rule.ExcludeKeys
                    .Select(key => ParseExcludeKey(key, ctx.Folders))
                    .OfType<ExcludeRule>()
                    .ToList();
                var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var fileKey in rule.FileKeys)
                {
                    if (fileKey.StartsWith("RECYCLE_BIN", StringComparison.OrdinalIgnoreCase))
                    {
                        // The user's own recycle folders on every fixed drive, with their real
                        // size (this used to be a flat 50 MB guess).
                        long binBytes = RecycleBinSize(ctx.Sid);
                        if (binBytes > 0)
                        {
                            item.FilePaths.Add("RECYCLE_BIN");
                            item.TotalSizeBytes += binBytes;
                        }
                        continue;
                    }

                    string[] parts = fileKey.Split('|');
                    if (parts.Length < 1) continue;

                    string expandedPath = ExpandSpecialFolder(parts[0], ctx.Folders);
                    string pattern = parts.Length > 1 ? parts[1] : "*.*";
                    bool recurse = parts.Length > 2 && parts[2].Equals("RECURSE", StringComparison.OrdinalIgnoreCase);

                    if (!Directory.Exists(expandedPath)) continue;

                    // Never follow junctions/symlinks: the engine is elevated, and a link planted in a
                    // user-writable cache or temp folder could otherwise redirect deletion anywhere.
                    // Inaccessible subfolders are skipped instead of aborting the whole key.
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = recurse,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        MatchType = MatchType.Win32
                    };

                    // winapp2 allows several patterns in one key, separated by ';' (e.g. "*.log;*.tmp").
                    foreach (var singlePattern in pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        try
                        {
                            foreach (var file in Directory.EnumerateFiles(expandedPath, singlePattern, options))
                            {
                                if (!seenFiles.Add(file) || IsExcluded(file, excludes)) continue;
                                try
                                {
                                    var fi = new FileInfo(file);
                                    item.FilePaths.Add(file);
                                    item.TotalSizeBytes += fi.Length;
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }

                if (item.FilePaths.Count > 0)
                {
                    results.Add(item);
                }
            }

            progressCallback?.Invoke("Scan Complete", 100);
            return results;
        }

        public long Clean(List<ScanItem> items, Action<string, int>? progressCallback = null) =>
            Clean(items, ClientContext.ForCurrentProcess(), progressCallback);

        // Who deletes what:
        //   * Files inside the requesting user's profile are deleted AS that user, so Windows itself
        //     guarantees nothing is removed that the user couldn't remove — a junction swapped into
        //     a cache folder between scan and clean can't borrow the engine's rights.
        //   * Anything outside the profile (Windows logs, Prefetch, Delivery Optimization) needs
        //     administrator rights and is only cleaned for administrator accounts.
        public long Clean(List<ScanItem> items, ClientContext ctx, Action<string, int>? progressCallback = null)
        {
            long deletedBytes = 0;
            int count = 0;

            foreach (var item in items)
            {
                progressCallback?.Invoke($"Cleaning {item.RuleName}", (count * 100) / items.Count);
                count++;

                // Duplicates are re-verified at delete time: files can change between scan and
                // clean. If the copy we keep is gone or unreadable, deleting the "extras" would
                // leave the user with no copy at all, so the whole set is skipped.
                string? keptHash = null;
                bool isDuplicateSet = item.Category == AdvancedScanner.DuplicateFilesCategory;
                if (isDuplicateSet)
                {
                    string? kept = GetKeptDuplicate(item);
                    keptHash = kept == null ? null : _hasher.GetFullHash(kept);
                    if (keptHash == null) continue;
                }

                foreach (var path in GetDeletionTargets(item))
                {
                    if (isDuplicateSet && _hasher.GetFullHash(path) != keptHash)
                        continue; // No longer identical to the kept copy — leave it alone.

                    if (path == "RECYCLE_BIN")
                    {
                        deletedBytes += ctx.RunAsUser(() => EmptyRecycleBin(ctx.Sid));
                        continue;
                    }

                    if (ctx.Folders.Contains(path))
                        deletedBytes += ctx.RunAsUser(() => DeletePath(path));
                    else if (ctx.IsAdministrator)
                        deletedBytes += DeletePath(path);
                    // else: a system location and the user isn't an administrator — skip.
                }
            }

            progressCallback?.Invoke("Clean Complete", 100);
            return deletedBytes;
        }

        // Deletes one scanned path; returns the bytes freed. Folders (from the empty-folder scan)
        // are only removed if still empty.
        private static long DeletePath(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: false);
                    return 0;
                }

                if (!File.Exists(path)) return 0;
                long size = new FileInfo(path).Length;
                File.Delete(path);
                return size;
            }
            catch
            {
                return 0; // File locked or access denied — skip silently
            }
        }

        // X:\$Recycle.Bin\<SID> on every fixed drive: where Windows keeps this user's deleted files.
        private static IEnumerable<string> RecycleFolders(string sid)
        {
            if (string.IsNullOrEmpty(sid)) yield break;
            foreach (var drive in DriveInfo.GetDrives())
            {
                bool usable;
                try { usable = drive.DriveType == DriveType.Fixed && drive.IsReady; }
                catch { usable = false; }
                if (!usable) continue;

                string folder = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
                if (Directory.Exists(folder)) yield return folder;
            }
        }

        private static readonly EnumerationOptions RecycleEnumeration = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        internal static long RecycleBinSize(string sid)
        {
            long total = 0;
            foreach (var folder in RecycleFolders(sid))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*", RecycleEnumeration))
                    {
                        if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        try { total += new FileInfo(file).Length; } catch { }
                    }
                }
                catch { }
            }
            return total;
        }

        // Empties the user's recycle folders (the $R data and $I info files Windows keeps there),
        // leaving the folder itself and its desktop.ini in place.
        private static long EmptyRecycleBin(string sid)
        {
            long freed = 0;
            foreach (var folder in RecycleFolders(sid))
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    if (entry.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (entry is DirectoryInfo dir)
                        {
                            long size = 0;
                            if ((dir.Attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                foreach (var f in dir.EnumerateFiles("*", RecycleEnumeration))
                                {
                                    try { size += f.Length; } catch { }
                                }
                            }
                            dir.Delete(recursive: true); // Removes a link itself, never its target
                            freed += size;
                        }
                        else
                        {
                            long size = ((FileInfo)entry).Length;
                            entry.Delete();
                            freed += size;
                        }
                    }
                    catch
                    {
                        // In use or access denied — leave it.
                    }
                }
            }
            return freed;
        }

        // The paths that cleaning an item is allowed to delete. A duplicate set lists every copy of
        // the same content, so the first listed copy is always kept — deleting every path would
        // destroy the data rather than de-duplicate it. A "set" that collapses to a single distinct
        // path (the same file listed twice) has nothing redundant and is skipped entirely.
        internal static IEnumerable<string> GetDeletionTargets(ScanItem item)
        {
            if (item?.FilePaths == null) return Enumerable.Empty<string>();

            if (item.Category != AdvancedScanner.DuplicateFilesCategory)
                return item.FilePaths;

            var distinct = item.FilePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => { try { return Path.GetFullPath(p); } catch { return p; } })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return distinct.Count < 2 ? Enumerable.Empty<string>() : distinct.Skip(1);
        }

        // The copy of a duplicate set that cleaning keeps (the first listed path).
        internal static string? GetKeptDuplicate(ScanItem item)
        {
            string? first = item?.FilePaths?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (first == null) return null;
            try { return Path.GetFullPath(first); } catch { return first; }
        }

        // Parses "FILE|<dir>|<pattern>", "FILE|<full path>" or "PATH|<dir>[|<pattern>]".
        // REG exclusions (and anything unrecognised) return null: this cleaner never touches the registry.
        internal ExcludeRule? ParseExcludeKey(string excludeKey) =>
            ParseExcludeKey(excludeKey, UserFolders.ForCurrentProcess());

        internal static ExcludeRule? ParseExcludeKey(string excludeKey, UserFolders folders)
        {
            if (string.IsNullOrWhiteSpace(excludeKey)) return null;

            var parts = excludeKey.Split('|');
            if (parts.Length < 2) return null;

            string kind = parts[0].Trim();
            bool isPath = kind.Equals("PATH", StringComparison.OrdinalIgnoreCase);
            bool isFile = kind.Equals("FILE", StringComparison.OrdinalIgnoreCase);
            if (!isPath && !isFile) return null;

            string? location = ExpandSpecialFolder(parts[1].Trim(), folders);
            string? pattern = parts.Length > 2 ? parts[2].Trim() : null;

            if (isFile && string.IsNullOrEmpty(pattern))
            {
                // A bare file path: split it into its directory and exact file name.
                pattern = Path.GetFileName(location);
                location = Path.GetDirectoryName(location);
            }

            if (string.IsNullOrEmpty(location)) return null;

            try
            {
                location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            }
            catch
            {
                return null;
            }

            return new ExcludeRule(isPath, location, string.IsNullOrEmpty(pattern) ? null : pattern);
        }

        internal static bool IsExcluded(string filePath, IReadOnlyList<ExcludeRule>? excludes)
        {
            if (excludes == null || excludes.Count == 0) return false;

            string fileDir = Path.GetDirectoryName(filePath) ?? string.Empty;
            string fileName = Path.GetFileName(filePath);

            foreach (var rule in excludes)
            {
                bool directoryMatches = rule.IsPathRule
                    // PATH covers the directory and everything beneath it.
                    ? fileDir.Equals(rule.Directory, StringComparison.OrdinalIgnoreCase) ||
                      fileDir.StartsWith(rule.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    // FILE only covers files sitting directly in that directory.
                    : fileDir.Equals(rule.Directory, StringComparison.OrdinalIgnoreCase);

                if (!directoryMatches) continue;

                if (rule.Pattern == null) return true;

                foreach (var pattern in rule.Pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (System.IO.Enumeration.FileSystemName.MatchesWin32Expression(pattern, fileName, ignoreCase: true))
                        return true;
                }
            }

            return false;
        }
    }
}
