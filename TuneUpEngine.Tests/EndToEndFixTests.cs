using System;
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
}
