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
        public List<string> DetectPaths { get; set; } = new List<string>(); // DetectFile: file/folder paths (wildcards allowed)
        public List<string> DetectKeys { get; set; } = new List<string>();  // Detect: registry keys (HKCU\..., HKLM\...)
        public List<string> FileKeys { get; set; } = new List<string>(); // "Path|Pattern|Flags"
        public List<string> ExcludeKeys { get; set; } = new List<string>(); // "FILE|Path|Pattern" or "PATH|Path|Pattern"
        public bool IsDefault { get; set; }
        public bool IsExternal { get; set; } // Loaded from winapp2.ini (replaced wholesale on reload)
        public string? Warning { get; set; } // winapp2 "Warning=" — shown before cleaning
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
        // The rule author's caution (e.g. "This will also delete all app-specific settings").
        public string? Warning { get; set; }

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
        // winapp2's numeric section groups (the CCleaner categories).
        private static readonly Dictionary<string, string> LangSecRefCategories = new()
        {
            ["3021"] = "Applications",
            ["3022"] = "Internet",
            ["3023"] = "Multimedia",
            ["3024"] = "Utilities",
            ["3025"] = "Windows",
        };

        private readonly List<CleanRule> _rules = new List<CleanRule>();
        private readonly object _rulesLock = new object();
        private readonly AdvancedScanner _hasher = new AdvancedScanner();

        public Winapp2Parser() : this(includeBuiltInRules: true) { }

        // Tests use includeBuiltInRules: false so their own rule isn't pre-empted by the built-in
        // "Windows Temporary Files" rule (files are only listed once per scan).
        internal Winapp2Parser(bool includeBuiltInRules)
        {
            if (includeBuiltInRules) LoadBuiltInRules();
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
                    // Every profile ("Default", "Profile 1", ...), not just the first one.
                    "%LocalAppData%\\Google\\Chrome\\User Data\\*\\Cache|*.*|RECURSE",
                    "%LocalAppData%\\Google\\Chrome\\User Data\\*\\Code Cache\\js|*.*|RECURSE"
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
                    "%LocalAppData%\\Microsoft\\Edge\\User Data\\*\\Cache|*.*|RECURSE"
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
                    // Not %WinDir%\Logs\CBS: CBS.log is the record of system-file corruption and
                    // repairs that the Component Repair page reads — cleaning it hid the evidence.
                    "%LocalAppData%\\Microsoft\\Windows\\WER|*.*|RECURSE",
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
                        // Detect/DetectFile may be numbered (DetectFile1=, DetectFile2=...).
                        var match = Regex.Match(trimmed, @"^(DetectFile\d*|Detect\d*|FileKey\d+|ExcludeKey\d+|Warning|Section|LangSecRef)=(.*)$", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            string key = match.Groups[1].Value;
                            string val = match.Groups[2].Value.Trim();

                            if (key.StartsWith("DetectFile", StringComparison.OrdinalIgnoreCase))
                            {
                                currentRule.DetectPaths.Add(val);
                            }
                            else if (key.StartsWith("Detect", StringComparison.OrdinalIgnoreCase))
                            {
                                // Detect= names a REGISTRY key. It used to be checked as a folder
                                // path, so the ~1,000 rules that rely on it were never detected.
                                currentRule.DetectKeys.Add(val);
                            }
                            else if (key.Equals("Warning", StringComparison.OrdinalIgnoreCase))
                            {
                                currentRule.Warning = val;
                            }
                            else if (key.Equals("Section", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!string.IsNullOrEmpty(val)) currentRule.Category = val;
                            }
                            else if (key.Equals("LangSecRef", StringComparison.OrdinalIgnoreCase))
                            {
                                // A Section= line (more specific) wins over the generic group.
                                if (currentRule.Category == "Third-Party Apps" && LangSecRefCategories.TryGetValue(val, out var cat))
                                    currentRule.Category = cat;
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
                EngineLog.Warn($"[Winapp2] Failed to parse {filePath}: {ex.Message}");
            }
        }

        public string ExpandSpecialFolder(string path) => ExpandSpecialFolder(path, UserFolders.ForCurrentProcess());

        // The first expansion of a path (see ExpandTokens). Kept for single-path callers.
        public static string ExpandSpecialFolder(string path, UserFolders folders) =>
            ExpandTokens(path, folders).FirstOrDefault() ?? string.Empty;

        // Translates winapp2 tokens to real folders. Per-user tokens (%LocalAppData%, %Temp%, ...)
        // resolve to the REQUESTING user's folders - not the engine's, which would be SYSTEM's
        // profile when running as a service. %ProgramFiles% / %CommonProgramFiles% mean BOTH the
        // 64-bit and 32-bit folders (winapp2's convention), so a path can expand to two.
        // (Plain string replacement: a folder path containing '$' must not be treated as a regex
        // substitution.)
        public static List<string> ExpandTokens(string path, UserFolders folders)
        {
            if (string.IsNullOrEmpty(path)) return new List<string>();

            string R(string value, string token, string replacement) =>
                value.Replace(token, replacement, StringComparison.OrdinalIgnoreCase);

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            path = R(path, "%LocalLowAppData%", Path.Combine(folders.Profile, "AppData", "LocalLow"));
            path = R(path, "%LocalAppData%", folders.LocalAppData);
            path = R(path, "%AppData%", folders.RoamingAppData);
            path = R(path, "%UserProfile%", folders.Profile);
            path = R(path, "%UserName%", Path.GetFileName(folders.Profile));
            path = R(path, "%Documents%", folders.Documents);
            path = R(path, "%Pictures%", folders.Pictures);
            path = R(path, "%Music%", folders.Music);
            path = R(path, "%Video%", folders.Videos);
            path = R(path, "%Temp%", folders.Temp);
            path = R(path, "%Public%", Environment.GetEnvironmentVariable("PUBLIC") ?? Path.Combine(Path.GetDirectoryName(folders.Profile) ?? "C:\\Users", "Public"));
            path = R(path, "%CommonAppData%", programData);
            path = R(path, "%ProgramData%", programData);
            path = R(path, "%WinDir%", windows);
            path = R(path, "%SystemRoot%", windows);
            path = R(path, "%SystemDrive%", (Path.GetPathRoot(windows) ?? "C:\\").TrimEnd('\\'));

            var results = new List<string> { path };
            results = Fork(results, "%ProgramFiles%",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            results = Fork(results, "%CommonProgramFiles%",
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86));

            // Normalise doubled separators left by tokens that end in '\' ("%SystemDrive%\x").
            return results
                .Select(r => Regex.Replace(r, @"(?<!^)\\{2,}", "\\"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> Fork(List<string> paths, string token, params string[] replacements) =>
            paths.SelectMany(p => p.Contains(token, StringComparison.OrdinalIgnoreCase)
                    ? replacements.Where(r => !string.IsNullOrEmpty(r))
                                  .Select(r => p.Replace(token, r, StringComparison.OrdinalIgnoreCase))
                    : new[] { p })
                 .ToList();

        private static bool HasWildcard(string s) => s.IndexOfAny(new[] { '*', '?' }) >= 0;

        // Expands wildcard folder segments ("...\Chrome*\User Data\*\Cache") into the folders that
        // actually exist. Wildcard matches never include junctions/links. Outside the user's
        // profile (where cleaning runs with the engine's own rights) no link is followed at all,
        // so a junction planted in e.g. a ProgramData folder can't redirect a clean.
        internal static List<string> ExpandWildcardDirectories(string path, bool allowLinksInFixedSegments, int limit = 512)
        {
            var found = new List<string>();
            string? root;
            try { root = Path.GetPathRoot(path); } catch { return found; }
            if (string.IsNullOrEmpty(root)) return found;

            var segments = path.Substring(root.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var current = new List<string> { root };
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchType = MatchType.Win32,
                RecurseSubdirectories = false
            };

            foreach (var segment in segments)
            {
                var next = new List<string>();
                foreach (var dir in current)
                {
                    try
                    {
                        if (HasWildcard(segment))
                        {
                            next.AddRange(Directory.EnumerateDirectories(dir, segment, options));
                        }
                        else
                        {
                            string candidate = Path.Combine(dir, segment);
                            if (Directory.Exists(candidate) && (allowLinksInFixedSegments || !IsReparsePoint(candidate)))
                                next.Add(candidate);
                        }
                    }
                    catch
                    {
                        // Inaccessible — skip.
                    }
                    if (next.Count >= limit) break;
                }
                current = next;
                if (current.Count == 0) return found;
            }

            found.AddRange(current);
            return found;
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }

        // A rule applies when it has no detection entries, or when ANY of its Detect (registry)
        // or DetectFile (file/folder, wildcards allowed) entries is present.
        private static bool IsRuleDetected(CleanRule rule, ClientContext ctx)
        {
            if (rule.DetectPaths.Count == 0 && rule.DetectKeys.Count == 0) return true;

            foreach (var key in rule.DetectKeys)
            {
                if (IsRegistryKeyPresent(key, ctx)) return true;
            }

            foreach (var detectPath in rule.DetectPaths)
            {
                foreach (var expanded in ExpandTokens(detectPath, ctx.Folders))
                {
                    if (PathExists(expanded)) return true;
                }
            }

            return false;
        }

        private static bool PathExists(string path)
        {
            if (!HasWildcard(path))
                return Directory.Exists(path) || File.Exists(path);

            string trimmed = path.TrimEnd('\\');
            string? parent = Path.GetDirectoryName(trimmed);
            string leaf = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return false;

            foreach (var dir in ExpandWildcardDirectories(parent, allowLinksInFixedSegments: true, limit: 64))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(dir, leaf, new EnumerationOptions { IgnoreInaccessible = true, MatchType = MatchType.Win32 }).Any())
                        return true;
                }
                catch { }
            }
            return false;
        }

        // Detect=HKCU\... / HKLM\... (also the long forms and HKCR). HKCU is the requesting user's
        // own hive; HKLM is checked in both the 64-bit and 32-bit registry views.
        internal static bool IsRegistryKeyPresent(string keyPath, ClientContext ctx)
        {
            int slash = keyPath.IndexOf('\\');
            if (slash <= 0) return false;
            string hive = keyPath.Substring(0, slash).ToUpperInvariant();
            string sub = keyPath.Substring(slash + 1);

            try
            {
                switch (hive)
                {
                    case "HKCU":
                    case "HKEY_CURRENT_USER":
                        using (var userHive = ctx.OpenUserHive())
                        using (var k = userHive.Key?.OpenSubKey(sub))
                            return k != null;

                    case "HKLM":
                    case "HKEY_LOCAL_MACHINE":
                        return MachineKeyExists(sub);

                    case "HKCR":
                    case "HKEY_CLASSES_ROOT":
                        using (var userHive = ctx.OpenUserHive())
                        using (var k = userHive.Key?.OpenSubKey($@"Software\Classes\{sub}"))
                            if (k != null) return true;
                        return MachineKeyExists($@"SOFTWARE\Classes\{sub}");

                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool MachineKeyExists(string sub)
        {
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                using var k = baseKey.OpenSubKey(sub);
                if (k != null) return true;
            }
            return false;
        }

        // Folders REMOVESELF must never remove, even if a rule's folder resolves to one of them.
        private static HashSet<string> ProtectedRoots(UserFolders f)
        {
            var roots = new[]
            {
                f.Profile, f.LocalAppData, f.RoamingAppData, Path.Combine(f.Profile, "AppData"),
                Path.Combine(f.Profile, "AppData", "LocalLow"), f.Documents, f.Desktop, f.Downloads,
                f.Pictures, f.Music, f.Videos, f.Temp, f.OneDrive,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            };
            return new HashSet<string>(
                roots.Where(r => !string.IsNullOrEmpty(r)).Select(r => Path.TrimEndingDirectorySeparator(r)),
                StringComparer.OrdinalIgnoreCase);
        }

        public List<ScanItem> Scan(Action<string, int>? progressCallback = null) =>
            Scan(ClientContext.ForCurrentProcess(), progressCallback);

        // ct: cancels the scan (throws OperationCanceledException between rules).
        public List<ScanItem> Scan(ClientContext ctx, Action<string, int>? progressCallback = null, CancellationToken ct = default)
        {
            var results = new List<ScanItem>();
            int count = 0;
            var protectedRoots = ProtectedRoots(ctx.Folders);
            // Shared by every rule: a file claimed by an earlier rule (built-in rules come first)
            // isn't listed again, so overlapping rules don't double-count the space to free.
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Snapshot so a definitions sync running concurrently can't modify the list mid-scan.
            List<CleanRule> rules;
            lock (_rulesLock)
            {
                rules = new List<CleanRule>(_rules);
            }

            foreach (var rule in rules)
            {
                ct.ThrowIfCancellationRequested();
                progressCallback?.Invoke(rule.Name, (count * 100) / rules.Count);
                count++;

                if (!IsRuleDetected(rule, ctx)) continue;

                var item = new ScanItem
                {
                    RuleName = rule.Name,
                    Category = rule.Category,
                    Warning = rule.Warning
                };

                var excludes = rule.ExcludeKeys
                    .SelectMany(key => ParseExcludeKeys(key, ctx.Folders))
                    .ToList();
                var foldersToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                    string pattern = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "*";
                    string flag = parts.Length > 2 ? parts[2].Trim() : "";
                    // REMOVESELF: clean the folder's whole contents, then remove the folder itself.
                    bool removeSelf = flag.Equals("REMOVESELF", StringComparison.OrdinalIgnoreCase);
                    bool recurse = removeSelf || flag.Equals("RECURSE", StringComparison.OrdinalIgnoreCase);

                    foreach (var expandedPath in ExpandTokens(parts[0], ctx.Folders))
                    {
                        bool insideProfile = ctx.Folders.Contains(expandedPath);
                        foreach (var dir in ExpandWildcardDirectories(expandedPath, allowLinksInFixedSegments: insideProfile))
                        {
                            CollectFiles(dir, pattern, recurse, excludes, seenFiles, item);

                            if (removeSelf && !protectedRoots.Contains(Path.TrimEndingDirectorySeparator(dir)) && !IsNeverClean(dir))
                            {
                                foldersToRemove.Add(dir);
                                foreach (var sub in EnumerateSubdirectories(dir))
                                    if (!IsNeverClean(sub)) foldersToRemove.Add(sub);
                            }
                        }
                    }
                }

                // Folders go last and deepest first, so each is empty by the time it's removed.
                // (A folder that still holds an excluded or locked file simply stays.)
                foreach (var folder in foldersToRemove.OrderByDescending(f => f.Count(c => c == '\\')))
                    item.FilePaths.Add(folder);

                if (item.FilePaths.Count > 0 && (item.TotalSizeBytes > 0 || foldersToRemove.Count > 0 || item.FilePaths.Contains("RECYCLE_BIN")))
                {
                    results.Add(item);
                }
            }

            progressCallback?.Invoke("Scan Complete", 100);
            return results;
        }

        // Never follow junctions/symlinks while collecting; inaccessible subfolders are skipped
        // instead of aborting the whole key.
        private static readonly EnumerationOptions TopLevelFiles = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchType = MatchType.Win32
        };

        private static readonly EnumerationOptions RecursiveFiles = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchType = MatchType.Win32
        };

        private static void CollectFiles(string dir, string pattern, bool recurse, List<ExcludeRule> excludes,
            HashSet<string> seenFiles, ScanItem item)
        {
            // winapp2 allows several patterns in one key, separated by ';' (e.g. "*.log;*.tmp").
            foreach (var singlePattern in pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, singlePattern, recurse ? RecursiveFiles : TopLevelFiles))
                    {
                        if (IsNeverClean(file) || IsExcluded(file, excludes) || !seenFiles.Add(file)) continue;
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

        // Locations no rule may clean, whatever a (community) rule says: Windows' event logs (security
        // and troubleshooting history), the CBS servicing log (the Component Repair page's evidence),
        // the registry hive files and the component store.
        private static readonly string[] NeverCleanRoots = BuildNeverCleanRoots();

        private static string[] BuildNeverCleanRoots()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(windows)) return Array.Empty<string>();
            return new[]
            {
                Path.Combine(windows, "System32", "winevt"),
                Path.Combine(windows, "Logs", "CBS"),
                Path.Combine(windows, "System32", "config"),
                Path.Combine(windows, "WinSxS"),
            };
        }

        internal static bool IsNeverClean(string path)
        {
            foreach (var root in NeverCleanRoots)
            {
                if (path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static IEnumerable<string> EnumerateSubdirectories(string dir)
        {
            try
            {
                return Directory.EnumerateDirectories(dir, "*", RecursiveFiles).ToList();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        public long Clean(List<ScanItem> items, Action<string, int>? progressCallback = null) =>
            Clean(items, ClientContext.ForCurrentProcess(), progressCallback);

        // Who deletes what:
        //   * Files inside the requesting user's profile are deleted AS that user, so Windows itself
        //     guarantees nothing is removed that the user couldn't remove — a junction swapped into
        //     a cache folder between scan and clean can't borrow the engine's rights.
        //   * Anything outside the profile (Windows logs, Prefetch, Delivery Optimization) needs
        //     administrator rights and is only cleaned for administrator accounts.
        // ct: stops cleaning between files and returns what was freed so far (nothing is left
        // half-deleted — each file is either deleted or untouched).
        public long Clean(List<ScanItem> items, ClientContext ctx, Action<string, int>? progressCallback = null, CancellationToken ct = default)
        {
            long deletedBytes = 0;
            int count = 0;

            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
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
                    if (ct.IsCancellationRequested) break;
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

        // One exclusion per token expansion (%ProgramFiles% covers both Program Files folders).
        internal static List<ExcludeRule> ParseExcludeKeys(string excludeKey, UserFolders folders)
        {
            var parts = excludeKey.Split('|');
            if (parts.Length < 2) return new List<ExcludeRule>();
            return ExpandTokens(parts[1].Trim(), folders)
                .Select(expanded => ParseExcludeKey(string.Join('|', new[] { parts[0], expanded }.Concat(parts.Skip(2))), folders))
                .OfType<ExcludeRule>()
                .ToList();
        }

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

        // FileSystemName treats '\' as an ESCAPE character, so a folder path must have its
        // separators escaped before it can be used as a wildcard pattern.
        internal static bool MatchesPathPattern(string pattern, string path) =>
            System.IO.Enumeration.FileSystemName.MatchesWin32Expression(
                pattern.Replace("\\", "\\\\"), path, ignoreCase: true);

        private static IEnumerable<string> SelfAndAncestors(string dir)
        {
            for (string? d = dir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                yield return d;
        }

        internal static bool IsExcluded(string filePath, IReadOnlyList<ExcludeRule>? excludes)
        {
            if (excludes == null || excludes.Count == 0) return false;

            string fileDir = Path.GetDirectoryName(filePath) ?? string.Empty;
            string fileName = Path.GetFileName(filePath);

            foreach (var rule in excludes)
            {
                if (HasWildcard(rule.Directory))
                {
                    // Wildcard folder ("...\plugins\*\"): FILE matches that folder only; PATH also
                    // matches anything beneath it.
                    bool wildMatch = rule.IsPathRule
                        ? SelfAndAncestors(fileDir).Any(d => MatchesPathPattern(rule.Directory, d))
                        : MatchesPathPattern(rule.Directory, fileDir);
                    if (!wildMatch) continue;
                    if (rule.Pattern == null) return true;
                    foreach (var pattern in rule.Pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (System.IO.Enumeration.FileSystemName.MatchesWin32Expression(pattern, fileName, ignoreCase: true))
                            return true;
                    }
                    continue;
                }

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
