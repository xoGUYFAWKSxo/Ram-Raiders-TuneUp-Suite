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
        public string OriginalPath { get; set; }
        public string DestinationPath { get; set; }
        public DateTime MigratedAt { get; set; }
        public long SizeBytes { get; set; }
        public bool SymlinkActive { get; set; }
    }

    public class MigrationAnalysis
    {
        public string SourcePath { get; set; }
        public long SourceSizeBytes { get; set; }
        public bool SourceExists { get; set; }
        public bool IsAlreadySymlink { get; set; }
        public string Error { get; set; }
    }

    public static class StorageMigrator
    {
        private const uint SYMBOLIC_LINK_FLAG_DIRECTORY = 0x1;
        private const uint SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE = 0x2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLinkW(
            string lpSymlinkFileName,
            string lpTargetFileName,
            uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string lpFileName);

        private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

        private static readonly string HistoryFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TuneUpPremiumSuite", "migration_history.json");

        // ── Analysis ────────────────────────────────────────────────────────────

        public static MigrationAnalysis Analyze(string sourcePath)
        {
            var result = new MigrationAnalysis { SourcePath = sourcePath };

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
            if (!Directory.Exists(sourcePath))
                return "Error: Source directory does not exist.";

            string destParent = Path.GetDirectoryName(destinationPath);
            if (!Directory.Exists(destParent))
                return $"Error: Destination parent '{destParent}' does not exist.";

            if (Directory.Exists(destinationPath))
                return "Error: Destination already exists. Choose a different path.";

            long sourceSize = CalculateDirectorySize(sourcePath);

            // Check destination free space
            string destDrive = Path.GetPathRoot(destinationPath);
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
                progressCallback?.Invoke("Verifying destination integrity...", 90);
                long destSize = CalculateDirectorySize(destinationPath);
                if (destSize < sourceSize * 0.99) // Allow 1% tolerance for metadata
                {
                    try { Directory.Delete(destinationPath, recursive: true); } catch { }
                    return $"Error: Verification failed. Source={FormatBytes(sourceSize)}, Dest={FormatBytes(destSize)}. Destination cleaned up.";
                }

                // Phase 3: Delete source
                progressCallback?.Invoke("Removing original directory...", 93);
                Directory.Delete(sourcePath, recursive: true);

                // Phase 4: Create symlink
                progressCallback?.Invoke("Creating NTFS symbolic link...", 97);
                bool symlinkCreated = CreateSymbolicLinkW(sourcePath, destinationPath,
                    SYMBOLIC_LINK_FLAG_DIRECTORY);

                if (!symlinkCreated)
                {
                    int err = Marshal.GetLastWin32Error();
                    return $"Error: Symlink creation failed (Win32 error {err}). Files are safe at: {destinationPath}";
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
                return $"Success: Migrated {FormatBytes(sourceSize)} to {destinationPath}. Symlink active at original path.";
            }
            catch (OperationCanceledException)
            {
                try { Directory.Delete(destinationPath, recursive: true); } catch { }
                return "Migration cancelled.";
            }
            catch (Exception ex)
            {
                return $"Error during migration: {ex.Message}";
            }
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
            long totalBytes, Action<string, double> progress, CancellationToken ct)
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
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile));
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
