using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TuneUpEngine
{
    public class AdvancedScanner
    {
        public const string DuplicateFilesCategory = "Duplicate Files";

        // The requesting user's personal folders - resolved from THEIR profile (see UserFolders), so a
        // service running as SYSTEM scans the right person's files. The profile root itself is
        // deliberately NOT a scan root: it contains every folder below (so each file was once
        // indexed twice and reported as a "duplicate" of itself) and it also contains AppData,
        // where empty folders and identical files belong to installed applications.
        private static string[] GetUserFolders(UserFolders folders)
        {
            return new[]
            {
                folders.Documents,
                folders.Pictures,
                folders.Videos,
                folders.Music,
                folders.Desktop,
                folders.Downloads,
                folders.OneDrive
            }.Where(p => !string.IsNullOrEmpty(p))
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToArray();
        }

        // Drops any root that lives inside another root, so no directory tree is walked twice.
        internal static string[] RemoveNestedRoots(IEnumerable<string> roots)
        {
            var normalized = roots
                .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return normalized
                .Where(r => !normalized.Any(other =>
                    !other.Equals(r, StringComparison.OrdinalIgnoreCase) &&
                    r.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }

        // Cloud placeholders (OneDrive Files On-Demand and similar) report their full size but their
        // content lives online. Reading them to hash would silently download the user's whole cloud
        // drive, so they are excluded from duplicate detection.
        private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
        private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

        internal static bool IsCloudPlaceholder(FileAttributes attributes) =>
            (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

        public List<ScanItem> ScanEmptyFolders(Action<string> progressCallback) =>
            ScanEmptyFolders(UserFolders.ForCurrentProcess(), progressCallback);

        public List<ScanItem> ScanEmptyFolders(UserFolders folders, Action<string> progressCallback, CancellationToken ct = default)
        {
            var results = new List<ScanItem>();
            var emptyFolders = new List<string>();
            var userFolders = GetUserFolders(folders);

            foreach (var folder in RemoveNestedRoots(userFolders))
            {
                if (Directory.Exists(folder))
                {
                    progressCallback?.Invoke($"Scanning for empty folders in {Path.GetFileName(folder)}...");
                    ct.ThrowIfCancellationRequested();
                    FindEmptyFolders(folder, emptyFolders, userFolders, ct);
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

        // True if the directory is a reparse point (symlink/junction). We never descend into or flag
        // these: doing so risks infinite loops and touches content that physically lives elsewhere
        // (for example a folder the Storage Migrator moved to another drive and symlinked back).
        private static bool IsReparsePoint(string dir)
        {
            try { return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }

        // Manual recursive file walk that skips reparse-point directories (Directory.EnumerateFiles
        // with AllDirectories follows them, which is what we must avoid).
        private static IEnumerable<string> EnumerateFilesSafe(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();

                string[]? files = null;
                try { files = Directory.GetFiles(dir); } catch { }
                if (files != null)
                    foreach (var f in files) yield return f;

                string[]? subdirs = null;
                try { subdirs = Directory.GetDirectories(dir); } catch { }
                if (subdirs != null)
                    foreach (var d in subdirs)
                        if (!IsReparsePoint(d)) stack.Push(d);
            }
        }

        private bool FindEmptyFolders(string currentDir, List<string> emptyFolders, string[] userFolders, CancellationToken ct)
        {
            // Checked at every folder (outside the try below, so it isn't swallowed).
            ct.ThrowIfCancellationRequested();
            try
            {
                bool isEmpty = true;
                foreach (var dir in Directory.GetDirectories(currentDir))
                {
                    // A symlinked/junctioned folder is not "empty" and must not be recursed into.
                    if (IsReparsePoint(dir)) { isEmpty = false; continue; }

                    bool subIsEmpty = FindEmptyFolders(dir, emptyFolders, userFolders, ct);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Access denied, treat as not empty to be safe
                return false;
            }
        }

        public List<ScanItem> ScanDuplicateFiles(Action<string> progressCallback) =>
            ScanDuplicateFiles(UserFolders.ForCurrentProcess(), progressCallback);

        public List<ScanItem> ScanDuplicateFiles(UserFolders folders, Action<string> progressCallback, CancellationToken ct = default)
        {
            var results = new List<ScanItem>();
            var filesBySize = new Dictionary<long, List<string>>();
            // Belt and braces alongside RemoveNestedRoots: a path must never be indexed twice, or it
            // would be reported as a duplicate of itself and cleaning it would delete the only copy.
            var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in RemoveNestedRoots(GetUserFolders(folders)))
            {
                if (!Directory.Exists(folder)) continue;
                progressCallback?.Invoke($"Indexing files in {Path.GetFileName(folder)}...");

                try
                {
                    foreach (var file in EnumerateFilesSafe(folder))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (!indexedPaths.Add(Path.GetFullPath(file))) continue;

                            var fi = new FileInfo(file);
                            if (fi.Length == 0) continue; // Ignore 0 byte files
                            if (IsCloudPlaceholder(fi.Attributes)) continue;

                            if (!filesBySize.ContainsKey(fi.Length))
                                filesBySize[fi.Length] = new List<string>();
                            
                            filesBySize[fi.Length].Add(file);
                        }
                        catch { }
                    }
                }
                // Skip unreadable folders, but never swallow a cancellation.
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }

            // Only process sizes with > 1 file
            var potentialDuplicates = filesBySize.Where(kvp => kvp.Value.Count > 1).ToList();
            int current = 0;

            // Files reported here can be deleted by the user, so they must be TRUE byte-for-byte
            // duplicates — not just files that happen to share a size or a common header. We confirm
            // identity in three escalating stages so the expensive work only happens when needed:
            //   1. Same size            (already grouped above — cheapest, no I/O)
            //   2. Same first-1MB hash  (one short read — weeds out files with different headers)
            //   3. Same full-file hash  (full read — proves the entire contents are identical)
            // A pair is only reported when it matches at every stage.
            foreach (var group in potentialDuplicates)
            {
                ct.ThrowIfCancellationRequested();
                current++;
                if (current % 10 == 0)
                    progressCallback?.Invoke($"Hashing potential duplicates... {current}/{potentialDuplicates.Count}");

                int dupSetIndex = 1;
                void EmitSet(List<string> files)
                {
                    results.Add(new ScanItem
                    {
                        Category = DuplicateFilesCategory,
                        RuleName = $"Duplicate Set {current}-{dupSetIndex} ({FormatSize(group.Key)})",
                        FilePaths = files,
                        TotalSizeBytes = group.Key * (files.Count - 1) // Size that can be freed
                    });
                    dupSetIndex++;
                }

                // Stage 2: pre-filter on the first 1 MB.
                var partialGroups = new Dictionary<string, List<string>>();
                foreach (var file in group.Value)
                {
                    string? ph = GetPartialHash(file);
                    if (ph == null) continue;
                    if (!partialGroups.TryGetValue(ph, out var list))
                        partialGroups[ph] = list = new List<string>();
                    list.Add(file);
                }

                // If the files are no larger than the pre-filter window, the first-1MB hash already
                // covers the whole file, so a stage-2 match is already a confirmed duplicate.
                bool partialCoversWholeFile = group.Key <= 1024 * 1024;

                foreach (var partialGroup in partialGroups.Where(kvp => kvp.Value.Count > 1))
                {
                    if (partialCoversWholeFile)
                    {
                        EmitSet(partialGroup.Value);
                        continue;
                    }

                    // Stage 3: confirm true byte-for-byte identity with a full-file hash. Only files
                    // that already match on both size and first-1MB reach this expensive read.
                    var fullGroups = new Dictionary<string, List<string>>();
                    foreach (var file in partialGroup.Value)
                    {
                        string? fh = GetFullHash(file);
                        if (fh == null) continue;
                        if (!fullGroups.TryGetValue(fh, out var list))
                            fullGroups[fh] = list = new List<string>();
                        list.Add(file);
                    }

                    foreach (var confirmed in fullGroups.Where(kvp => kvp.Value.Count > 1))
                        EmitSet(confirmed.Value);
                }
            }

            return results;
        }

        // Pre-filter hash: SHA-256 of the first 1 MB. A cheap way to separate files that merely
        // share a size before committing to a full read. Files that differ here cannot be duplicates.
        internal string? GetPartialHash(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buffer = new byte[1024 * 1024];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                using var sha = SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(buffer, 0, bytesRead));
            }
            catch
            {
                return null;
            }
        }

        // Confirmation hash: SHA-256 over the entire file. Two files of identical size whose full-file
        // hashes match are treated as byte-for-byte duplicates. SHA-256 (vs. the old first-1MB MD5)
        // removes both the header-collision false positives and the weak-hash concern.
        internal string? GetFullHash(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sha = SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(stream));
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
