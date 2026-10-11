using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // Stands in for bcdedit / powercfg / netsh so journal behaviour can be tested without touching
    // the real boot configuration, power plans or TCP stack.
    internal class FakeSystemCommands : ISystemCommands
    {
        public Dictionary<string, string> Bcd = new(StringComparer.OrdinalIgnoreCase);
        public bool BcdReadable = true;
        public string ActivePlan = "381b4222-f694-41f0-9685-ff5bb260df2e";
        public int FailNextPlanSets;
        public Dictionary<string, string> Netsh = new(StringComparer.OrdinalIgnoreCase) { ["ecncapability"] = "enabled" };
        public List<string> Calls = new();

        public (bool ok, string? value) GetBcdValue(string setting) =>
            BcdReadable ? (true, Bcd.TryGetValue(setting, out var v) ? v : null) : (false, null);
        public bool SetBcdValue(string setting, string value) { Calls.Add($"bcd set {setting}={value}"); Bcd[setting] = value; return true; }
        public bool DeleteBcdValue(string setting) { Calls.Add($"bcd delete {setting}"); Bcd.Remove(setting); return true; }
        public string? GetActivePowerPlan() => ActivePlan;
        public bool SetActivePowerPlan(string guid)
        {
            if (FailNextPlanSets > 0) { FailNextPlanSets--; return false; }
            ActivePlan = guid;
            return true;
        }
        public string? GetNetshTcpGlobal(string setting) => Netsh.TryGetValue(setting, out var v) ? v : null;
        public bool SetNetshTcpGlobal(string setting, string value) { Netsh[setting] = value; return true; }
    }

    // All registry work happens under a throwaway HKCU\Software\RRTU_Test_<guid> key.
    public class ChangeJournalTests : IDisposable
    {
        private readonly string _root = @"Software\RRTU_Test_" + Guid.NewGuid().ToString("N");
        private readonly string _journalFile = Path.Combine(Path.GetTempPath(), "rrtu_journal_" + Guid.NewGuid().ToString("N") + ".json");
        private readonly FakeSystemCommands _commands = new();

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
            try { File.Delete(_journalFile); } catch { }
        }

        private ChangeJournal NewJournal(bool persisted = false) =>
            new ChangeJournal(persisted ? _journalFile : null, _commands);

        private object? Read(string sub, string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{_root}\{sub}");
            return key?.GetValue(name);
        }

        private void Write(string sub, string name, object value, RegistryValueKind kind)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{_root}\{sub}");
            key.SetValue(name, value, kind);
        }

        private bool CaptureAndWrite(ChangeJournal j, string owner, string sub, string name, object value, RegistryValueKind kind)
        {
            if (!j.CaptureRegistryValue(owner, RegistryHive.CurrentUser, $@"{_root}\{sub}", name)) return false;
            Write(sub, name, value, kind);
            return true;
        }

        [Fact]
        public void Release_RestoresTheOriginalValue()
        {
            Write("a", "v", 20, RegistryValueKind.DWord);
            var j = NewJournal();

            CaptureAndWrite(j, "t", "a", "v", 0, RegistryValueKind.DWord);
            Assert.Equal(0, Read("a", "v"));

            Assert.Equal(0, j.Release("t"));
            Assert.Equal(20, Read("a", "v"));
            Assert.Empty(j.Snapshot());
        }

        [Fact]
        public void ValueThatDidNotExist_IsDeleted_AndKeyWeCreated_IsRemoved()
        {
            var j = NewJournal();
            CaptureAndWrite(j, "t", @"new\deeper", "v", 1, RegistryValueKind.DWord);

            j.Release("t");

            Assert.Null(Read(@"new\deeper", "v"));
            using var key = Registry.CurrentUser.OpenSubKey($@"{_root}\new\deeper");
            Assert.Null(key);
        }

        [Fact]
        public void ApplyingTwice_KeepsTheRealOriginal()
        {
            // The old Game Mode bug: re-applying recorded the already-tweaked value as "original".
            Write("a", "v", 20, RegistryValueKind.DWord);
            var j = NewJournal();

            CaptureAndWrite(j, "t", "a", "v", 0, RegistryValueKind.DWord);
            CaptureAndWrite(j, "t", "a", "v", 0, RegistryValueKind.DWord);
            j.Release("t");

            Assert.Equal(20, Read("a", "v"));
        }

        [Fact]
        public void SharedSetting_IsRestoredOnlyWhenTheLastOwnerReleases()
        {
            Write("a", "v", 20, RegistryValueKind.DWord);
            var j = NewJournal();

            CaptureAndWrite(j, "gamemode", "a", "v", 0, RegistryValueKind.DWord);
            CaptureAndWrite(j, "tweak:SystemResponsiveness", "a", "v", 0, RegistryValueKind.DWord);

            j.Release("tweak:SystemResponsiveness");
            Assert.Equal(0, Read("a", "v")); // Game Mode still wants it

            j.Release("gamemode");
            Assert.Equal(20, Read("a", "v"));
        }

        [Fact]
        public void OriginalsSurviveARestart()
        {
            Write("a", "v", "before", RegistryValueKind.String);
            CaptureAndWrite(NewJournal(persisted: true), "t", "a", "v", "after", RegistryValueKind.String);

            // Engine crashed / was closed; a new engine loads the journal from disk.
            var reloaded = NewJournal(persisted: true);
            Assert.True(reloaded.IsOwned("t"));
            reloaded.Release("t");

            Assert.Equal("before", Read("a", "v"));
        }

        [Fact]
        public void ValueKindsRoundTrip()
        {
            Write("k", "qword", 1234567890123L, RegistryValueKind.QWord);
            Write("k", "expand", "%windir%\\x", RegistryValueKind.ExpandString);
            Write("k", "multi", new[] { "a", "b" }, RegistryValueKind.MultiString);
            Write("k", "bin", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            var j = NewJournal(persisted: true);

            CaptureAndWrite(j, "t", "k", "qword", 1L, RegistryValueKind.QWord);
            CaptureAndWrite(j, "t", "k", "expand", "x", RegistryValueKind.ExpandString);
            CaptureAndWrite(j, "t", "k", "multi", new[] { "z" }, RegistryValueKind.MultiString);
            CaptureAndWrite(j, "t", "k", "bin", new byte[] { 9 }, RegistryValueKind.Binary);
            NewJournal(persisted: true).Release("t");

            using var key = Registry.CurrentUser.OpenSubKey($@"{_root}\k")!;
            Assert.Equal(1234567890123L, key.GetValue("qword"));
            Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("expand"));
            Assert.Equal("%windir%\\x", key.GetValue("expand", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(new[] { "a", "b" }, key.GetValue("multi"));
            Assert.Equal(new byte[] { 1, 2, 3 }, key.GetValue("bin"));
        }

        [Fact]
        public void TransientChanges_AreUndoneOnTheNextStart()
        {
            string original = _commands.ActivePlan;
            var j = NewJournal(persisted: true);
            Assert.True(j.CapturePowerPlan("gamemode-boost", transient: true));
            _commands.SetActivePowerPlan("05a5332a-9b3a-4cb6-ab67-8c654b65d4cd");

            // Engine died mid-game. Next start:
            NewJournal(persisted: true).ReleaseTransient();

            Assert.Equal(original, _commands.ActivePlan);
        }

        [Fact]
        public void Bcd_NotSetBefore_IsDeletedOnRestore()
        {
            var j = NewJournal();
            Assert.True(j.CaptureBcd("gamemode", "disabledynamictick"));
            _commands.SetBcdValue("disabledynamictick", "yes");

            j.Release("gamemode");

            Assert.False(_commands.Bcd.ContainsKey("disabledynamictick"));
            Assert.Contains("bcd delete disabledynamictick", _commands.Calls);
        }

        [Fact]
        public void Bcd_SetBefore_IsPutBack()
        {
            _commands.Bcd["disabledynamictick"] = "No";
            var j = NewJournal();
            j.CaptureBcd("gamemode", "disabledynamictick");
            _commands.SetBcdValue("disabledynamictick", "yes");

            j.Release("gamemode");

            Assert.Equal("No", _commands.Bcd["disabledynamictick"]);
        }

        [Fact]
        public void UnreadableOriginal_IsNotCaptured()
        {
            _commands.BcdReadable = false;
            var j = NewJournal();

            Assert.False(j.CaptureBcd("gamemode", "disabledynamictick"));
            Assert.False(j.IsOwned("gamemode"));
        }

        [Fact]
        public void UnsafeNetshValue_IsNotCaptured()
        {
            _commands.Netsh["ecncapability"] = "enabled & del C:\\x";
            Assert.False(NewJournal().CaptureNetshTcpGlobal("net", "ecncapability"));
        }

        [Fact]
        public void FailedRestore_StaysPending_AndIsRetried()
        {
            string original = _commands.ActivePlan;
            var j = NewJournal(persisted: true);
            j.CapturePowerPlan("boost");
            _commands.SetActivePowerPlan("05a5332a-9b3a-4cb6-ab67-8c654b65d4cd");

            _commands.FailNextPlanSets = 1;
            Assert.Equal(1, j.Release("boost"));
            Assert.True(j.Snapshot().Single().RestorePending);

            Assert.Equal(0, NewJournal(persisted: true).RetryPendingRestores());
            Assert.Equal(original, _commands.ActivePlan);
        }

        [Fact]
        public void ReleaseAll_UndoesEverything()
        {
            Write("a", "v", 1, RegistryValueKind.DWord);
            var j = NewJournal();
            CaptureAndWrite(j, "x", "a", "v", 2, RegistryValueKind.DWord);
            CaptureAndWrite(j, "y", "b", "w", 3, RegistryValueKind.DWord);

            Assert.Equal(0, j.ReleaseAll());

            Assert.Equal(1, Read("a", "v"));
            Assert.Null(Read("b", "w"));
        }
    }

    public class RegistryTweakerTableTests : IDisposable
    {
        private readonly string _root = @"Software\RRTU_Test_" + Guid.NewGuid().ToString("N");
        private static readonly ClientContext Me = ClientContext.ForCurrentProcess();

        public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

        [Fact]
        public void EveryTweakTheUiShows_IsStillDefined()
        {
            // The UI binds toggles by these IDs ("Tgl" + Id); none may be lost in the table rewrite.
            string[] expected =
            {
                "NetworkThrottling", "SystemResponsiveness", "GameDVR", "PowerThrottling", "DisableDriverUpdates",
                "DisableStoreUpdates", "DisableTelemetry", "ClearPageFile", "DisableShortcutSuffix", "DisablePreventShutdown",
                "CloseUnresponsive", "DisableSecurityWarning", "TakeOwnership", "DisableSmartScreen", "ShowBSODDetails",
                "EnableNetworkDrivesElevated", "DisableUAC", "EnableAntiAdware", "ForceCrashDump", "SpeedUpMouseHover",
                "SpeedUpDesktopLoading", "DisableBingSearch", "TaskbarEndTask", "TaskbarSeconds", "DisableNewAppPrompt",
                "CmdAdminContextMenu", "ExplorerAutoComplete", "ShowDriveLettersFirst", "SpeedUpAeroSnap", "SpeedUpWindowsPeek",
                "SpeedUpMenus", "Win10ContextMenu", "DisableWin11Ads", "DisableSigninBlur", "EnableBatteryEstimatedTime",
                "RemoveShortcutArrow"
            };
            var ids = RegistryTweaker.Definitions.Select(d => d.Id).ToList();

            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Equal(expected.OrderBy(x => x), ids.OrderBy(x => x));
        }

        [Fact]
        public void PolicyValues_AreDeletedNotOverwritten_WhenSwitchedOffWithoutAJournal()
        {
            // Writing a "default" into a Policies key creates a policy that never existed.
            foreach (var value in RegistryTweaker.Definitions.SelectMany(d => d.Values))
            {
                if (value.Path.Contains(@"\Policies\Microsoft\", StringComparison.OrdinalIgnoreCase))
                    Assert.Null(value.LegacyOff);
            }
        }

        [Fact]
        public void EnableLua_IsNeverDeleted()
        {
            var uac = RegistryTweaker.Definitions.Single(d => d.Id == "DisableUAC").Values.Single();
            Assert.Equal(1, uac.LegacyOff);
        }

        private TweakDefinition TestTweak() => new TweakDefinition(
            "Test",
            new[] { new TweakKey(RegistryHive.CurrentUser, $@"{_root}\menu") },
            new[]
            {
                new TweakValue(RegistryHive.CurrentUser, $@"{_root}\menu", "", RegistryValueKind.String, "Label", null),
                new TweakValue(RegistryHive.CurrentUser, $@"{_root}\desk", "Delay", RegistryValueKind.String, "10", "400"),
            });

        [Fact]
        public void ApplyThenRemove_RestoresExactlyWhatWasThere()
        {
            using (var k = Registry.CurrentUser.CreateSubKey($@"{_root}\desk")) k.SetValue("Delay", "250");
            var j = new ChangeJournal(null, new FakeSystemCommands());
            var tweak = TestTweak();

            Assert.True(RegistryTweaker.Apply(tweak, j, Me));
            Assert.True(RegistryTweaker.IsApplied(tweak, Me));

            Assert.True(RegistryTweaker.Remove(tweak, j, Me));
            Assert.False(RegistryTweaker.IsApplied(tweak, Me));
            using (var desk = Registry.CurrentUser.OpenSubKey($@"{_root}\desk"))
                Assert.Equal("250", desk!.GetValue("Delay")); // the user's own value, not the "400" default
            using (var menu = Registry.CurrentUser.OpenSubKey($@"{_root}\menu"))
                Assert.Null(menu); // the key we created is gone
        }

        [Fact]
        public void Remove_WithoutJournal_UsesLegacyOffValues()
        {
            var tweak = TestTweak();
            var applyJournal = new ChangeJournal(null, new FakeSystemCommands());
            RegistryTweaker.Apply(tweak, applyJournal, Me);

            // A fresh journal has no record (applied by an older version).
            Assert.True(RegistryTweaker.Remove(tweak, new ChangeJournal(null, new FakeSystemCommands()), Me));

            using (var desk = Registry.CurrentUser.OpenSubKey($@"{_root}\desk"))
                Assert.Equal("400", desk!.GetValue("Delay"));
            using (var menu = Registry.CurrentUser.OpenSubKey($@"{_root}\menu"))
                Assert.Null(menu);
        }
    }
}
