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
        // No cores, so the sampling thread Start() launches can't race the readings a test feeds in.
        public LatencySample Sample() => new(Array.Empty<double>(), Array.Empty<double>(), 0, 0, 0, 0);
        public void Dispose() { }
    }

    public class LatencyAuditorTests
    {
        private static LatencySample S(params (double dpc, double isr)[] cores) =>
            new(cores.Select(c => c.dpc).ToArray(), cores.Select(c => c.isr).ToArray(), 0, 0, 0, 0);

        private static LatencyAuditor NewAuditor() => new(() => new FakeLatencySource());

        private static void Fill(LatencyAuditor auditor, LatencySample sample)
        {
            for (int i = 0; i < LatencyAuditor.WindowSamples; i++) auditor.ApplySample(sample);
        }

        [Fact]
        public void PeaksAndAverages_AreRealStatistics()
        {
            var auditor = NewAuditor();
            Fill(auditor, S((1, 0.5), (0, 0)));
            Fill(auditor, S((3, 1.5), (0, 0)));
            Fill(auditor, S((2, 1.0), (0, 0)));

            var cpu0 = auditor.GetReport().Cores.Single(c => c.Name == "CPU 0");
            Assert.Equal(4.5, cpu0.PeakTotalPercent, 6);
            Assert.Equal(3, cpu0.PeakDpcPercent, 6);
            Assert.Equal(1.5, cpu0.PeakInterruptPercent, 6);
            Assert.Equal(2, cpu0.AvgDpcPercent, 6);       // true mean, not (previous + new) / 2
            Assert.Equal(1, cpu0.AvgInterruptPercent, 6);
        }

        [Fact]
        public void ImpossibleReadings_AreClampedToACoresTime()
        {
            // A rate read right after the priming read returned 772% on a CI machine.
            var auditor = NewAuditor();
            Fill(auditor, S((772.3, -5)));

            var core = auditor.GetReport().Cores.Single();
            Assert.Equal(100, core.PeakDpcPercent, 6);
            Assert.Equal(0, core.PeakInterruptPercent, 6);
        }

        [Fact]
        public void SingleClockTick_DoesNotFlagACore()
        {
            // The counters move one 15.6 ms tick (1.5625% of a second) at a time. In the user's
            // screenshot two stray ticks marked an idle core ELEVATED and every core read "1.6".
            var auditor = NewAuditor();
            auditor.ApplySample(S((3.125, 0)));
            for (int i = 1; i < LatencyAuditor.WindowSamples; i++) auditor.ApplySample(S((0, 0)));

            var core = auditor.GetReport().Cores.Single();
            Assert.Equal("OK", core.Status);
            Assert.Equal(0.625, core.PeakTotalPercent, 6);
        }

        [Fact]
        public void PeakParts_ComeFromTheSameMoment()
        {
            // Peak DPC and peak ISR used to be tracked separately and summed for the status, so
            // two unrelated readings could add up to HIGH. The parts now always sum to the total.
            var auditor = NewAuditor();
            Fill(auditor, S((8, 0)));
            Fill(auditor, S((0, 8)));

            var core = auditor.GetReport().Cores.Single();
            Assert.Equal("ELEVATED", core.Status);
            Assert.Equal(core.PeakTotalPercent, core.PeakDpcPercent + core.PeakInterruptPercent, 6);
        }

        [Fact]
        public void Summary_WaitsForAFullWindow()
        {
            var auditor = NewAuditor();
            auditor.Start();
            try
            {
                auditor.ApplySample(S((50, 0)));
                var report = auditor.GetReport();
                Assert.Contains("Measuring", report.Summary);
                Assert.Equal("OK", report.Cores.First(c => c.Name == "CPU 0").Status);
            }
            finally
            {
                auditor.Stop();
            }
        }

        [Theory]
        [InlineData(1.0, 0.5, "OK", false)]
        [InlineData(2.5, 1.0, "ELEVATED", false)]
        [InlineData(8.0, 3.0, "HIGH", true)]
        public void Status_ReflectsTheMeasuredLoad(double dpc, double isr, string status, bool spike)
        {
            var auditor = NewAuditor();
            Fill(auditor, S((dpc, isr)));

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
            Fill(auditor, S((12, 2), (0.1, 0.1), (0.2, 0)));

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

            for (int i = 0; i < LatencyAuditor.WindowSamples - 1 + 60; i++) auditor.ApplySample(S((15, 0)));

            Assert.Equal(3, events); // 1st, 30th and 60th high readings
        }

        [Fact]
        public void Summary_NamesHighCoresAndIsHonestAboutAttribution()
        {
            var auditor = NewAuditor();
            auditor.Start();
            try
            {
                Fill(auditor, S((15, 0), (0, 0)));
                string summary = auditor.GetReport().Summary;
                Assert.Contains("CPU 0", summary);
                Assert.Contains("can't tell which driver", summary);
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
                Fill(auditor, S((0.5, 0.2)));
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

            System.Threading.Thread.Sleep(1000); // the auditor's real sampling interval
            var sample = source.Sample();
            Assert.Equal(sample.DpcPercent.Length, sample.InterruptPercent.Length);
            Assert.InRange(sample.DpcPercent.Length, 1, Environment.ProcessorCount);
            Assert.All(sample.DpcPercent, v => Assert.InRange(v, 0, 100));
        }
    }
}
