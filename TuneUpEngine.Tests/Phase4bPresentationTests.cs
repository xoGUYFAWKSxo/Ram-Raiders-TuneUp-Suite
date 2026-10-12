using System.Collections.Generic;
using TuneUpEngine;
using TuneUpUI.Presentation;
using Xunit;

namespace TuneUpEngine.Tests
{
    // The window's display logic, tested without opening a window.
    public class DashboardFormatterTests
    {
        private static EngineStatsPayload Stats(long standby) => new()
        {
            TotalMemoryBytes = 32UL * 1024 * 1024 * 1024,
            AvailableMemoryBytes = 12UL * 1024 * 1024 * 1024,
            MemoryFreePercent = 37.5,
            TotalMemoryFreedBytes = 512L * 1024 * 1024,
            StandbyBytes = standby,
            EventLogs = new List<string>(),
        };

        [Fact]
        public void FormatsTheMemoryCards()
        {
            var text = DashboardFormatter.Format(Stats(standby: 3L * 1024 * 1024 * 1024));

            Assert.Equal("38 %", text.FreePercent);           // rounds like the old UI
            Assert.Equal("12.0 GB / 32.0 GB", text.AvailableOfTotal);
            Assert.Equal("3072 MB", text.Standby);
            Assert.Equal("512 MB", text.Reclaimed);
        }

        [Fact]
        public void UnknownStandbySize_IsShownAsNotAvailable_NeverGuessed()
        {
            Assert.Equal("n/a", DashboardFormatter.Format(Stats(standby: -1)).Standby);
        }
    }

    public class EventLogViewTests
    {
        private static List<string> Log(int count, string last) =>
            new List<string>(new string[count - 1]) { last };

        [Fact]
        public void NewEntry_AtTheCap_StillRefreshes()
        {
            // The Phase 0 freeze: at the 100-entry cap the count stops changing but entries don't.
            Assert.True(EventLogView.NeedsRefresh(100, "[10:00:01] old", Log(100, "[10:00:05] new")));
        }

        [Fact]
        public void Unchanged_DoesNotRefresh()
        {
            Assert.False(EventLogView.NeedsRefresh(100, "[10:00:05] same", Log(100, "[10:00:05] same")));
        }

        [Fact]
        public void DifferentCount_Refreshes()
        {
            Assert.True(EventLogView.NeedsRefresh(3, "[x] a", Log(4, "[x] a")));
        }
    }

    public class TrayToastPolicyTests
    {
        [Theory]
        [InlineData("[ProBalance] Throttled process: x", true, false, true)]
        [InlineData("[Memory] Standby list auto-purged. Freed 300.0 MB", false, true, true)]
        [InlineData("[Memory] Standby list auto-purged. Freed 300.0 MB", true, false, false)] // memory toasts off
        [InlineData("[GameMode] Elevated game", true, true, false)]                           // not a toast category
        public void NotifiesOnlyForChosenEventTypes(string line, bool proBalance, bool memory, bool expected)
        {
            Assert.Equal(expected, TrayToastPolicy.ShouldNotify(line, windowVisible: false, notificationsEnabled: true, proBalance, memory));
        }

        [Fact]
        public void NeverNotifiesWhileTheWindowIsOpen_OrWhenDisabled()
        {
            const string line = "[ProBalance] Throttled process: x";
            Assert.False(TrayToastPolicy.ShouldNotify(line, windowVisible: true, notificationsEnabled: true, true, true));
            Assert.False(TrayToastPolicy.ShouldNotify(line, windowVisible: false, notificationsEnabled: false, true, true));
        }
    }

    public class ServiceStatusTextTests
    {
        [Theory]
        [InlineData(2, false, true, "Startup: Automatic  ·  Running")]
        [InlineData(2, true, true, "Startup: Automatic (delayed start)  ·  Running")]
        [InlineData(3, false, false, "Startup: Manual  ·  Stopped")]
        [InlineData(4, false, false, "Startup: Disabled  ·  Stopped")]
        [InlineData(-1, false, false, "Startup: Unknown  ·  Stopped")]
        public void ShowsStartTypeAndRunningState(int start, bool delayed, bool running, string expected)
        {
            var svc = new ServiceStatus { StartType = start, IsDelayedStart = delayed, IsRunning = running };
            Assert.Equal(expected, ServiceStatusText.Describe(svc));
        }
    }

    public class CleanConfirmationTests
    {
        [Fact]
        public void States_CountAndSize()
        {
            var text = CleanConfirmation.Build(new List<ScanItem>
            {
                new() { RuleName = "Temp", Category = "System", TotalSizeBytes = 3L * 1024 * 1024 },
                new() { RuleName = "Cache", Category = "Internet", TotalSizeBytes = 1L * 1024 * 1024 },
            });
            Assert.StartsWith("Permanently delete 2 selected item(s) (about 4.0 MB)?", text);
            Assert.DoesNotContain("duplicate", text);
            Assert.DoesNotContain("Please note", text);
        }

        [Fact]
        public void ExplainsDuplicatesKeepOneCopy_AndListsRuleWarnings()
        {
            var text = CleanConfirmation.Build(new List<ScanItem>
            {
                new() { RuleName = "Duplicate Set 1-1", Category = AdvancedScanner.DuplicateFilesCategory, TotalSizeBytes = 10 },
                new() { RuleName = "Mouse Center", Category = "Applications", Warning = "This will also delete all app-specific settings." },
            });
            Assert.Contains("the first copy listed is kept", text);
            Assert.Contains("• Mouse Center: This will also delete all app-specific settings.", text);
        }
    }
}
