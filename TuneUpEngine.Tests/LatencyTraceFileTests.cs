using System;
using System.Collections.Generic;
using System.Linq;
using TuneUpEngine;
using TuneUpUI.Presentation;
using Xunit;

namespace TuneUpEngine.Tests
{
    // The Live Monitor's "Save Trace" file contents.
    public class LatencyTraceFileTests
    {
        private static readonly DateTime SavedAt = new(2026, 10, 12, 14, 5, 9);

        private static LatencyReport Report(bool traced = true) => new()
        {
            SampleCount = 90,
            Summary = "Overall driver load is fine.",
            DriverTraceAvailable = traced,
            DriverTraceMessage = traced ? "" : "Couldn't start the driver trace: Access is denied.",
            Drivers = traced
                ? new List<DriverLatencyRecord>
                {
                    new() { Driver = "nvlddmkm.sys", Description = "NVIDIA graphics driver", Advice = "Reinstall it.",
                            HighestMicroseconds = 2450, DpcCount = 12000, IsrCount = 60, TotalMilliseconds = 823.9, Status = "HIGH" },
                    new() { Driver = "ndis.sys", Description = "Windows network stack", Advice = "Update it.",
                            HighestMicroseconds = 121, DpcCount = 680, TotalMilliseconds = 12.5, Status = "OK" },
                }
                : new List<DriverLatencyRecord>(),
            Cores = new List<CoreLatencyRecord>
            {
                new() { Name = "CPU 1", PeakTotalPercent = 6.5, PeakDpcPercent = 4.3, PeakInterruptPercent = 2.2, Status = "ELEVATED" },
            },
        };

        [Fact]
        public void Text_HasDriversCoresAndAdviceOnlyForProblemDrivers()
        {
            string text = LatencyTraceFile.ToText(Report(), SavedAt, "Microsoft Windows 10.0.26300");

            Assert.Contains("2026-10-12 14:05:09", text);
            Assert.Contains("90 seconds", text);
            Assert.Contains("nvlddmkm.sys", text);
            Assert.Contains("2,450", text);
            Assert.Contains("12,060", text);               // DPC + ISR runs
            Assert.Contains("CPU 1", text);
            Assert.Contains("nvlddmkm.sys (NVIDIA graphics driver): Reinstall it.", text);
            Assert.DoesNotContain("Update it.", text);      // ndis.sys is OK
            Assert.DoesNotContain(Environment.MachineName, text);
        }

        [Fact]
        public void Text_SaysWhyThereAreNoDrivers()
        {
            string text = LatencyTraceFile.ToText(Report(traced: false), SavedAt, "Windows");
            Assert.Contains("Access is denied.", text);
        }

        [Fact]
        public void Csv_HasOneRowPerDriverAndCore_InInvariantNumbers()
        {
            var lines = LatencyTraceFile.ToCsv(Report()).TrimEnd().Split(Environment.NewLine);

            Assert.Equal(4, lines.Length);
            Assert.StartsWith("Type,Name,", lines[0]);
            Assert.Equal("Driver,nvlddmkm.sys,NVIDIA graphics driver,2450.0,12060,12000,60,823.900,,,,HIGH", lines[1]);
            Assert.Equal("Core,CPU 1,,,,,,,6.50,4.30,2.20,ELEVATED", lines[3]);
        }

        [Fact]
        public void Csv_QuotesCommasAndDefusesFormulas()
        {
            var report = Report();
            report.Drivers[0].Description = "Graphics, NVIDIA";
            report.Drivers[1].Driver = "=cmd.sys";

            string csv = LatencyTraceFile.ToCsv(report);
            Assert.Contains("\"Graphics, NVIDIA\"", csv);
            Assert.Contains(",'=cmd.sys,", csv);
        }

        [Fact]
        public void DefaultFileName_IsDatedAndSafeForWindows()
        {
            string name = LatencyTraceFile.DefaultFileName(SavedAt);
            Assert.Equal("DPC trace 2026-10-12 1405", name);
            Assert.True(name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0);
        }
    }
}
