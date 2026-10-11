using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TuneUpEngine
{
    // One setting the engine changed, with the value it had BEFORE the first change.
    public class JournalTarget
    {
        public string Type { get; set; } = "";       // RegValue | RegKey | Bcd | PowerPlan | Netsh
        public string Hive { get; set; } = "";       // HKLM | HKCU (registry targets only)
        public string? UserSid { get; set; }          // HKCU targets: whose hive it was
        public string Path { get; set; } = "";       // Registry key path, or the setting name
        public string? Name { get; set; }             // Registry value name

        public bool Existed { get; set; }             // Did the value / key / setting exist?
        public bool KeyExisted { get; set; } = true;  // RegValue: did its containing key exist?
        public string? ValueKind { get; set; }        // RegValue: RegistryValueKind name
        public string? DataJson { get; set; }         // Original data, JSON-encoded

        public List<string> Owners { get; set; } = new();
        public bool Transient { get; set; }           // Undo on next engine start (crash recovery)
        public bool RestorePending { get; set; }      // All owners released but restore failed — retried later
        public long Sequence { get; set; }            // Capture order; restores run newest-first

        public string Identity =>
            $"{Type}|{Hive}|{UserSid}|{Path}|{Name}".ToLowerInvariant();
    }

    public class JournalFile
    {
        public int Version { get; set; } = 1;
        public long NextSequence { get; set; } = 1;
        public List<JournalTarget> Targets { get; set; } = new();
    }

    // Commands the journal needs to read and restore non-registry settings. Abstracted so tests can
    // exercise the journal without touching the real boot configuration, power plans or TCP stack.
    public interface ISystemCommands
    {
        // ok=false: couldn't be read. ok=true, value=null: the setting isn't set.
        (bool ok, string? value) GetBcdValue(string setting);
        bool SetBcdValue(string setting, string value);
        bool DeleteBcdValue(string setting);
        string? GetActivePowerPlan();
        bool SetActivePowerPlan(string guid);
        string? GetNetshTcpGlobal(string setting);
        bool SetNetshTcpGlobal(string setting, string value);
    }

    // The engine's record of every system setting it has changed, so each change can be undone to
    // the user's ACTUAL previous value — not a hard-coded "default" — even after a crash or reboot.
    //
    // Rules:
    //  * Capture BEFORE writing. The first capture of a setting records its original value; later
    //    captures (by the same or another feature) only add an owner, so re-applying a tweak can
    //    never overwrite the real original with an already-tweaked value.
    //  * A setting is restored only when its LAST owner releases it, so two features that touch
    //    the same value (e.g. Game Mode and the SystemResponsiveness tweak) can't undo each other.
    //  * Every change is persisted immediately (atomic replace), so a crash right after a write
    //    still leaves the original on disk.
    //  * Transient targets (e.g. the power plan switched while a game is boosted) are undone on
    //    the next engine start if the engine died before undoing them itself.
    //
    // The file is written with administrator rights and drives registry writes on restore, so it
    // lives in ProgramData with an ACL that only Administrators and SYSTEM can modify.
    public class ChangeJournal
    {
        public static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RamRaidersTuneUpSuite", "change_journal.json");

        private readonly string? _path;
        private readonly bool _hardenAcl;
        private readonly ISystemCommands _commands;
        private readonly object _lock = new();
        private JournalFile _file = new();
        private bool _directorySecured;

        public event Action<string>? OnLog;

        // The tools used to read and change non-registry settings (bcdedit, powercfg, netsh).
        public ISystemCommands Commands => _commands;

        // path == null keeps the journal in memory only (used by tests and as a safe fallback).
        public ChangeJournal(string? path, ISystemCommands? commands = null, bool hardenAcl = false)
        {
            _path = path;
            _hardenAcl = hardenAcl;
            _commands = commands ?? new WindowsSystemCommands();
            Load();
        }

        public static ChangeJournal CreateDefault() =>
            new ChangeJournal(DefaultPath, new WindowsSystemCommands(), hardenAcl: true);

        // ── Queries ─────────────────────────────────────────────────────────────

        public bool IsOwned(string owner)
        {
            lock (_lock) return _file.Targets.Any(t => t.Owners.Contains(owner));
        }

        public IReadOnlyList<JournalTarget> Snapshot()
        {
            lock (_lock) return _file.Targets.Select(Clone).ToList();
        }

        // ── Capture ─────────────────────────────────────────────────────────────

        // userSid: for HKCU, whose hive this is (defaults to the engine's own user). Requests from
        // the UI pass the connected user's SID, so their settings land in — and are later restored
        // to — their own hive even when the engine runs as a service.
        public bool CaptureRegistryValue(string owner, RegistryHive hive, string keyPath, string valueName, bool transient = false, string? userSid = null)
        {
            var target = NewRegistryTarget("RegValue", hive, keyPath, valueName, transient, userSid);
            return Capture(owner, target, t => ReadRegistryValue(hive, t));
        }

        // Records whether a key existed, so a key this app created (e.g. a context-menu entry) can
        // be deleted again on restore. Capture the key BEFORE the values written inside it.
        public bool CaptureRegistryKey(string owner, RegistryHive hive, string keyPath, bool transient = false, string? userSid = null)
        {
            var target = NewRegistryTarget("RegKey", hive, keyPath, null, transient, userSid);
            return Capture(owner, target, t =>
            {
                using var key = BaseKey(hive, t.UserSid, writable: false)?.OpenSubKey(keyPath);
                t.Existed = key != null;
                return true;
            });
        }

        public bool CaptureBcd(string owner, string setting, bool transient = false)
        {
            var target = new JournalTarget { Type = "Bcd", Path = setting, Transient = transient };
            return Capture(owner, target, t =>
            {
                var (ok, value) = _commands.GetBcdValue(setting);
                if (!ok) return false;
                t.Existed = value != null;
                t.DataJson = value == null ? null : JsonSerializer.Serialize(value);
                return true;
            });
        }

        public bool CapturePowerPlan(string owner, bool transient = false)
        {
            var target = new JournalTarget { Type = "PowerPlan", Path = "active", Transient = transient };
            return Capture(owner, target, t =>
            {
                string? guid = _commands.GetActivePowerPlan();
                if (guid == null || !Guid.TryParse(guid, out _)) return false;
                t.Existed = true;
                t.DataJson = JsonSerializer.Serialize(guid);
                return true;
            });
        }

        public bool CaptureNetshTcpGlobal(string owner, string setting, bool transient = false)
        {
            var target = new JournalTarget { Type = "Netsh", Path = setting, Transient = transient };
            return Capture(owner, target, t =>
            {
                string? value = _commands.GetNetshTcpGlobal(setting);
                if (value == null || !IsSafeToken(value)) return false;
                t.Existed = true;
                t.DataJson = JsonSerializer.Serialize(value);
                return true;
            });
        }

        // Returns true when the original state is safely recorded (now or earlier). Callers must
        // not make the change when this returns false — it could never be undone.
        private bool Capture(string owner, JournalTarget candidate, Func<JournalTarget, bool> readOriginal)
        {
            lock (_lock)
            {
                var existing = _file.Targets.FirstOrDefault(t => t.Identity == candidate.Identity);
                if (existing != null)
                {
                    if (!existing.Owners.Contains(owner)) existing.Owners.Add(owner);
                    existing.RestorePending = false; // Re-owned: the change is wanted again.
                    existing.Transient &= candidate.Transient;
                    Save();
                    return true;
                }

                try
                {
                    if (!readOriginal(candidate)) return false;
                }
                catch (Exception ex)
                {
                    Log($"[Journal] Could not read original state of {Describe(candidate)}: {ex.Message}");
                    return false;
                }

                candidate.Owners.Add(owner);
                candidate.Sequence = _file.NextSequence++;
                _file.Targets.Add(candidate);
                Save();
                return true;
            }
        }

        // ── Release / restore ───────────────────────────────────────────────────

        // The owner no longer needs its changes. Every setting no other owner still holds is put
        // back exactly as it was. Returns the number of settings that could NOT be restored (they
        // stay in the journal and are retried on the next engine start).
        public int Release(string owner) => ReleaseWhere(t => t.Owners.Contains(owner), t => t.Owners.Remove(owner));

        public int ReleaseTransient() => ReleaseWhere(t => t.Transient, t => t.Owners.Clear());

        // Undo everything this app ever changed (used by the uninstaller via --revert-all).
        public int ReleaseAll() => ReleaseWhere(_ => true, t => t.Owners.Clear());

        // Retries restores that failed earlier (e.g. a user hive that wasn't loaded at the time).
        public int RetryPendingRestores() => ReleaseWhere(t => t.RestorePending, _ => { });

        private int ReleaseWhere(Func<JournalTarget, bool> match, Action<JournalTarget> release)
        {
            lock (_lock)
            {
                var affected = _file.Targets.Where(match).ToList();
                foreach (var t in affected) release(t);

                int failures = 0;
                // Newest first: values inside a key this app created are restored before the key.
                foreach (var t in affected.Where(t => t.Owners.Count == 0).OrderByDescending(t => t.Sequence))
                {
                    bool restored;
                    try
                    {
                        restored = Restore(t);
                    }
                    catch (Exception ex)
                    {
                        Log($"[Journal] Restore failed for {Describe(t)}: {ex.Message}");
                        restored = false;
                    }

                    if (restored)
                    {
                        _file.Targets.Remove(t);
                    }
                    else
                    {
                        t.RestorePending = true;
                        failures++;
                    }
                }

                Save();
                return failures;
            }
        }

        private bool Restore(JournalTarget t)
        {
            switch (t.Type)
            {
                case "RegValue": return RestoreRegistryValue(t);
                case "RegKey": return RestoreRegistryKey(t);
                case "Bcd":
                    if (!t.Existed) return _commands.DeleteBcdValue(t.Path);
                    string? bcd = Deserialize<string>(t.DataJson);
                    return bcd != null && IsSafeToken(bcd) && _commands.SetBcdValue(t.Path, bcd);
                case "PowerPlan":
                    string? guid = Deserialize<string>(t.DataJson);
                    return guid != null && Guid.TryParse(guid, out _) && _commands.SetActivePowerPlan(guid);
                case "Netsh":
                    string? netsh = Deserialize<string>(t.DataJson);
                    return netsh != null && IsSafeToken(netsh) && _commands.SetNetshTcpGlobal(t.Path, netsh);
                default:
                    Log($"[Journal] Unknown entry type '{t.Type}' dropped.");
                    return true;
            }
        }

        // ── Registry helpers ────────────────────────────────────────────────────

        private static JournalTarget NewRegistryTarget(string type, RegistryHive hive, string keyPath, string? valueName, bool transient, string? userSid)
        {
            if (hive != RegistryHive.LocalMachine && hive != RegistryHive.CurrentUser)
                throw new ArgumentException("Only HKLM and HKCU are supported.", nameof(hive));

            return new JournalTarget
            {
                Type = type,
                Hive = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU",
                UserSid = hive == RegistryHive.CurrentUser ? (userSid ?? WindowsIdentity.GetCurrent().User?.Value) : null,
                Path = keyPath.Trim('\\'),
                Name = valueName,
                Transient = transient
            };
        }

        // HKCU entries are restored into the hive of the user they were captured from, so they
        // still land in the right place if the engine is later running as someone else.
        private static RegistryKey? BaseKey(RegistryHive hive, string? userSid, bool writable)
        {
            if (hive == RegistryHive.LocalMachine) return Registry.LocalMachine;

            string? current = WindowsIdentity.GetCurrent().User?.Value;
            if (userSid == null || string.Equals(userSid, current, StringComparison.OrdinalIgnoreCase))
                return Registry.CurrentUser;

            return Registry.Users.OpenSubKey(userSid, writable); // null if that user's hive isn't loaded
        }

        private static RegistryHive HiveOf(JournalTarget t) =>
            t.Hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;

        private static bool ReadRegistryValue(RegistryHive hive, JournalTarget t)
        {
            using var key = BaseKey(hive, t.UserSid, writable: false)?.OpenSubKey(t.Path);
            t.KeyExisted = key != null;
            object? data = key?.GetValue(t.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            t.Existed = data != null;
            if (key == null || data == null) return true;

            var kind = key.GetValueKind(t.Name);
            t.ValueKind = kind.ToString();
            t.DataJson = kind switch
            {
                RegistryValueKind.DWord => JsonSerializer.Serialize((int)data),
                RegistryValueKind.QWord => JsonSerializer.Serialize((long)data),
                RegistryValueKind.String or RegistryValueKind.ExpandString => JsonSerializer.Serialize((string)data),
                RegistryValueKind.MultiString => JsonSerializer.Serialize((string[])data),
                _ => JsonSerializer.Serialize(data as byte[] ?? Array.Empty<byte>())
            };
            return true;
        }

        private bool RestoreRegistryValue(JournalTarget t)
        {
            var hive = HiveOf(t);
            var baseKey = BaseKey(hive, t.UserSid, writable: true);
            if (baseKey == null) return false; // User hive not loaded — retry later.

            if (!t.Existed)
            {
                using (var key = baseKey.OpenSubKey(t.Path, writable: true))
                {
                    key?.DeleteValue(t.Name ?? "", throwOnMissingValue: false);
                }

                // We created the key just to hold this value: remove it again if nothing else uses it.
                if (!t.KeyExisted) DeleteKeyIfEmpty(baseKey, t.Path);
                return true;
            }

            var kind = Enum.Parse<RegistryValueKind>(t.ValueKind ?? nameof(RegistryValueKind.Binary));
            object value = kind switch
            {
                RegistryValueKind.DWord => Deserialize<int>(t.DataJson),
                RegistryValueKind.QWord => Deserialize<long>(t.DataJson),
                RegistryValueKind.String or RegistryValueKind.ExpandString => Deserialize<string>(t.DataJson) ?? "",
                RegistryValueKind.MultiString => Deserialize<string[]>(t.DataJson) ?? Array.Empty<string>(),
                _ => Deserialize<byte[]>(t.DataJson) ?? Array.Empty<byte>()
            };
            if (kind != RegistryValueKind.DWord && kind != RegistryValueKind.QWord &&
                kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString &&
                kind != RegistryValueKind.MultiString)
                kind = RegistryValueKind.Binary;

            using var writeKey = baseKey.CreateSubKey(t.Path, writable: true);
            writeKey.SetValue(t.Name ?? "", value, kind);
            return true;
        }

        private static bool RestoreRegistryKey(JournalTarget t)
        {
            if (t.Existed) return true; // It was there before; its values are restored individually.

            var baseKey = BaseKey(HiveOf(t), t.UserSid, writable: true);
            if (baseKey == null) return false;
            baseKey.DeleteSubKeyTree(t.Path, throwOnMissingSubKey: false);
            return true;
        }

        private static void DeleteKeyIfEmpty(RegistryKey baseKey, string path)
        {
            try
            {
                using (var key = baseKey.OpenSubKey(path))
                {
                    if (key == null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
                }
                baseKey.DeleteSubKey(path, throwOnMissingSubKey: false);
            }
            catch
            {
                // Best effort — an empty leftover key is harmless.
            }
        }

        // ── Persistence ─────────────────────────────────────────────────────────

        private void Load()
        {
            if (_path == null || !File.Exists(_path)) return;

            try
            {
                if (_hardenAcl && !IsTrustedOwner(_path))
                {
                    // Someone other than an administrator created this file. Its contents would
                    // drive privileged registry writes, so it is ignored and replaced.
                    Log("[Journal] Ignoring a change journal not owned by Administrators/SYSTEM.");
                    return;
                }

                _file = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(_path)) ?? new JournalFile();
                _file.Targets.RemoveAll(t => string.IsNullOrEmpty(t.Type));
            }
            catch (Exception ex)
            {
                Log($"[Journal] Could not read {_path}: {ex.Message}. Starting a new journal.");
                try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch { }
                _file = new JournalFile();
            }
        }

        private void Save()
        {
            if (_path == null) return;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(_path)!;
                if (_hardenAcl && !_directorySecured)
                {
                    EnsureProtectedDirectory(dir);
                    _directorySecured = true;
                }
                else
                {
                    Directory.CreateDirectory(dir);
                }

                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log($"[Journal] Failed to save {_path}: {ex.Message}");
            }
        }

        // Administrators and SYSTEM: full control. Everyone else: read only. Inheritance from
        // ProgramData (which lets ordinary users create files) is removed.
        private static void EnsureProtectedDirectory(string dir)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

            var info = new DirectoryInfo(dir);
            if (!info.Exists) info.Create(security);
            else info.SetAccessControl(security);
        }

        private static bool IsTrustedOwner(string file)
        {
            var owner = new FileInfo(file).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner != null &&
                   (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                    owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
        }

        // ── Misc ────────────────────────────────────────────────────────────────

        // Values passed back to bcdedit / netsh must be single plain words.
        internal static bool IsSafeToken(string value) => Regex.IsMatch(value, "^[A-Za-z0-9]{1,32}$");

        private static T? Deserialize<T>(string? json) =>
            string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json);

        private static string Describe(JournalTarget t) =>
            t.Type.StartsWith("Reg") ? $"{t.Hive}\\{t.Path}{(t.Name != null ? "\\" + t.Name : "")}" : $"{t.Type} {t.Path}";

        private static JournalTarget Clone(JournalTarget t) =>
            JsonSerializer.Deserialize<JournalTarget>(JsonSerializer.Serialize(t))!;

        private void Log(string message)
        {
            Console.WriteLine(message);
            OnLog?.Invoke(message);
        }
    }

    // The real implementation of ISystemCommands, shelling out to the Windows tools.
    public class WindowsSystemCommands : ISystemCommands
    {
        private static readonly Dictionary<string, string> NetshLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["autotuninglevel"] = "Receive Window Auto-Tuning Level",
            ["ecncapability"] = "ECN Capability",
            ["timestamps"] = "RFC 1323 Timestamps",
            ["rss"] = "Receive-Side Scaling State",
        };

        public (bool ok, string? value) GetBcdValue(string setting)
        {
            var (exit, output) = Run("bcdedit", "/enum {current}");
            if (exit != 0 || string.IsNullOrEmpty(output)) return (false, null);
            var match = Regex.Match(output, $@"^\s*{Regex.Escape(setting)}\s+(\S+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return (true, match.Success ? match.Groups[1].Value : null);
        }

        public bool SetBcdValue(string setting, string value) =>
            Run("bcdedit", $"/set {{current}} {setting} {value}").exit == 0;

        public bool DeleteBcdValue(string setting)
        {
            var (exit, output) = Run("bcdedit", $"/deletevalue {{current}} {setting}");
            // Deleting a value that isn't set is not a failure for our purposes.
            return exit == 0 || output.Contains("not found", StringComparison.OrdinalIgnoreCase);
        }

        public string? GetActivePowerPlan()
        {
            var (exit, output) = Run("powercfg", "/getactivescheme");
            if (exit != 0) return null;
            var match = Regex.Match(output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return match.Success ? match.Value : null;
        }

        public bool SetActivePowerPlan(string guid) =>
            Guid.TryParse(guid, out _) && Run("powercfg", $"-setactive {guid}").exit == 0;

        // Only works on English Windows (netsh labels are localised). Elsewhere it returns null,
        // so the setting is not captured and therefore not changed.
        public string? GetNetshTcpGlobal(string setting)
        {
            if (!NetshLabels.TryGetValue(setting, out var label)) return null;
            var (exit, output) = Run("netsh", "int tcp show global");
            if (exit != 0) return null;
            var match = Regex.Match(output, $@"{Regex.Escape(label)}\s*:\s*(\S+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        public bool SetNetshTcpGlobal(string setting, string value) =>
            NetshLabels.ContainsKey(setting) && ChangeJournal.IsSafeToken(value) &&
            Run("netsh", $"int tcp set global {setting}={value}").exit == 0;

        private static (int exit, string output) Run(string exe, string args)
        {
            try
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var stderr = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(15000)) { try { process.Kill(); } catch { } return (-1, output); }
                return (process.ExitCode, output + stderr.Result);
            }
            catch
            {
                return (-1, "");
            }
        }
    }
}
