using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace TuneUpEngine
{
    // Records the prior MSISupported state of a single device so a Force-MSI run can be undone.
    public class MsiBackupEntry
    {
        // Full registry path (under HKLM) to the device's MessageSignaledInterruptProperties key.
        public string MsiPropertiesKeyPath { get; set; }
        public string DeviceDesc { get; set; }
        public bool HadValue { get; set; }      // whether MSISupported already existed before our change
        public int PreviousValue { get; set; }  // its prior value, when HadValue is true
    }

    public class HardwareController
    {
        private const string PciRoot = @"SYSTEM\CurrentControlSet\Enum\PCI";

        // Timestamped MSI backups live alongside the Registry Guard backups, so every reversible
        // change this suite makes ends up in one predictable place.
        // Machine-wide settings, so machine-wide backups (ProgramData). Older versions wrote them to
        // the engine account's AppData, which is still checked when reverting.
        private static string BackupDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RamRaidersTuneUpSuite", "RegistryBackups");

        private static string LegacyBackupDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RamRaidersTuneUpSuite", "RegistryBackups");

        public static string CheckMsiCompliance()
        {
            var compliantDevices = new List<string>();
            try
            {
                using (var pciKey = Registry.LocalMachine.OpenSubKey(PciRoot))
                {
                    if (pciKey == null) return "Error: Could not read PCI registry.";

                    foreach (var devId in pciKey.GetSubKeyNames())
                    {
                        using (var devKey = pciKey.OpenSubKey(devId))
                        {
                            if (devKey == null) continue;
                            foreach (var instId in devKey.GetSubKeyNames())
                            {
                                using (var instKey = devKey.OpenSubKey(instId))
                                {
                                    if (instKey == null) continue;

                                    string className = instKey.GetValue("Class") as string;
                                    string devDesc = instKey.GetValue("DeviceDesc") as string;

                                    if (className == "Display" || className == "Net")
                                    {
                                        // Found a relevant device. Check if it has Interrupt Management
                                        using (var paramKey = instKey.OpenSubKey("Device Parameters"))
                                        {
                                            if (paramKey != null)
                                            {
                                                using (var imKey = paramKey.OpenSubKey("Interrupt Management"))
                                                {
                                                    if (imKey != null)
                                                    {
                                                        // It has Interrupt Management. Likely supports MSI.
                                                        // Filter out very old known bad drivers here if needed.
                                                        compliantDevices.Add(devDesc != null ? devDesc.Split(';')[^1] : devId);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (compliantDevices.Count > 0)
                {
                    return "Compliant: " + string.Join(", ", compliantDevices);
                }
                return "No compliant PCIe Display or Network devices found.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        internal const string JournalOwner = "msi";

        public static string EnableMSI(ChangeJournal journal)
        {
            int updatedCount = 0;
            int skipped = 0;
            var backup = new List<MsiBackupEntry>();
            try
            {
                using (var pciKey = Registry.LocalMachine.OpenSubKey(PciRoot, true))
                {
                    if (pciKey == null) return "Error: Could not read PCI registry.";

                    foreach (var devId in pciKey.GetSubKeyNames())
                    {
                        using var devKey = pciKey.OpenSubKey(devId, true);
                        if (devKey == null) continue;

                        foreach (var instId in devKey.GetSubKeyNames())
                        {
                            using var instKey = devKey.OpenSubKey(instId, true);
                            if (instKey == null) continue;

                            string className = instKey.GetValue("Class") as string;
                            if (className != "Display" && className != "Net") continue;

                            string devDesc = instKey.GetValue("DeviceDesc") as string ?? instId;

                            // GATE: only touch devices that already expose an Interrupt Management node —
                            // the exact same heuristic CheckMsiCompliance() uses to report a device as
                            // "compliant". The previous code created this node from scratch for EVERY
                            // Display/Net device and wrote MSISupported unconditionally, which is what
                            // risked unbootable systems / black screens on hardware that can't do MSI.
                            using var paramKey = instKey.OpenSubKey("Device Parameters", writable: true);
                            if (paramKey == null) { skipped++; continue; }

                            using var imKey = paramKey.OpenSubKey("Interrupt Management", writable: true);
                            if (imKey == null) { skipped++; continue; }

                            string msiKeyPath =
                                $@"{PciRoot}\{devId}\{instId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

                            // Record the true original in the change journal BEFORE writing. The
                            // journal keeps the first original it sees, so running Enable twice no
                            // longer makes "revert" restore the already-enabled value.
                            if (!journal.CaptureRegistryValue(JournalOwner, RegistryHive.LocalMachine, msiKeyPath, "MSISupported"))
                            {
                                skipped++;
                                continue;
                            }

                            using var msiKey = imKey.CreateSubKey("MessageSignaledInterruptProperties");
                            if (msiKey == null) { skipped++; continue; }

                            // Also keep the human-readable backup file the user can inspect.
                            object existing = msiKey.GetValue("MSISupported");
                            backup.Add(new MsiBackupEntry
                            {
                                MsiPropertiesKeyPath = msiKeyPath,
                                DeviceDesc = devDesc.Contains(';') ? devDesc.Split(';')[^1] : devDesc,
                                HadValue = existing != null,
                                PreviousValue = existing is int v ? v : 0
                            });

                            msiKey.SetValue("MSISupported", 1, RegistryValueKind.DWord);
                            updatedCount++;
                        }
                    }
                }

                if (updatedCount == 0)
                {
                    return $"No compliant Display/Network devices required changes " +
                           $"({skipped} device(s) skipped — no MSI support node present).";
                }

                string backupPath = SaveBackup(backup);
                return $"Success: Enabled MSI for {updatedCount} compliant device(s) " +
                       $"({skipped} skipped). Backup saved as {Path.GetFileName(backupPath)}. Reboot required.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // Restores MSISupported to its pre-tweak state using the most recent backup: the value is
        // either set back to what it was, or removed entirely if we were the ones who created it.
        public static string RevertMSI(ChangeJournal journal)
        {
            try
            {
                if (journal.IsOwned(JournalOwner))
                {
                    int failed = journal.Release(JournalOwner);
                    return failed == 0
                        ? "Success: Restored every device's original MSI setting. Reboot required."
                        : $"Error: {failed} device setting(s) could not be restored yet; they will be retried on the next engine start.";
                }

                // Enabled by a version without the change journal: fall back to the backup files.
                var latest = new[] { BackupDir, LegacyBackupDir }
                    .Where(Directory.Exists)
                    .SelectMany(d => new DirectoryInfo(d).GetFiles("MSI_Backup_*.json"))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest == null)
                    return "No MSI backup found to revert.";

                var entries = JsonSerializer.Deserialize<List<MsiBackupEntry>>(
                                  File.ReadAllText(latest.FullName)) ?? new List<MsiBackupEntry>();

                int reverted = 0;
                foreach (var e in entries)
                {
                    using var key = Registry.LocalMachine.OpenSubKey(e.MsiPropertiesKeyPath, writable: true);
                    if (key == null) continue;

                    if (e.HadValue)
                        key.SetValue("MSISupported", e.PreviousValue, RegistryValueKind.DWord);
                    else
                        key.DeleteValue("MSISupported", throwOnMissingValue: false);

                    reverted++;
                }

                return $"Success: Reverted MSI settings for {reverted} device(s) from {latest.Name}. Reboot required.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private static string SaveBackup(List<MsiBackupEntry> backup)
        {
            Directory.CreateDirectory(BackupDir);
            string path = Path.Combine(BackupDir, $"MSI_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(backup,
                new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
    }
}
