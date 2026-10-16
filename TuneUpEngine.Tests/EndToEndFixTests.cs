using System;
using System.Collections.Generic;
using System.IO;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // Bugs found by driving a real engine over the pipe.
    public class MemoryListCommandTests
    {
        // SYSTEM_MEMORY_LIST_COMMAND as Windows numbers it. Purge was 2 (MemoryEmptyWorkingSets),
        // so "purge standby" trimmed every program's working set and the standby list grew.
        [Fact]
        public void CommandValuesMatchWindows()
        {
            Assert.Equal(2, (int)NativeMethods.SYSTEM_MEMORY_LIST_COMMAND.MemoryEmptyWorkingSets);
            Assert.Equal(4, (int)NativeMethods.SYSTEM_MEMORY_LIST_COMMAND.MemoryPurgeStandbyList);
            Assert.Equal(5, (int)NativeMethods.SYSTEM_MEMORY_LIST_COMMAND.MemoryPurgeLowPriorityStandbyList);
        }
    }

    // Healthy cores showed a red "OK" badge (the colour was keyed on IsSpike the wrong way round).
    public class LatencyBadgeTests
    {
        [Theory]
        [InlineData("OK", 0x10, 0x61, 0x40)]
        [InlineData("ELEVATED", 0xB4, 0x6A, 0x0C)]
        [InlineData("HIGH", 0x9B, 0x1C, 0x1C)]
        public void StatusColours(string status, byte r, byte g, byte b)
        {
            var brush = (System.Windows.Media.SolidColorBrush)TuneUpUI.LatencyStatusBadgeConverter.Instance
                .Convert(status, typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(System.Windows.Media.Color.FromRgb(r, g, b), brush.Color);
        }
    }

    // Found testing the destructive features against a real engine.
    public class DestructiveFeatureTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "RRTU_Repair_" + Guid.NewGuid().ToString("N"));
        private readonly string _key = @"Software\RRTU_Test_" + Guid.NewGuid().ToString("N");

        public void Dispose()
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_key, false);
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private ClientContext Me(bool admin)
        {
            var me = ClientContext.ForCurrentProcess();
            return new ClientContext { Sid = me.Sid, IsAdministrator = admin, Folders = new UserFolders { RoamingAppData = _dir } };
        }

        private RegistryIssue Issue(string hive, string valueName) => new RegistryIssue
        {
            Id = Guid.NewGuid().ToString(), Title = "Test " + valueName, KeyPath = $@"{hive}\{_key}",
            ValueName = valueName, IssueType = "Startup",
        };

        // The backup is staged in ProgramData, which only administrators can write, so run without
        // elevation the export fails; either way, an entry is deleted only together with a backup.
        [Fact]
        public void Repair_DeletesTheValueOnlyWithABackup()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_key)) k.SetValue("dead", @"C:\RRTU_missing\x.exe");

            var problems = new RegistryVerifier().Repair(new List<RegistryIssue> { Issue("HKCU", "dead") }, Me(admin: false));

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_key)!;
            if (problems.Count == 0)
            {
                Assert.Null(key.GetValue("dead"));
                Assert.Single(Directory.GetFiles(Path.Combine(_dir, "GalacticTuneUp", "RegistryBackups"), "*.reg"));
            }
            else
            {
                Assert.Contains("backup", Assert.Single(problems));
                Assert.NotNull(key.GetValue("dead"));
            }
        }

        // Skipped or failed entries were only written to the backup manifest while the UI said
        // the repair worked.
        [Fact]
        public void Repair_ReportsWhatItCouldNotRepair()
        {
            var problems = new RegistryVerifier().Repair(new List<RegistryIssue> { Issue("HKLM", "anything") }, Me(admin: false));

            Assert.Single(problems);
            Assert.Contains("administrator", problems[0]);
        }

        // Nothing is deleted without a backup: a key reg.exe can't export stays in place.
        [Fact]
        public void Repair_LeavesAnEntryItCannotBackUp()
        {
            var problems = new RegistryVerifier().Repair(new List<RegistryIssue> { Issue("HKCU", "missing-key-so-no-export") }, Me(admin: false));

            Assert.Single(problems);
            Assert.Contains("backup", problems[0]);
        }

        // Re-enabling restores the start type; only an Automatic service is started straight away.
        [Theory]
        [InlineData(2, true)]
        [InlineData(3, false)]
        public void ReEnabledServiceIsStartedOnlyIfAutomatic(int startType, bool start) =>
            Assert.Equal(start, DebloatGuard.ShouldStartAfterReEnable(startType));
    }

    public class EngineSettingsMigrationTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "RRTU_Settings_" + Guid.NewGuid().ToString("N"));
        private string Current => Path.Combine(_dir, "ProgramData", "engine_settings.json");
        private string Legacy => Path.Combine(_dir, "LocalAppData", "engine_settings.json");

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private void WriteLegacy()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Legacy)!);
            File.WriteAllText(Legacy, "{\"ProBalanceEnabled\":true,\"AutoPurgeEnabled\":false,\"GameModeEnabled\":true}");
        }

        [Fact]
        public void LegacyFile_IsMovedToProgramData()
        {
            WriteLegacy();

            var s = EngineSettings.Load(Current, Legacy);

            Assert.True(s.ProBalanceEnabled);
            Assert.True(s.GameModeEnabled);
            Assert.True(File.Exists(Current));
            Assert.False(File.Exists(Legacy));
        }

        // Uninstall deletes ProgramData. The next install must start with everything off, not
        // re-read the old file.
        [Fact]
        public void AfterProgramDataIsDeleted_DefaultsAreOff()
        {
            WriteLegacy();
            EngineSettings.Load(Current, Legacy);
            File.Delete(Current);

            var s = EngineSettings.Load(Current, Legacy);

            Assert.False(s.ProBalanceEnabled);
            Assert.False(s.AutoPurgeEnabled);
            Assert.False(s.GameModeEnabled);
        }

        // A legacy file left beside an existing current one (by a version that copied but never
        // deleted it) is ignored and removed, so it can't come back after an uninstall.
        [Fact]
        public void CurrentFile_WinsAndLeftoverLegacyIsRemoved()
        {
            WriteLegacy();
            Assert.True(new EngineSettings().Save(Current));

            var s = EngineSettings.Load(Current, Legacy);

            Assert.False(s.ProBalanceEnabled);
            Assert.False(File.Exists(Legacy));
        }
    }

    // The app was renamed on 2026-10-16. Its old data folder holds the change journal, so it must
    // move to the new name rather than be left behind (uninstall would no longer undo those changes).
    public class AppFolderRenameTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "tuneup-folders-" + Guid.NewGuid().ToString("N"));

        public AppFolderRenameTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void OldFolderIsMovedToTheNewName()
        {
            string legacy = Path.Combine(_dir, AppFolders.LegacyName);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "change_journal.json"), "[]");

            string root = AppFolders.Resolve(_dir);

            Assert.Equal(Path.Combine(_dir, AppFolders.Name), root);
            Assert.True(File.Exists(Path.Combine(root, "change_journal.json")));
            Assert.False(Directory.Exists(legacy));
        }

        [Fact]
        public void ExistingNewFolderIsNeverOverwritten()
        {
            Directory.CreateDirectory(Path.Combine(_dir, AppFolders.LegacyName));
            Directory.CreateDirectory(Path.Combine(_dir, AppFolders.Name));

            Assert.Equal(Path.Combine(_dir, AppFolders.Name), AppFolders.Resolve(_dir));
            Assert.True(Directory.Exists(Path.Combine(_dir, AppFolders.LegacyName)));
        }

        [Fact]
        public void LockedOldFolderIsUsedAsItIs()
        {
            string legacy = Path.Combine(_dir, AppFolders.LegacyName);
            Directory.CreateDirectory(legacy);
            using var open = File.Open(Path.Combine(legacy, "engine.log"), FileMode.Create, FileAccess.Write, FileShare.None);

            Assert.Equal(legacy, AppFolders.Resolve(_dir));
        }
    }

    // The Storage Tools subtitle once showed three garbled characters for an em dash: a UTF-8 file was read as Windows-1252
    // and saved back. Every source file must be valid UTF-8 with none of those leftovers.
    public class SourceEncodingTests
    {
        private static readonly string[] Extensions =
            { ".cs", ".xaml", ".iss", ".md", ".ps1", ".bat", ".html", ".css", ".js", ".json", ".yml", ".csproj" };

        // Escaped so this file stays ASCII and can't trip the check itself.
        private static readonly System.Text.RegularExpressions.Regex DoubleEncoded = new(
            "\u00E2\u20AC|\u00C3[\u0080-\u00BF\u0152-\u2122]|\u00C2[\u00A0-\u00BF]|\u00F0\u0178|\u00EF\u00BB\u00BF|\uFFFD");

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TuneUpApp.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("TuneUpApp.slnx not found above " + AppContext.BaseDirectory);
        }

        [Fact]
        public void NoSourceFileHasDoubleEncodedText()
        {
            var strictUtf8 = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);
            var problems = new List<string>();
            int scanned = 0;
            string root = RepoRoot();
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file);
                if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "publish" or ".git" or ".vs"))
                    continue;
                if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                scanned++;
                byte[] bytes = File.ReadAllBytes(file);
                int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                string text;
                try { text = strictUtf8.GetString(bytes, start, bytes.Length - start); }
                catch (System.Text.DecoderFallbackException) { problems.Add($"{rel}: not valid UTF-8"); continue; }
                string[] lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    if (DoubleEncoded.IsMatch(lines[i])) problems.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
            Assert.True(scanned > 50, $"Only {scanned} source files found under {root}");
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void CheckCatchesTheOldStorageSubtitle()
        {
            string broken = System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes("links \u2014 launchers"))
                .Replace('\u0080', '\u20AC').Replace('\u0094', '\u201D');
            Assert.Matches(DoubleEncoded, broken);
            Assert.DoesNotMatch(DoubleEncoded, "links \u2014 launchers, 5 \u00B5s, \u2713 Ready, \u26A0\uFE0F");
        }
    }
}
