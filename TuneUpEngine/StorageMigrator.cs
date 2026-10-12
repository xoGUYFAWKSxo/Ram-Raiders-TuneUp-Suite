using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace TuneUpEngine
{
    public class MigrationRecord
    {
        public string OriginalPath { get; set; } = "";
        public string DestinationPath { get; set; } = "";
        public DateTime MigratedAt { get; set; }
        public long SizeBytes { get; set; }
        public bool SymlinkActive { get; set; }
    }

    public class MigrationAnalysis
    {
        public string SourcePath { get; set; } = "";
        public long SourceSizeBytes { get; set; }
        public bool SourceExists { get; set; }
        public bool IsAlreadySymlink { get; set; }
        public string? Error { get; set; }
    }

    public static class StorageMigrator
    {
        // (The CreateSymbolicLinkW P/Invoke was removed. Windows declares it as returning a 1-byte
        // BOOLEAN, but it was marshalled as a 4-byte BOOL, so the garbage upper bytes made a FAILED
        // link read as success — and the original folder was then deleted with no link left.)

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string lpFileName);

        private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

        // Machine-wide (migrations move machine folders such as game installs), next to the change
        // journal. The engine's own AppData would be SYSTEM's profile when running as a service.
        private static readonly string HistoryFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RamRaidersTuneUpSuite", "migration_history.json");

        // ── Path safety ─────────────────────────────────────────────────────────

        // Migration copies a folder and then recursively DELETES the original with administrator
        // rights, and both paths arrive over IPC. These checks stop it from being pointed at the
        // operating system, whole drives, or folders that contain other people's / the system's data.
        // Returns null when the source is acceptable, otherwise a human-readable reason.
        internal static string? ValidateSource(string? sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
                return "Source must be a full path (e.g. C:\\Games\\MyGame).";

            string source;
            try { source = Normalize(sourcePath); }
            catch { return "The source path is not valid."; }

            // Note: normalising "C:\" leaves it as "C:\" (the root keeps its separator), so compare
            // against the normalised root rather than a trimmed one.
            string? root = Path.GetPathRoot(source);
            if (root != null && Normalize(root).Equals(source, StringComparison.OrdinalIgnoreCase))
                return "A whole drive cannot be migrated.";

            foreach (var protectedDir in ProtectedFolders())
            {
                // The source may not BE a protected folder, nor contain one (e.g. C:\Users).
                if (IsSameOrInside(protectedDir, source))
                    return $"'{source}' is or contains a protected system folder and cannot be migrated.";
            }

            // Every user's profile root and AppData folders — not just the engine's own (which is
            // SYSTEM's when running as a service). Their subfolders, e.g. a game folder in
            // Documents, are fine.
            string? profileError = CheckProfileFolders(source);
            if (profileError != null) return profileError;

            foreach (var systemTree in SystemTrees())
            {
                // Nothing inside these trees may be moved at all.
                if (IsSameOrInside(source, systemTree))
                    return $"'{source}' is inside a protected system location and cannot be migrated.";
            }

            return null;
        }

        internal static string? ValidateDestination(string? sourcePath, string? destinationPath)
        {
            if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath))
                return "Destination must be a full path.";

            string destination, source;
            try
            {
                destination = Normalize(destinationPath);
                source = string.IsNullOrWhiteSpace(sourcePath) ? string.Empty : Normalize(sourcePath);
            }
            catch { return "The destination path is not valid."; }

            // Copying a folder into itself recurses forever; copying it onto its own parent would
            // then delete the copy along with the source.
            if (IsSameOrInside(destination, source) || IsSameOrInside(source, destination))
                return "Destination cannot be inside the source folder (or the other way round).";

            foreach (var systemTree in SystemTrees())
            {
                if (IsSameOrInside(destination, systemTree))
                    return $"'{destination}' is inside a protected system location.";
            }

            return null;
        }

        // .NET accepts characters Windows forbids in file names (e.g. '|', '?', '*'), so reject them
        // explicitly — wildcards in particular must never reach a recursive delete.
        private static readonly char[] ForbiddenPathChars = { '<', '>', '"', '|', '?', '*' };

        internal static string? CheckProfileFolders(string source)
        {
            string? usersDir = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (string.IsNullOrEmpty(usersDir) || !IsSameOrInside(source, usersDir)) return null;

            string[] parts = Path.GetRelativePath(Normalize(usersDir), source)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 1)
                return $"'{source}' is a user profile folder and cannot be migrated.";
            if (parts.Length <= 3 && parts[1].Equals("AppData", StringComparison.OrdinalIgnoreCase))
                return $"'{source}' is an AppData folder and cannot be migrated.";
            return null;
        }

        private static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            // Skip the root ("C:\" or "\\?\") when looking for forbidden characters.
            string afterRoot = full.Substring(Path.GetPathRoot(full)?.Length ?? 0);
            if (afterRoot.IndexOfAny(ForbiddenPathChars) >= 0 || full.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ArgumentException("Path contains characters that are not allowed.", nameof(path));
            return Path.TrimEndingDirectorySeparator(full);
        }

        // True when 'path' equals 'container' or lies somewhere beneath it.
        internal static bool IsSameOrInside(string path, string container)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(container)) return false;
            path = Normalize(path);
            container = Normalize(container);
            return path.Equals(container, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(container + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // Folders that must never be the source themselves, or be swallowed by a source.
        // Their subfolders (e.g. a game under Program Files, a folder in Documents) are fine.
        private static IEnumerable<string> ProtectedFolders()
        {
            var folders = new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData, // C:\ProgramData
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.Desktop,
            }.Select(Environment.GetFolderPath)
             .Append(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "") // C:\Users
             .Append(AppContext.BaseDirectory); // This app's own install folder

            return folders.Where(f => !string.IsNullOrEmpty(f)).Select(Normalize);
        }

        // Locations where nothing at all may be moved, or moved into.
        private static IEnumerable<string> SystemTrees()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var trees = new List<string>();
            if (!string.IsNullOrEmpty(windows))
            {
                trees.Add(windows);
                string root = Path.GetPathRoot(windows) ?? "C:\\";
                trees.Add(Path.Combine(root, "System Volume Information"));
                trees.Add(Path.Combine(root, "$Recycle.Bin"));
                trees.Add(Path.Combine(root, "Recovery"));
            }
            return trees.Select(Normalize);
        }

        // ── Analysis ────────────────────────────────────────────────────────────

        public static MigrationAnalysis Analyze(string sourcePath)
        {
            var result = new MigrationAnalysis { SourcePath = sourcePath };

            string? sourceError = ValidateSource(sourcePath);
            if (sourceError != null)
            {
                result.SourceExists = !string.IsNullOrWhiteSpace(sourcePath) && Directory.Exists(sourcePath);
                result.Error = sourceError;
                return result;
            }

            if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
            {
                result.SourceExists = false;
                result.Error = "Source directory does not exist.";
                return result;
            }

            result.SourceExists = true;

            // Check if already a symlink/reparse point
            uint attrs = GetFileAttributesW(sourcePath);
            result.IsAlreadySymlink = attrs != INVALID_FILE_ATTRIBUTES &&
                                      (attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0;

            if (result.IsAlreadySymlink)
            {
                result.Error = "Source is already a symbolic link — it was previously migrated.";
                return result;
            }

            // Calculate directory size
            try
            {
                result.SourceSizeBytes = CalculateDirectorySize(sourcePath);
            }
            catch (Exception ex)
            {
                result.Error = $"Could not calculate size: {ex.Message}";
            }

            return result;
        }

        // ── Migration ───────────────────────────────────────────────────────────

        public static string Migrate(string sourcePath, string destinationPath,
            Action<string, double> progressCallback, CancellationToken ct = default)
        {
            // Validate
            string? pathError = ValidateSource(sourcePath) ?? ValidateDestination(sourcePath, destinationPath);
            if (pathError != null)
                return $"Error: {pathError}";

            if (!Directory.Exists(sourcePath))
                return "Error: Source directory does not exist.";

            // A source that is itself a link was already migrated; "copying" it would duplicate the
            // target and then delete through the link.
            if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                return "Error: Source is already a symbolic link — it was previously migrated.";

            string? destParent = Path.GetDirectoryName(destinationPath);
            if (!Directory.Exists(destParent))
                return $"Error: Destination parent '{destParent}' does not exist.";

            if (Directory.Exists(destinationPath))
                return "Error: Destination already exists. Choose a different path.";

            long sourceSize = CalculateDirectorySize(sourcePath);

            // Check destination free space
            string destDrive = Path.GetPathRoot(destinationPath) ?? destinationPath;
            var driveInfo = new DriveInfo(destDrive);
            if (driveInfo.AvailableFreeSpace < sourceSize)
                return $"Error: Insufficient space on {destDrive}. Need {FormatBytes(sourceSize)}, have {FormatBytes(driveInfo.AvailableFreeSpace)}.";

            try
            {
                // Phase 1: Copy
                progressCallback?.Invoke("Copying files to destination...", 0);
                long bytesCopied = 0;
                CopyDirectory(sourcePath, destinationPath, ref bytesCopied, sourceSize, progressCallback, ct);

                if (ct.IsCancellationRequested)
                {
                    // Clean up partial destination
                    try { Directory.Delete(destinationPath, recursive: true); } catch { }
                    return "Migration cancelled by user.";
                }

                // Phase 2: Verify
                // The next phase irreversibly deletes the source, so verification must be exact.
                // An aggregate size check (the previous approach) can pass even when an individual
                // file was truncated or skipped — its shortfall hides inside a 1% tolerance — and
                // the original would then be destroyed. Instead, confirm every source file is present
                // at the destination with a matching length, and that the file counts agree exactly.
                progressCallback?.Invoke("Verifying destination integrity...", 90);
                var (verified, detail) = VerifyCopy(sourcePath, destinationPath);
                if (!verified)
                {
                    try { Directory.Delete(destinationPath, recursive: true); } catch { }
                    return $"Error: Verification failed ({detail}). Destination cleaned up; original left untouched.";
                }

                // Phase 3: Swap the original for a link — without ever leaving a half-deleted folder.
                // The old order deleted the source first and created the link after; a locked file
                // stopped the delete halfway, leaving a partial folder and no link. Now the original
                // is RENAMED aside (instant and atomic on the same drive), the link is created in its
                // place, and only then is the renamed original deleted. If the link can't be made,
                // the rename is reversed and nothing has changed.
                progressCallback?.Invoke("Switching to the new location...", 93);
                string parked = ParkedPath(sourcePath);
                Directory.Move(sourcePath, parked);

                string? linkError = TryCreateDirectoryLink(sourcePath, destinationPath);
                if (linkError != null)
                {
                    Directory.Move(parked, sourcePath);
                    try { Directory.Delete(destinationPath, recursive: true); } catch { }
                    return $"Error: Could not create the link ({linkError}). Nothing was changed.";
                }

                // Phase 4: Remove the original. The link already works; a file that can't be deleted
                // (e.g. in use) is reported, and the rest of the space is still freed.
                progressCallback?.Invoke("Removing the original copy...", 97);
                string? leftover = null;
                try
                {
                    Directory.Delete(parked, recursive: true);
                }
                catch (Exception ex)
                {
                    leftover = $" Some original files couldn't be removed ({ex.Message}); they are in '{parked}' and can be deleted later.";
                }

                // Phase 5: Record history
                progressCallback?.Invoke("Recording migration...", 99);
                SaveMigrationRecord(new MigrationRecord
                {
                    OriginalPath = sourcePath,
                    DestinationPath = destinationPath,
                    MigratedAt = DateTime.Now,
                    SizeBytes = sourceSize,
                    SymlinkActive = true
                });

                progressCallback?.Invoke("Migration complete!", 100);
                return $"Success: Migrated {FormatBytes(sourceSize)} to {destinationPath}. Symlink active at original path.{leftover}";
            }
            catch (OperationCanceledException)
            {
                try { Directory.Delete(destinationPath, recursive: true); } catch { }
                return "Migration cancelled.";
            }
            catch (Exception ex)
            {
                // Failed before the swap (e.g. a file couldn't be copied): the original is untouched,
                // so remove the partial copy rather than leaving it behind.
                if (Directory.Exists(sourcePath) && (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) == 0)
                {
                    try { Directory.Delete(destinationPath, recursive: true); } catch { }
                }
                return $"Error during migration: {ex.Message}";
            }
        }

        // Creates a directory symbolic link and then CHECKS it really exists and points at the target
        // before anything is deleted. Returns null on success, otherwise the reason.
        // Creating links needs administrator rights (the engine has them) or Developer Mode.
        internal static string? TryCreateDirectoryLink(string linkPath, string targetPath)
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            var link = new DirectoryInfo(linkPath);
            bool ok = link.Exists &&
                      (link.Attributes & FileAttributes.ReparsePoint) != 0 &&
                      link.LinkTarget != null &&
                      Path.GetFullPath(link.LinkTarget).TrimEnd('\\').Equals(
                          Path.GetFullPath(targetPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (ok) return null;

            try { if (link.Exists) Directory.Delete(linkPath); } catch { }
            return "the link could not be verified";
        }

        // Where the original is parked during the swap: a sibling on the same drive.
        private static string ParkedPath(string sourcePath)
        {
            string trimmed = Path.TrimEndingDirectorySeparator(sourcePath);
            return $"{trimmed}.rrtu-original-{DateTime.Now:yyyyMMddHHmmss}";
        }

        // ── Undo ────────────────────────────────────────────────────────────────

        // Moves a migrated folder back to its original location and removes the link. Same safety
        // rules as migrating: the files are copied back and verified BEFORE the link is replaced,
        // and the moved copy is only deleted once the folder is back in place.
        // ct cancels only while copying back (before anything is replaced).
        public static string UndoMigration(string originalPath, Action<string, double> progressCallback, CancellationToken ct = default)
        {
            var record = GetHistory().FirstOrDefault(r =>
                r.OriginalPath.Equals(originalPath, StringComparison.OrdinalIgnoreCase));
            if (record == null)
                return "Error: That folder isn't in the migration history.";

            string? pathError = ValidateSource(record.OriginalPath);
            if (pathError != null)
                return $"Error: {pathError}";

            var link = new DirectoryInfo(record.OriginalPath);
            if (!link.Exists || (link.Attributes & FileAttributes.ReparsePoint) == 0)
                return "Error: The original location is no longer a link — it may already have been restored.";

            string? target = link.LinkTarget;
            if (target == null || !Path.GetFullPath(target).TrimEnd('\\').Equals(
                    Path.GetFullPath(record.DestinationPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return "Error: The link no longer points to the migrated folder, so it was left alone.";

            if (!Directory.Exists(record.DestinationPath))
                return $"Error: The migrated folder '{record.DestinationPath}' no longer exists.";

            long size = CalculateDirectorySize(record.DestinationPath);
            string? root = Path.GetPathRoot(record.OriginalPath);
            if (root != null && new DriveInfo(root).AvailableFreeSpace < size)
                return $"Error: Not enough space on {root} to move {FormatBytes(size)} back.";

            string restoring = $"{Path.TrimEndingDirectorySeparator(record.OriginalPath)}.rrtu-restoring";
            try
            {
                if (Directory.Exists(restoring)) Directory.Delete(restoring, recursive: true);

                progressCallback?.Invoke("Copying files back...", 0);
                long copied = 0;
                CopyDirectory(record.DestinationPath, restoring, ref copied, size, progressCallback, ct);

                progressCallback?.Invoke("Verifying the copy...", 90);
                var (verified, detail) = VerifyCopy(record.DestinationPath, restoring);
                if (!verified)
                {
                    try { Directory.Delete(restoring, recursive: true); } catch { }
                    return $"Error: Verification failed ({detail}). Nothing was changed.";
                }

                // Replace the link with the restored folder. Deleting a directory link removes only
                // the link, never the folder it points to.
                progressCallback?.Invoke("Putting the folder back...", 94);
                Directory.Delete(record.OriginalPath);
                Directory.Move(restoring, record.OriginalPath);

                progressCallback?.Invoke("Removing the migrated copy...", 97);
                string? leftover = null;
                try { Directory.Delete(record.DestinationPath, recursive: true); }
                catch (Exception ex) { leftover = $" The copy at '{record.DestinationPath}' couldn't be fully removed ({ex.Message})."; }

                RemoveMigrationRecord(record.OriginalPath);
                progressCallback?.Invoke("Restore complete!", 100);
                return $"Success: Restored {FormatBytes(size)} to {record.OriginalPath}.{leftover}";
            }
            catch (Exception ex)
            {
                // If the link is still in place the folder is still usable at its old path.
                if (Directory.Exists(restoring) && Directory.Exists(record.OriginalPath))
                {
                    try { Directory.Delete(restoring, recursive: true); } catch { }
                }
                return $"Error during restore: {ex.Message}";
            }
        }

        private static void RemoveMigrationRecord(string originalPath)
        {
            try
            {
                var records = GetHistory();
                records.RemoveAll(r => r.OriginalPath.Equals(originalPath, StringComparison.OrdinalIgnoreCase));
                File.WriteAllText(HistoryFile, JsonSerializer.Serialize(records,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ── History ─────────────────────────────────────────────────────────────

        public static List<MigrationRecord> GetHistory()
        {
            try
            {
                if (!File.Exists(HistoryFile)) return new List<MigrationRecord>();
                string json = File.ReadAllText(HistoryFile);
                var records = JsonSerializer.Deserialize<List<MigrationRecord>>(json) ?? new();

                // Refresh symlink active state
                foreach (var r in records)
                {
                    uint attrs = GetFileAttributesW(r.OriginalPath);
                    r.SymlinkActive = attrs != INVALID_FILE_ATTRIBUTES &&
                                      (attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                }
                return records;
            }
            catch { return new List<MigrationRecord>(); }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static void CopyDirectory(string src, string dst, ref long bytesCopied,
            long totalBytes, Action<string, double>? progress, CancellationToken ct)
        {
            Directory.CreateDirectory(dst);

            foreach (var file in Directory.EnumerateFiles(src))
            {
                ct.ThrowIfCancellationRequested();
                string destFile = Path.Combine(dst, Path.GetFileName(file));
                var info = new FileInfo(file);
                File.Copy(file, destFile, overwrite: true);
                bytesCopied += info.Length;
                double pct = totalBytes > 0 ? (double)bytesCopied / totalBytes * 88 : 0; // 0-88%
                progress?.Invoke($"Copying {Path.GetFileName(file)}...", pct);
            }

            foreach (var dir in Directory.EnumerateDirectories(src))
            {
                string destDir = Path.Combine(dst, Path.GetFileName(dir));
                CopyDirectory(dir, destDir, ref bytesCopied, totalBytes, progress, ct);
            }
        }

        // Confirms the destination is a faithful copy of the source before the source is deleted.
        // Every source file must exist at the destination with an identical length, and the total
        // file counts must match exactly (so the destination has no missing — or extra — files).
        // Returns ok=false with a human-readable reason on the first discrepancy found.
        private static (bool ok, string detail) VerifyCopy(string src, string dst)
        {
            int srcCount = 0;
            foreach (var srcFile in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
            {
                srcCount++;
                string rel = Path.GetRelativePath(src, srcFile);
                string dstFile = Path.Combine(dst, rel);

                if (!File.Exists(dstFile))
                    return (false, $"missing at destination: {rel}");

                long srcLen, dstLen;
                try
                {
                    srcLen = new FileInfo(srcFile).Length;
                    dstLen = new FileInfo(dstFile).Length;
                }
                catch (Exception ex)
                {
                    return (false, $"could not read '{rel}': {ex.Message}");
                }

                if (srcLen != dstLen)
                    return (false, $"size mismatch for '{rel}': source {FormatBytes(srcLen)}, dest {FormatBytes(dstLen)}");
            }

            int dstCount = Directory.EnumerateFiles(dst, "*.*", SearchOption.AllDirectories).Count();
            if (dstCount != srcCount)
                return (false, $"file count mismatch: source {srcCount}, destination {dstCount}");

            return (true, $"{srcCount} files verified");
        }

        private static long CalculateDirectorySize(string path)
        {
            long size = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return size;
        }

        private static void SaveMigrationRecord(MigrationRecord record)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
                var records = GetHistory();
                records.RemoveAll(r => r.OriginalPath.Equals(record.OriginalPath,
                    StringComparison.OrdinalIgnoreCase));
                records.Insert(0, record);
                File.WriteAllText(HistoryFile, JsonSerializer.Serialize(records,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
