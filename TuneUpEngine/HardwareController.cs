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
        public string MsiPropertiesKeyPath { get; set; } = "";
        public string DeviceDesc { get; set; } = "";
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
            AppFolders.Root(Environment.SpecialFolder.CommonApplicationData), "RegistryBackups");

        private static string LegacyBackupDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolders.LegacyName, "RegistryBackups");

        // Device setup classes. Windows records a device's class as ClassGUID; the plain "Class"
        // name the scan used to look for isn't written on current Windows, so it found nothing.
        internal const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        internal const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

        internal static bool IsDisplayOrNet(string? classGuid, string? className) =>
            string.Equals(classGuid, DisplayClassGuid, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(classGuid, NetClassGuid, StringComparison.OrdinalIgnoreCase) ||
            className == "Display" || className == "Net";

        // A graphics or network PCI device that is plugged in and exposes an Interrupt Management
        // node (the sign its driver handles MSI settings).
        internal sealed record MsiDevice(string DevId, string InstId, string Name, int? MsiSupported)
        {
            public bool MsiOn => MsiSupported == 1;
            public string KeyPath => $@"{PciRoot}\{DevId}\{InstId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
        }

        [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        // The PCI registry branch also remembers devices that were removed or moved to another slot.
        // CM_LOCATE_DEVNODE_NORMAL only finds devices that are present now.
        private static bool IsPresent(string instanceId)
        {
            try { return CM_Locate_DevNodeW(out _, instanceId, 0) == 0; }
            catch { return true; } // can't tell: keep the device rather than hide it
        }

        private static List<MsiDevice> FindMsiCapableDevices()
        {
            var devices = new List<MsiDevice>();
            using var pciKey = Registry.LocalMachine.OpenSubKey(PciRoot)
                ?? throw new InvalidOperationException("Could not read PCI registry.");

            foreach (var devId in pciKey.GetSubKeyNames())
            {
                using var devKey = pciKey.OpenSubKey(devId);
                if (devKey == null) continue;
                foreach (var instId in devKey.GetSubKeyNames())
                {
                    using var instKey = devKey.OpenSubKey(instId);
                    if (instKey == null) continue;
                    if (!IsDisplayOrNet(instKey.GetValue("ClassGUID") as string, instKey.GetValue("Class") as string)) continue;

                    using var imKey = instKey.OpenSubKey(@"Device Parameters\Interrupt Management");
                    if (imKey == null) continue;
                    if (!IsPresent($@"PCI\{devId}\{instId}")) continue;

                    using var msiKey = imKey.OpenSubKey("MessageSignaledInterruptProperties");
                    string desc = instKey.GetValue("FriendlyName") as string
                                  ?? instKey.GetValue("DeviceDesc") as string ?? devId;
                    devices.Add(new MsiDevice(devId, instId, desc.Split(';')[^1],
                        msiKey?.GetValue("MSISupported") as int?));
                }
            }
            return devices;
        }

        // The UI enables "Enable MSI Mode" only when the text starts with "Compliant", i.e. there is
        // a supported device that isn't using MSI yet.
        internal static string DescribeScan(IReadOnlyList<MsiDevice> devices)
        {
            if (devices.Count == 0)
                return "No plugged-in graphics or network devices with MSI support were found.";

            int off = devices.Count(d => !d.MsiOn);
            string header = off > 0
                ? $"Compliant: {off} of {devices.Count} supported device(s) are not using MSI yet."
                : $"All {devices.Count} supported device(s) already use MSI — nothing to enable.";
            return header + "\n" + string.Join("\n",
                devices.Select(d => $"• {d.Name} — MSI {(d.MsiOn ? "on" : "off")}"));
        }

        public static string CheckMsiCompliance()
        {
            try
            {
                return DescribeScan(FindMsiCapableDevices());
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
            int alreadyOn = 0;
            int skipped = 0;
            var backup = new List<MsiBackupEntry>();
            try
            {
                // Only devices the scan reports: plugged in, graphics/network, and already exposing an
                // Interrupt Management node. Creating that node for devices without one risks black
                // screens or unbootable systems on hardware that can't do MSI.
                foreach (var device in FindMsiCapableDevices())
                {
                    if (device.MsiOn) { alreadyOn++; continue; }

                    // Record the true original in the change journal BEFORE writing. The journal
                    // keeps the first original it sees, so running Enable twice doesn't make
                    // "revert" restore the already-enabled value.
                    if (!journal.CaptureRegistryValue(JournalOwner, RegistryHive.LocalMachine, device.KeyPath, "MSISupported"))
                    {
                        skipped++;
                        continue;
                    }

                    using var msiKey = Registry.LocalMachine.CreateSubKey(device.KeyPath, writable: true);
                    if (msiKey == null) { skipped++; continue; }

                    // Also keep the human-readable backup file the user can inspect.
                    backup.Add(new MsiBackupEntry
                    {
                        MsiPropertiesKeyPath = device.KeyPath,
                        DeviceDesc = device.Name,
                        HadValue = device.MsiSupported != null,
                        PreviousValue = device.MsiSupported ?? 0
                    });

                    msiKey.SetValue("MSISupported", 1, RegistryValueKind.DWord);
                    updatedCount++;
                }

                if (updatedCount == 0)
                {
                    return skipped > 0
                        ? $"Error: {skipped} device(s) could not be changed ({alreadyOn} already use MSI)."
                        : $"Nothing to change: all {alreadyOn} supported device(s) already use MSI.";
                }

                string backupPath = SaveBackup(backup);
                return $"Success: Enabled MSI for {updatedCount} device(s) " +
                       $"({alreadyOn} already on, {skipped} skipped). Backup saved as {Path.GetFileName(backupPath)}. Reboot required.";
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
