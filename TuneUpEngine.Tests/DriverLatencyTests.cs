using System;
using System.Linq;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // Feeds the trace aggregator known driver images and timed runs, the way the kernel session would.
    internal class FakeDriverTrace : IDriverTraceSource
    {
        public Action<DriverLatencyStats>? Feed;
        public string? Error;
        public bool Disposed;
        public long EventsLost => 0;

        public bool TryStart(DriverLatencyStats stats, out string? error)
        {
            error = Error;
            if (Error != null) return false;
            Feed?.Invoke(stats);
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    public class DriverLatencyTests
    {
        private const ulong Nvidia = 0xFFFFF80010000000UL;
        private const ulong Ndis = 0xFFFFF80020000000UL;

        private static DriverLatencyStats StatsWithDrivers()
        {
            var stats = new DriverLatencyStats();
            stats.AddImage(Nvidia, 0x100000, @"\SystemRoot\System32\DriverStore\FileRepository\nv.inf_amd64\nvlddmkm.sys");
            stats.AddImage(Ndis, 0x50000, @"\SystemRoot\System32\drivers\ndis.sys");
            return stats;
        }

        [Fact]
        public void Runs_AreChargedToTheDriverThatOwnsTheRoutine()
        {
            var stats = StatsWithDrivers();
            stats.Record(false, Nvidia + 0x1234, 120);
            stats.Record(false, Nvidia + 0x9000, 2500);
            stats.Record(true, Ndis + 0x10, 40);

            var drivers = stats.Snapshot(10);
            var nv = drivers.Single(d => d.Driver == "nvlddmkm.sys");
            Assert.Equal(2, nv.DpcCount);
            Assert.Equal(2500, nv.HighestMicroseconds, 6);
            Assert.Equal(2.62, nv.TotalMilliseconds, 6);
            Assert.Equal("HIGH", nv.Status);
            Assert.Equal("NVIDIA graphics driver", nv.Description);
            Assert.Equal("nvlddmkm.sys", drivers[0].Driver); // longest run first

            var ndis = drivers.Single(d => d.Driver == "ndis.sys");
            Assert.Equal(1, ndis.IsrCount);
            Assert.Equal(40, ndis.HighestIsrMicroseconds, 6);
            Assert.Equal("OK", ndis.Status);
        }

        [Fact]
        public void RoutineOutsideEveryImage_IsUnknown_NotGuessed()
        {
            var stats = StatsWithDrivers();
            stats.Record(false, Nvidia + 0x200000, 50);  // past the end of nvlddmkm.sys
            stats.Record(false, 0x1000, 50);             // below every image

            var only = stats.Snapshot(10).Single();
            Assert.Equal(DriverLatencyStats.UnknownDriver, only.Driver);
            Assert.Equal(2, only.DpcCount);
        }

        [Fact]
        public void SeededImageWithoutSize_StillOwnsItsRoutines()
        {
            // Drivers already loaded come from the driver list, which gives no sizes.
            var stats = new DriverLatencyStats();
            stats.AddImage(Ndis, 0, "ndis.sys");
            stats.Record(false, Ndis + 0x4000, 10);
            Assert.Equal("ndis.sys", stats.Snapshot(1).Single().Driver);

            // The trace's own image event adds the size.
            stats.AddImage(Ndis, 0x1000, "ndis.sys");
            stats.Record(false, Ndis + 0x4000, 10);
            Assert.Contains(stats.Snapshot(5), d => d.Driver == DriverLatencyStats.UnknownDriver);
        }

        [Fact]
        public void UnloadedDriver_NoLongerClaimsRoutines()
        {
            var stats = StatsWithDrivers();
            stats.Record(false, Ndis + 1, 10);
            stats.RemoveImage(Ndis);
            stats.Record(false, Ndis + 1, 10);

            Assert.Contains(stats.Snapshot(5), d => d.Driver == DriverLatencyStats.UnknownDriver);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(5_000_000)]
        [InlineData(double.NaN)]
        public void ImpossibleDurations_AreRejected(double microseconds)
        {
            var stats = StatsWithDrivers();
            stats.Record(false, Nvidia + 1, microseconds);

            Assert.Empty(stats.Snapshot(5));
            Assert.Equal(1, stats.RejectedReadings);
        }

        [Theory]
        [InlineData(499, "OK")]
        [InlineData(500, "ELEVATED")]
        [InlineData(1999, "ELEVATED")]
        [InlineData(2000, "HIGH")]
        public void Status_FollowsTheLongestSingleRun(double microseconds, string status)
        {
            var stats = StatsWithDrivers();
            stats.Record(false, Nvidia + 1, microseconds);
            stats.Record(false, Nvidia + 1, 5); // a later short run doesn't lower it
            Assert.Equal(status, stats.Snapshot(1).Single().Status);
        }

        [Fact]
        public void Catalog_DescribesKnownFamiliesAndFallsBack()
        {
            Assert.Equal("Intel Wi-Fi driver", DriverCatalog.Describe("Netwtw14.sys").Description);
            Assert.Equal("Third-party driver", DriverCatalog.Describe("somevendor.sys").Description);
        }

        private static LatencySample Quiet() => new(new double[] { 0.2 }, new double[] { 0.1 }, 0, 0, 0, 0);

        [Fact]
        public void Report_NamesTheDriverBehindALongRun_EvenWhenLoadIsLow()
        {
            var trace = new FakeDriverTrace
            {
                Feed = stats =>
                {
                    stats.AddImage(Nvidia, 0x100000, "nvlddmkm.sys");
                    stats.Record(false, Nvidia + 1, 3100);
                },
            };
            var auditor = new LatencyAuditor(() => new FakeLatencySource(), () => trace);
            auditor.Start();
            try
            {
                for (int i = 0; i < LatencyAuditor.WindowSamples; i++) auditor.ApplySample(Quiet());
                var report = auditor.GetReport();

                Assert.True(report.DriverTraceAvailable);
                Assert.Equal("nvlddmkm.sys", report.Drivers.Single().Driver);
                Assert.Contains("nvlddmkm.sys", report.Summary);
                Assert.Contains("3,100 µs", report.Summary);
            }
            finally
            {
                auditor.Stop();
            }
            Assert.True(trace.Disposed);
            Assert.Single(auditor.GetReport().Drivers); // results stay readable after Stop
        }

        [Fact]
        public void TraceFailure_IsReported_AndCountersStillWork()
        {
            var trace = new FakeDriverTrace { Error = "Access is denied." };
            var auditor = new LatencyAuditor(() => new FakeLatencySource(), () => trace);
            auditor.Start();
            try
            {
                for (int i = 0; i < LatencyAuditor.WindowSamples; i++) auditor.ApplySample(Quiet());
                var report = auditor.GetReport();

                Assert.True(report.IsRunning);
                Assert.False(report.DriverTraceAvailable);
                Assert.Contains("Access is denied.", report.DriverTraceMessage);
                Assert.Empty(report.Drivers);
                Assert.Contains("healthy", report.Summary);
                Assert.True(trace.Disposed);
            }
            finally
            {
                auditor.Stop();
            }
        }
    }
}
