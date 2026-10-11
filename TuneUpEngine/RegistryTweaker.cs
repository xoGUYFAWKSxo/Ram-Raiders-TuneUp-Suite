using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace TuneUpEngine
{
    // A registry value a tweak sets. LegacyOff is only used to switch off a tweak that was applied
    // before the change journal existed (so its true original is unknown): null deletes the value,
    // which is the correct "off" for every Policies value — writing a "default" there would create
    // a policy that never existed.
    internal record TweakValue(RegistryHive Hive, string Path, string Name, RegistryValueKind Kind, object On, object? LegacyOff);

    // A key a tweak creates (context-menu entries and the like). The tweak is only "on" while the
    // key exists. Switching off restores it via the journal — deleting it if this app created it.
    internal record TweakKey(RegistryHive Hive, string Path);

    internal record TweakDefinition(string Id, TweakKey[] Keys, TweakValue[] Values);

    public class RegistryTweaker
    {
        private const RegistryHive HKLM = RegistryHive.LocalMachine;
        private const RegistryHive HKCU = RegistryHive.CurrentUser;
        private const RegistryValueKind DWord = RegistryValueKind.DWord;
        private const RegistryValueKind Str = RegistryValueKind.String;

        private const string MmcssProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        private const string Desktop = @"Control Panel\Desktop";
        private const string ExplorerAdvanced = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string PoliciesSystem = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        private const string ContentDelivery = @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        // HKCR is a merged view; new machine-wide shell verbs are written to HKLM\SOFTWARE\Classes
        // explicitly so the journal knows exactly where they live.
        private const string Classes = @"SOFTWARE\Classes";
        private const string TakeOwnFileCmd = "cmd.exe /c takeown /f \"%1\" && icacls \"%1\" /grant administrators:F";
        private const string TakeOwnDirCmd = "cmd.exe /c takeown /f \"%1\" /r /d y && icacls \"%1\" /grant administrators:F /t";
        private const string Win10MenuClsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

        private static TweakDefinition Tweak(string id, params TweakValue[] values) => new(id, Array.Empty<TweakKey>(), values);
        private static TweakDefinition Tweak(string id, TweakKey[] keys, params TweakValue[] values) => new(id, keys, values);
        private static TweakValue V(RegistryHive hive, string path, string name, RegistryValueKind kind, object on, object? legacyOff) =>
            new(hive, path, name, kind, on, legacyOff);

        // Every tweak, defined once. Reading the status, applying, journaling and switching off all
        // come from this table, so they can't drift apart (they previously did: several tweaks
        // checked a different value from the one they wrote).
        internal static readonly TweakDefinition[] Definitions =
        {
            // Gaming
            Tweak("NetworkThrottling", V(HKLM, MmcssProfile, "NetworkThrottlingIndex", DWord, unchecked((int)0xFFFFFFFF), 10)),
            Tweak("SystemResponsiveness", V(HKLM, MmcssProfile, "SystemResponsiveness", DWord, 0, 20)),
            Tweak("GameDVR",
                V(HKCU, @"System\GameConfigStore", "GameDVR_Enabled", DWord, 0, 1),
                V(HKCU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", DWord, 0, 1)),
            Tweak("PowerThrottling", V(HKLM, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", DWord, 1, null)),

            // System
            Tweak("DisableDriverUpdates", V(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", DWord, 1, null)),
            Tweak("DisableStoreUpdates", V(HKLM, @"SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", DWord, 2, null)),
            Tweak("DisableTelemetry", V(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", DWord, 0, null)),
            Tweak("ClearPageFile", V(HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "ClearPageFileAtShutdown", DWord, 1, 0)),

            // Windows usage
            // Explorer reads "link" as a 4-byte REG_BINARY.
            Tweak("DisableShortcutSuffix", V(HKCU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "link", RegistryValueKind.Binary, new byte[4], null)),
            Tweak("DisablePreventShutdown", V(HKCU, Desktop, "AutoEndTasks", Str, "1", "0")),

            // Security & stability
            Tweak("CloseUnresponsive",
                V(HKCU, Desktop, "WaitToKillAppTimeout", Str, "2000", "20000"),
                V(HKCU, Desktop, "HungAppTimeout", Str, "2000", "5000")),
            Tweak("DisableSecurityWarning", V(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Policies\Attachments", "SaveZoneInformation", DWord, 1, null)),
            Tweak("TakeOwnership",
                new[] { new TweakKey(HKLM, $@"{Classes}\*\shell\TakeOwnership"), new TweakKey(HKLM, $@"{Classes}\Directory\shell\TakeOwnership") },
                V(HKLM, $@"{Classes}\*\shell\TakeOwnership", "", Str, "Take Ownership", null),
                V(HKLM, $@"{Classes}\*\shell\TakeOwnership", "HasLUAShield", Str, "", null),
                V(HKLM, $@"{Classes}\*\shell\TakeOwnership", "NoWorkingDirectory", Str, "", null),
                V(HKLM, $@"{Classes}\*\shell\TakeOwnership\command", "", Str, TakeOwnFileCmd, null),
                V(HKLM, $@"{Classes}\*\shell\TakeOwnership\command", "IsolatedCommand", Str, TakeOwnFileCmd, null),
                V(HKLM, $@"{Classes}\Directory\shell\TakeOwnership", "", Str, "Take Ownership", null),
                V(HKLM, $@"{Classes}\Directory\shell\TakeOwnership", "HasLUAShield", Str, "", null),
                V(HKLM, $@"{Classes}\Directory\shell\TakeOwnership", "NoWorkingDirectory", Str, "", null),
                V(HKLM, $@"{Classes}\Directory\shell\TakeOwnership\command", "", Str, TakeOwnDirCmd, null),
                V(HKLM, $@"{Classes}\Directory\shell\TakeOwnership\command", "IsolatedCommand", Str, TakeOwnDirCmd, null)),
            Tweak("DisableSmartScreen", V(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", DWord, 0, null)),
            Tweak("ShowBSODDetails", V(HKLM, @"SYSTEM\CurrentControlSet\Control\CrashControl", "DisplayParameters", DWord, 1, 0)),
            Tweak("EnableNetworkDrivesElevated", V(HKLM, PoliciesSystem, "EnableLinkedConnections", DWord, 1, null)),
            // EnableLUA must be put back to 1, never deleted.
            Tweak("DisableUAC", V(HKLM, PoliciesSystem, "EnableLUA", DWord, 0, 1)),
            Tweak("EnableAntiAdware", V(HKLM, @"SOFTWARE\Policies\Microsoft\Windows Defender\MpEngine", "MpEnablePus", DWord, 1, null)),
            Tweak("ForceCrashDump", new[] { new TweakKey(HKLM, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps") }),

            // Desktop
            Tweak("SpeedUpMouseHover", V(HKCU, @"Control Panel\Mouse", "MouseHoverTime", Str, "10", "400")),
            Tweak("SpeedUpDesktopLoading", V(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "Max Cached Icons", Str, "4096", null)),
            Tweak("DisableBingSearch", V(HKCU, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", DWord, 1, null)),
            Tweak("TaskbarEndTask", V(HKCU, $@"{ExplorerAdvanced}\TaskbarDeveloperSettings", "TaskbarEndTask", DWord, 1, 0)),
            Tweak("TaskbarSeconds", V(HKCU, ExplorerAdvanced, "ShowSecondsInSystemClock", DWord, 1, 0)),

            // File Explorer
            Tweak("DisableNewAppPrompt", V(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "NoNewAppAlert", DWord, 1, null)),
            Tweak("CmdAdminContextMenu",
                new[] { new TweakKey(HKLM, $@"{Classes}\Directory\shell\runas") },
                V(HKLM, $@"{Classes}\Directory\shell\runas", "", Str, "Open Command Window Here as Administrator", null),
                V(HKLM, $@"{Classes}\Directory\shell\runas", "HasLUAShield", Str, "", null),
                V(HKLM, $@"{Classes}\Directory\shell\runas\command", "", Str, "cmd.exe /s /k pushd \"%V\"", null)),
            Tweak("ExplorerAutoComplete",
                V(HKCU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoComplete", "AutoSuggest", Str, "yes", "no"),
                V(HKCU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoComplete", "AppendCompletion", Str, "yes", "no")),
            Tweak("ShowDriveLettersFirst", V(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "ShowDriveLettersFirst", DWord, 4, 0)),

            // Appearance
            Tweak("SpeedUpAeroSnap", V(HKCU, Desktop, "WindowArrangementActive", Str, "1", "0")),
            Tweak("SpeedUpWindowsPeek", V(HKCU, ExplorerAdvanced, "DesktopLivePreviewHoverTime", DWord, 100, 1000)),
            Tweak("SpeedUpMenus", V(HKCU, Desktop, "MenuShowDelay", Str, "10", "400")),
            Tweak("Win10ContextMenu",
                new[] { new TweakKey(HKCU, Win10MenuClsid) },
                V(HKCU, $@"{Win10MenuClsid}\InprocServer32", "", Str, "", null)),
            Tweak("DisableWin11Ads",
                V(HKCU, ContentDelivery, "SubscribedContent-338389Enabled", DWord, 0, 1),
                V(HKCU, ContentDelivery, "SystemPaneSuggestionsEnabled", DWord, 0, 1),
                V(HKCU, ContentDelivery, "SoftLandingEnabled", DWord, 0, 1)),
            Tweak("DisableSigninBlur", V(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "DisableAcrylicBackgroundOnLogon", DWord, 1, null)),
            Tweak("EnableBatteryEstimatedTime", V(HKLM, @"SYSTEM\CurrentControlSet\Control\Power", "EnergyEstimationEnabled", DWord, 1, 0)),
            Tweak("RemoveShortcutArrow", V(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons", "29", Str, @"%windir%\System32\shell32.dll,-50", null)),
        };

        // Tweaks that weaken Windows security and are no longer offered. They can still be switched
        // OFF (so anyone who applied them can restore the protection), but never switched on:
        //   DisableUAC             — EnableLUA=0 turns off UAC entirely and breaks Store/UWP apps.
        //   DisableSmartScreen     — removes reputation checks on downloaded executables.
        //   DisableSecurityWarning — strips Mark-of-the-Web, so downloads lose their security zone.
        public static readonly IReadOnlySet<string> RetiredTweaks = new HashSet<string>(StringComparer.Ordinal)
        {
            "DisableUAC", "DisableSmartScreen", "DisableSecurityWarning"
        };

        // Tweaks that touch HKCU belong to one user, so their journal owner includes that user's
        // SID — otherwise one user switching a tweak off would also undo it for everyone else.
        internal static string OwnerOf(TweakDefinition tweak, ClientContext ctx) =>
            TouchesUserHive(tweak) ? $"tweak:{tweak.Id}@{ctx.Sid}" : $"tweak:{tweak.Id}";

        private static bool TouchesUserHive(TweakDefinition tweak) =>
            tweak.Keys.Any(k => k.Hive == RegistryHive.CurrentUser) ||
            tweak.Values.Any(v => v.Hive == RegistryHive.CurrentUser);

        // Machine-wide tweaks (anything under HKLM) need an administrator account. Per-user tweaks
        // only change the requesting user's own settings, so any user may apply them.
        internal static bool RequiresAdministrator(TweakDefinition tweak) =>
            tweak.Keys.Any(k => k.Hive == RegistryHive.LocalMachine) ||
            tweak.Values.Any(v => v.Hive == RegistryHive.LocalMachine);

        public static bool RequiresAdministrator(string tweakName) =>
            Definitions.FirstOrDefault(t => t.Id == tweakName) is { } t && RequiresAdministrator(t);

        // HKCU always means the requesting user's own hive — not the engine's (which is SYSTEM's
        // when running as a service, or another admin's after over-the-shoulder UAC).
        private static RegistryKey Root(RegistryHive hive, UserHiveHandle userHive)
        {
            if (hive == RegistryHive.LocalMachine) return Registry.LocalMachine;
            return userHive.Key ?? throw new InvalidOperationException("The user's registry hive is not loaded.");
        }

        public static Dictionary<string, bool> GetTweakStatuses(ClientContext ctx)
        {
            var statuses = new Dictionary<string, bool>();
            using var userHive = ctx.OpenUserHive();
            foreach (var tweak in Definitions)
            {
                try
                {
                    statuses[tweak.Id] = IsApplied(tweak, userHive);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RegistryTweaker] Failed to read {tweak.Id}: {ex.Message}");
                    statuses[tweak.Id] = false;
                }
            }
            return statuses;
        }

        internal static bool IsApplied(TweakDefinition tweak, ClientContext ctx)
        {
            using var userHive = ctx.OpenUserHive();
            return IsApplied(tweak, userHive);
        }

        private static bool IsApplied(TweakDefinition tweak, UserHiveHandle userHive)
        {
            foreach (var key in tweak.Keys)
            {
                using var k = Root(key.Hive, userHive).OpenSubKey(key.Path);
                if (k == null) return false;
            }

            foreach (var value in tweak.Values)
            {
                using var k = Root(value.Hive, userHive).OpenSubKey(value.Path);
                if (!ValueMatches(k?.GetValue(value.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), value.On))
                    return false;
            }

            return true;
        }

        private static bool ValueMatches(object? actual, object expected) => (actual, expected) switch
        {
            (int a, int e) => a == e,
            (string a, string e) => a == e,
            (byte[] a, byte[] e) => a.SequenceEqual(e),
            _ => false
        };

        public static bool SetTweakStatus(string tweakName, bool enable, ChangeJournal journal, ClientContext ctx)
        {
            if (enable && RetiredTweaks.Contains(tweakName))
            {
                Console.WriteLine($"[RegistryTweaker] Refused to enable retired security-reducing tweak {tweakName}.");
                return false;
            }

            var tweak = Definitions.FirstOrDefault(t => t.Id == tweakName);
            if (tweak == null) return false;

            if (RequiresAdministrator(tweak) && !ctx.IsAdministrator)
            {
                Console.WriteLine($"[RegistryTweaker] Refused {tweakName}: machine-wide tweaks require an administrator account.");
                return false;
            }

            try
            {
                return enable ? Apply(tweak, journal, ctx) : Remove(tweak, journal, ctx);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RegistryTweaker] Failed to set tweak {tweakName}: {ex.Message}");
                return false;
            }
        }

        internal static bool Apply(TweakDefinition tweak, ChangeJournal journal, ClientContext ctx)
        {
            string owner = OwnerOf(tweak, ctx);

            // Record every original first. If any can't be recorded, change nothing.
            bool captured =
                tweak.Keys.All(k => journal.CaptureRegistryKey(owner, k.Hive, k.Path, userSid: ctx.Sid)) &&
                tweak.Values.All(v => journal.CaptureRegistryValue(owner, v.Hive, v.Path, v.Name, userSid: ctx.Sid));
            if (!captured)
            {
                journal.Release(owner);
                Console.WriteLine($"[RegistryTweaker] Could not record the original state of {tweak.Id}; nothing was changed.");
                return false;
            }

            using var userHive = ctx.OpenUserHive(writable: true);

            foreach (var key in tweak.Keys)
            {
                using var _ = Root(key.Hive, userHive).CreateSubKey(key.Path);
            }

            foreach (var value in tweak.Values)
            {
                using var k = Root(value.Hive, userHive).CreateSubKey(value.Path);
                k.SetValue(value.Name, value.On, value.Kind);
            }

            return true;
        }

        internal static bool Remove(TweakDefinition tweak, ChangeJournal journal, ClientContext ctx)
        {
            string owner = OwnerOf(tweak, ctx);

            // Normal case: put back exactly what was there before this app changed it.
            if (journal.IsOwned(owner))
                return journal.Release(owner) == 0;

            // Applied before the journal existed (or by something else): the original is unknown,
            // so fall back to each value's documented "off" state.
            using var userHive = ctx.OpenUserHive(writable: true);

            foreach (var value in tweak.Values)
            {
                using var k = Root(value.Hive, userHive).OpenSubKey(value.Path, writable: true);
                if (k == null) continue;
                if (value.LegacyOff == null) k.DeleteValue(value.Name, throwOnMissingValue: false);
                else k.SetValue(value.Name, value.LegacyOff, value.Kind);
            }

            foreach (var key in tweak.Keys)
                Root(key.Hive, userHive).DeleteSubKeyTree(key.Path, throwOnMissingSubKey: false);

            return true;
        }
    }
}
