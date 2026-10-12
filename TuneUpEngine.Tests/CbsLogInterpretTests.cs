using System.Collections.Generic;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // The Component Repair tab showed raw CBS.log lines, and a successful DISM repair
    // ("Total Repaired Corruption: 876") was reported as "System corruption detected!".
    public class CbsLogInterpretTests
    {
        private static List<string> DismSummary(string operation, string result, int detected, int fileFlags, int repaired) => new()
        {
            "2026-10-12 08:31:27, Info                  CSI    00000191 StoreCorruptionRepair transaction completed.",
            "2026-10-12 08:31:27, Info                  CBS    Checking System Update Readiness.",
            "2026-10-12 08:31:27, Info                  CBS    Summary:",
            $"2026-10-12 08:31:27, Info                  CBS    Operation: {operation} ",
            $"2026-10-12 08:31:27, Info                  CBS    Operation result: {result}",
            $"2026-10-12 08:31:27, Info                  CBS    Total Detected Corruption:\t{detected}",
            "2026-10-12 08:31:27, Info                  CBS    \tCSI Payload Corruption:\t0",
            $"2026-10-12 08:31:27, Info                  CBS    \tCSI FileFlags Corrupt:\t{fileFlags}",
            $"2026-10-12 08:31:27, Info                  CBS    Total Repaired Corruption:\t{repaired}",
            "2026-10-12 08:31:27, Info                  CBS    Total Operation Time: 430 seconds.",
            "2026-10-12 08:31:27, Info                  CBS    Exec: Processing complete, session(Corruption Repairing): 31283780_2576168021 [HRESULT = 0x00000000 - S_OK]",
        };

        private static List<string> SfcRun(int repairing, params string[] cannotRepair)
        {
            var lines = new List<string>
            {
                "2026-10-12 08:32:43, Info                  CSI    000003cc [SR] Verify complete",
                $"2026-10-12 08:32:43, Info                  CSI    000003cd [SR] Repairing {repairing} components",
                "2026-10-12 08:32:43, Info                  CSI    000003ce [SR] Beginning Verify and Repair transaction",
            };
            foreach (var f in cannotRepair)
                lines.Add($"2026-10-12 08:32:43, Info                  CSI    000003cf [SR] Cannot repair member file [l:20]\"Amd64\\{f}\" of prncacla.inf, version 10.0");
            lines.Add("2026-10-12 08:32:43, Info                  CSI    000003d0 [SR] Repair complete");
            return lines;
        }

        [Fact]
        public void SuccessfulRepair_IsNotReportedAsCorruption()
        {
            var lines = DismSummary("Detect and Repair", "0x0", 876, 876, 876);
            lines.AddRange(SfcRun(0));

            var r = SystemIntegrityAgent.Interpret(lines);

            Assert.False(r.HasCorruption);
            Assert.Equal(2, r.CorruptionLogs.Count);
            Assert.Equal("On 12 Oct 2026 at 08:31, Windows checked its backup copy of system files and fixed all 876 problems it found."
                + " They were minor file-setting mismatches, not damaged files. Nothing is left to fix.", r.CorruptionLogs[0]);
            Assert.Equal("On 12 Oct 2026 at 08:32, Windows checked its own program files. No damaged files found.", r.CorruptionLogs[1]);
        }

        [Fact]
        public void ScanOnlyWithProblems_IsCorruption()
        {
            var r = SystemIntegrityAgent.Interpret(DismSummary("Detect only", "0x0", 3, 0, 0));
            Assert.True(r.HasCorruption);
            Assert.Contains("found 3 problems. They haven't been fixed yet.", r.CorruptionLogs[0]);
        }

        [Fact]
        public void PartialRepair_IsCorruption()
        {
            var r = SystemIntegrityAgent.Interpret(DismSummary("Detect and Repair", "0x0", 5, 0, 2));
            Assert.True(r.HasCorruption);
            Assert.Contains("could only fix 2. 3 still need fixing.", r.CorruptionLogs[0]);
        }

        [Fact]
        public void FailedDismRun_AsksToRunAgain()
        {
            var r = SystemIntegrityAgent.Interpret(DismSummary("Detect and Repair", "0x800f081f", 0, 0, 0));
            Assert.True(r.HasCorruption);
            Assert.Contains("didn't finish (error 0x800f081f)", r.CorruptionLogs[0]);
        }

        [Fact]
        public void SfcUnfixableFiles_AreNamed()
        {
            var r = SystemIntegrityAgent.Interpret(SfcRun(2, "CNBJ2530.DPB", "CNBJ2530.DPB", "foo.dll"));
            Assert.True(r.HasCorruption);
            Assert.Contains("found 2 damaged files it couldn't fix: CNBJ2530.DPB, foo.dll.", r.CorruptionLogs[0]);
        }

        [Fact]
        public void OnlyLatestSfcRunCounts()
        {
            var lines = SfcRun(1, "old.dll");
            lines.AddRange(SfcRun(0));
            var r = SystemIntegrityAgent.Interpret(lines);
            Assert.False(r.HasCorruption);
            Assert.Contains("No damaged files found.", r.CorruptionLogs[0]);
        }

        [Fact]
        public void NoRecentChecks_SaysSo()
        {
            var r = SystemIntegrityAgent.Interpret(new[] { "2026-10-12 08:00:00, Info CBS Something unrelated" });
            Assert.False(r.HasCorruption);
            Assert.Contains("hasn't checked its system files recently", r.CorruptionLogs[0]);
        }
    }
}
