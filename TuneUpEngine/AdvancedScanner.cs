using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TuneUpEngine
{
    public class AdvancedScanner
    {
        private static string[] GetUserFolders()
        {
            // Use USERPROFILE env var so we always resolve to the real logged-in user's
            // home directory, not the SYSTEM account's profile when running elevated.
            string userProfile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            return new[]
            {
                userProfile,
                Path.Combine(userProfile, "Documents"),
                Path.Combine(userProfile, "Pictures"),
                Path.Combine(userProfile, "Videos"),
                Path.Combine(userProfile, "Music"),
                Path.Combine(userProfile, "Desktop"),
                Path.Combine(userProfile, "Downloads"),
                Path.Combine(userProfile, "OneDrive") // Common on modern Windows
            }.Where(p => !string.IsNullOrEmpty(p))
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToArray();
        }

        public List<ScanItem> ScanEmptyFolders(Action<string> progressCallback)
        {
            var results = new List<ScanItem>();
            var emptyFolders = new List<string>();
            var userFolders = GetUserFolders();

            foreach (var folder in userFolders)
            {
                if (Directory.Exists(folder))
                {
                    progressCallback?.Invoke($"Scanning for empty folders in {Path.GetFileName(folder)}...");
                    FindEmptyFolders(folder, emptyFolders, userFolders);
                }
            }

            if (emptyFolders.Count > 0)
            {
                results.Add(new ScanItem
                {
                    Category = "Empty Folders",
                    RuleName = "User Empty Directories",
                    FilePaths = emptyFolders,
                    TotalSizeBytes = 0
                });
            }

            return results;
        }

        private bool FindEmptyFolders(string currentDir, List<string> emptyFolders, string[] userFolders)
        {
            try
            {
                bool isEmpty = true;
                foreach (var dir in Directory.GetDirectories(currentDir))
                {
                    bool subIsEmpty = FindEmptyFolders(dir, emptyFolders, userFolders);
                    if (!subIsEmpty) isEmpty = false;
                }

                if (Directory.GetFiles(currentDir).Length > 0)
                {
                    isEmpty = false;
                }

                // Never flag the root user folders themselves as empty, only subfolders
                if (isEmpty && !userFolders.Contains(currentDir, StringComparer.OrdinalIgnoreCase))
                {
                    emptyFolders.Add(currentDir);
                }

                return isEmpty;
            }
            catch
            {
                // Access denied, treat as not empty to be safe
                return false;
            }
        }

        public List<ScanItem> ScanDuplicateFiles(Action<string> progressCallback)
        {
            var results = new List<ScanItem>();
            var filesBySize = new Dictionary<long, List<string>>();
            var userFolders = GetUserFolders();

            foreach (var folder in userFolders)
            {
                if (!Directory.Exists(folder)) continue;
                progressCallback?.Invoke($"Indexing files in {Path.GetFileName(folder)}...");
                
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if (fi.Length == 0) continue; // Ignore 0 byte files

                            if (!filesBySize.ContainsKey(fi.Length))
                                filesBySize[fi.Length] = new List<string>();
                            
                            filesBySize[fi.Length].Add(file);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Only process sizes with > 1 file
            var potentialDuplicates = filesBySize.Where(kvp => kvp.Value.Count > 1).ToList();
            int current = 0;
            
            foreach (var group in potentialDuplicates)
            {
                current++;
                if (current % 10 == 0)
                    progressCallback?.Invoke($"Hashing potential duplicates... {current}/{potentialDuplicates.Count}");

                var hashGroups = new Dictionary<string, List<string>>();

                foreach (var file in group.Value)
                {
                    string hash = GetFastFileHash(file);
                    if (hash == null) continue;

                    if (!hashGroups.ContainsKey(hash))
                        hashGroups[hash] = new List<string>();
                    
                    hashGroups[hash].Add(file);
                }

                int dupSetIndex = 1;
                foreach (var hGroup in hashGroups.Where(kvp => kvp.Value.Count > 1))
                {
                    results.Add(new ScanItem
                    {
                        Category = "Duplicate Files",
                        RuleName = $"Duplicate Set {current}-{dupSetIndex} ({FormatSize(group.Key)})",
                        FilePaths = hGroup.Value,
                        TotalSizeBytes = group.Key * (hGroup.Value.Count - 1) // Size that can be freed
                    });
                    dupSetIndex++;
                }
            }

            return results;
        }

        private string GetFastFileHash(string path)
        {
            try
            {
                // Hash first 1MB for speed
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    using (var md5 = MD5.Create())
                    {
                        byte[] hash = md5.ComputeHash(buffer, 0, bytesRead);
                        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} Bytes";
        }
    }
}
