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
        public bool IsDefault { get; set; }
    }

    public class ScanItem : System.ComponentModel.INotifyPropertyChanged
    {
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
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        private readonly List<CleanRule> _rules = new List<CleanRule>();

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
                            _rules.Add(currentRule);
                        }

                        string name = trimmed.Substring(1, trimmed.Length - 2);
                        currentRule = new CleanRule
                        {
                            Name = name,
                            Category = "Third-Party Apps",
                            IsDefault = true
                        };
                    }
                    else if (currentRule != null)
                    {
                        var match = Regex.Match(trimmed, @"^(Detect|DetectFile|FileKey\d+)=(.*)$", RegexOptions.IgnoreCase);
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
                        }
                    }
                }

                if (currentRule != null && currentRule.FileKeys.Count > 0)
                {
                    _rules.Add(currentRule);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Winapp2] Failed to parse {filePath}: {ex.Message}");
            }
        }

        public string ExpandSpecialFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            // Perform manual translations of Winapp2.ini custom tokens to standard folders
            path = Regex.Replace(path, @"%LocalAppData%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), RegexOptions.IgnoreCase);
            path = Regex.Replace(path, @"%AppData%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), RegexOptions.IgnoreCase);
            path = Regex.Replace(path, @"%UserProfile%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), RegexOptions.IgnoreCase);
            path = Regex.Replace(path, @"%Documents%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), RegexOptions.IgnoreCase);
            path = Regex.Replace(path, @"%CommonAppData%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), RegexOptions.IgnoreCase);
            path = Regex.Replace(path, @"%WinDir%", Environment.GetFolderPath(Environment.SpecialFolder.Windows), RegexOptions.IgnoreCase);
            path = Regex.Replace(path, @"%SystemDrive%", Path.GetPathRoot(Environment.SystemDirectory), RegexOptions.IgnoreCase);
            path = Regex.Replace(path, @"%Temp%", Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), RegexOptions.IgnoreCase);

            return path;
        }

        private bool IsRuleDetected(CleanRule rule)
        {
            if (rule.DetectPaths.Count == 0) return true; // Always scan if no specific detection path is defined

            foreach (var detectPath in rule.DetectPaths)
            {
                string expanded = ExpandSpecialFolder(detectPath);
                if (Directory.Exists(expanded) || File.Exists(expanded))
                {
                    return true;
                }
            }

            return false;
        }

        public List<ScanItem> Scan(Action<string, int>? progressCallback = null)
        {
            var results = new List<ScanItem>();
            int count = 0;

            foreach (var rule in _rules)
            {
                progressCallback?.Invoke(rule.Name, (count * 100) / _rules.Count);
                count++;

                if (!IsRuleDetected(rule)) continue;

                var item = new ScanItem
                {
                    RuleName = rule.Name,
                    Category = rule.Category
                };

                foreach (var fileKey in rule.FileKeys)
                {
                    if (fileKey.StartsWith("RECYCLE_BIN", StringComparison.OrdinalIgnoreCase))
                    {
                        // We cannot list files in Recycle Bin easily, so we assign a placeholder size or query shell
                        item.FilePaths.Add("RECYCLE_BIN");
                        item.TotalSizeBytes += 1024 * 1024 * 50; // Estimate 50 MB if not empty
                        continue;
                    }

                    string[] parts = fileKey.Split('|');
                    if (parts.Length < 1) continue;

                    string expandedPath = ExpandSpecialFolder(parts[0]);
                    string pattern = parts.Length > 1 ? parts[1] : "*.*";
                    bool recurse = parts.Length > 2 && parts[2].Equals("RECURSE", StringComparison.OrdinalIgnoreCase);

                    if (!Directory.Exists(expandedPath)) continue;

                    try
                    {
                        var searchOption = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        var files = Directory.GetFiles(expandedPath, pattern, searchOption);
                        foreach (var file in files)
                        {
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

                if (item.FilePaths.Count > 0)
                {
                    results.Add(item);
                }
            }

            progressCallback?.Invoke("Scan Complete", 100);
            return results;
        }

        public long Clean(List<ScanItem> items, Action<string, int>? progressCallback = null)
        {
            long deletedBytes = 0;
            int count = 0;

            foreach (var item in items)
            {
                progressCallback?.Invoke($"Cleaning {item.RuleName}", (count * 100) / items.Count);
                count++;

                foreach (var path in item.FilePaths)
                {
                    if (path == "RECYCLE_BIN")
                    {
                        try
                        {
                            int result = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                            if (result == 0)
                            {
                                deletedBytes += item.TotalSizeBytes;
                            }
                        }
                        catch { }
                        continue;
                    }

                    if (Directory.Exists(path))
                    {
                        try
                        {
                            Directory.Delete(path, false); // only delete if empty
                        }
                        catch { }
                        continue;
                    }

                    if (!File.Exists(path)) continue;

                    try
                    {
                        var fi = new FileInfo(path);
                        long size = fi.Length;
                        File.Delete(path);
                        deletedBytes += size;
                    }
                    catch
                    {
                        // File locked or access denied, skip silently
                    }
                }
            }

            progressCallback?.Invoke("Clean Complete", 100);
            return deletedBytes;
        }
    }
}
