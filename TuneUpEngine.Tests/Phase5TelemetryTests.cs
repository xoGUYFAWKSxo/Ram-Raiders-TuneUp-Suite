using System;
using System.Collections.Generic;
using System.Linq;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    public class MemoryListTests
    {
        private const long Page = 4096;
        private const ulong EightGb = 8UL * 1024 * 1024 * 1024;

        [Fact]
        public void PageCounts_AreConvertedAndStandbyIsTheSumOfAllPriorities()
        {
            // Zero, Free, Modified, ModifiedNoWrite, Bad, Standby[8], Repurposed[8], ModifiedPageFile
            long[] fields = new long[22];
            fields[0] = 100; fields[1] = 50; fields[2] = 30;
            for (int i = 5; i < 13; i++) fields[i] = 10;   // standby priorities 0-7
            for (int i = 13; i < 21; i++) fields[i] = 999; // repurposed counts — not memory

            var lists = StandbyListManager.FromPageCounts(fields, Page);

            Assert.Equal(100 * Page, lists.ZeroBytes);
            Assert.Equal(50 * Page, lists.FreeBytes);
            Assert.Equal(30 * Page, lists.ModifiedBytes);
            Assert.Equal(80 * Page, lists.StandbyBytes);
            Assert.Equal(150 * Page, lists.UnusedBytes);
        }

        private static MemoryLists Lists(double unusedGb, double standbyGb)
        {
            const double gb = 1024.0 * 1024 * 1024;
            return new MemoryLists((long)(unusedGb * gb), 0, 0, (long)(standbyGb * gb));
        }

        [Fact]
        public void AutoPurge_WhenFreeMemoryIsShortAndTheCacheIsLarge()
        {
            Assert.True(StandbyListManager.ShouldAutoPurge(Lists(0.5, 3), EightGb, DateTime.UtcNow, DateTime.MinValue));
        }

        [Fact]
        public void NoAutoPurge_WhenThereIsPlentyOfFreeMemory()
        {
            Assert.False(StandbyListManager.ShouldAutoPurge(Lists(4, 3), EightGb, DateTime.UtcNow, DateTime.MinValue));
        }

        [Fact]
        public void NoAutoPurge_WhenTheCacheHasLittleToGiveBack()
        {
            // The old trigger fired here: "available" was low, but purging would free almost nothing.
            Assert.False(StandbyListManager.ShouldAutoPurge(Lists(0.5, 0.2), EightGb, DateTime.UtcNow, DateTime.MinValue));
        }

        [Fact]
        public void NoAutoPurge_WithinTheCooldown()
        {
            var now = DateTime.UtcNow;
            Assert.False(StandbyListManager.ShouldAutoPurge(Lists(0.5, 3), EightGb, now, now.AddSeconds(-30)));
            Assert.True(StandbyListManager.ShouldAutoPurge(Lists(0.5, 3), EightGb, now, now.AddSeconds(-61)));
        }

        [Fact]
        public void RealPageLists_AreEitherUnavailableOrPlausible()
        {
            // Reading them needs administrator rights; without them the result must be "unknown",
            // never a guess.
            var manager = new StandbyListManager();
            var lists = manager.GetMemoryLists();
            if (lists is MemoryLists l)
            {
                ulong total = manager.GetMemoryStatus().TotalPhys;
                Assert.InRange(l.StandbyBytes, 0, (long)total);
                Assert.InRange(l.UnusedBytes, 0, (long)total);
            }
        }
    }

    internal class FakeLatencySource : ILatencyCounterSource
    {
        public bool Available = true;
        public bool TryInitialize(out string? error)
        {
            error = Available ? null : "counters disabled";
            return Available;
        }
        public LatencySample Sample() => new(new double[2], new double[2], 0, 0, 0, 0);
        public void Dispose() { }
    }

    public class LatencyAuditorTests
    {
        private static LatencySample S(params (double dpc, double isr)[] cores) =>
            new(cores.Select(c => c.dpc).ToArray(), cores.Select(c => c.isr).ToArray(), 0, 0, 0, 0);

        private static LatencyAuditor NewAuditor() => new(() => new FakeLatencySource());

        [Fact]
        public void PeaksAndAverages_AreRealStatistics()
        {
            var auditor = NewAuditor();
            auditor.ApplySample(S((1, 0.5), (0, 0)));
            auditor.ApplySample(S((3, 1.5), (0, 0)));
            auditor.ApplySample(S((2, 1.0), (0, 0)));

            var cpu0 = auditor.GetReport().Cores.Single(c => c.Name == "CPU 0");
            Assert.Equal(3, cpu0.PeakDpcPercent);
            Assert.Equal(1.5, cpu0.PeakInterruptPercent);
            Assert.Equal(2, cpu0.AvgDpcPercent, 6);       // true mean, not (previous + new) / 2
            Assert.Equal(1, cpu0.AvgInterruptPercent, 6);
        }

        [Theory]
        [InlineData(1.0, 0.5, "OK", false)]
        [InlineData(2.5, 1.0, "ELEVATED", false)]
        [InlineData(8.0, 3.0, "HIGH", true)]
        public void Status_ReflectsTheMeasuredLoad(double dpc, double isr, string status, bool spike)
        {
            var auditor = NewAuditor();
            auditor.ApplySample(S((dpc, isr)));

            var core = auditor.GetReport().Cores.Single();
            Assert.Equal(status, core.Status);
            Assert.Equal(spike, core.IsSpike);
        }

        [Fact]
        public void IdleCores_DoNotSpike()
        {
            // The old auditor flagged every listed driver as spiking together whenever interrupt
            // load rose, because each driver's number was a fixed share of the same total.
            var auditor = NewAuditor();
            auditor.ApplySample(S((12, 2), (0.1, 0.1), (0.2, 0)));

            var report = auditor.GetReport();
            Assert.Single(report.Cores, c => c.IsSpike);
            Assert.Equal("CPU 0", report.Cores.First(c => c.IsSpike).Name);
        }

        [Fact]
        public void SpikeEvents_AreThrottled()
        {
            var auditor = NewAuditor();
            int events = 0;
            auditor.OnSpikeDetected += _ => events++;

            for (int i = 0; i < 60; i++) auditor.ApplySample(S((15, 0)));

            Assert.Equal(3, events); // 1st, 30th and 60th high readings
        }

        [Fact]
        public void Summary_NamesHighCoresAndIsHonestAboutAttribution()
        {
            var auditor = NewAuditor();
            auditor.Start();
            try
            {
                auditor.ApplySample(S((15, 0), (0, 0)));
                string summary = auditor.GetReport().Summary;
                Assert.Contains("CPU 0", summary);
                Assert.Contains("can't name the driver", summary);
            }
            finally
            {
                auditor.Stop();
            }
        }

        [Fact]
        public void HealthySummary_WhenLoadIsLow()
        {
            var auditor = NewAuditor();
            auditor.Start();
            try
            {
                auditor.ApplySample(S((0.5, 0.2)));
                Assert.Contains("healthy", auditor.GetReport().Summary);
            }
            finally
            {
                auditor.Stop();
            }
        }

        [Fact]
        public void UnavailableCounters_AreReportedNotFaked()
        {
            var auditor = new LatencyAuditor(() => new FakeLatencySource { Available = false });
            auditor.Start();

            var report = auditor.GetReport();
            Assert.False(report.IsRunning);
            Assert.False(report.CountersAvailable);
            Assert.Empty(report.Cores);
            Assert.Contains("unavailable", report.Summary);
        }

        [Fact]
        public void RealCounters_ReportOneEntryPerLogicalProcessor()
        {
            using var source = new PerformanceCounterLatencySource();
            if (!source.TryInitialize(out string? error))
            {
                Assert.False(string.IsNullOrEmpty(error)); // unavailable is acceptable, but must say why
                return;
            }

            var sample = source.Sample();
            Assert.Equal(sample.DpcPercent.Length, sample.InterruptPercent.Length);
            Assert.InRange(sample.DpcPercent.Length, 1, Environment.ProcessorCount);
            Assert.All(sample.DpcPercent, v => Assert.InRange(v, 0, 100));
        }
    }
}
