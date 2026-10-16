using System.Collections.Generic;
using System.Threading;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // The Cleaner tab's "Scan Temp Files", "Scan Windows Update Cache" and "Scan Browser Cache"
    // switches decide which built-in rules the junk scan runs. Rules outside those groups (Recycle
    // Bin, winapp2 entries) always run. The progress callback names every rule the scan considers,
    // so these tests don't depend on which junk files exist on the machine.
    public class CleanerToggleTests
    {
        private static readonly string[] TempRules =
            { "Windows Temporary Files", "Windows Error Reporting & Logs", "DirectX Shader Cache", "Prefetch Data" };
        private const string UpdateRule = "Delivery Optimization Files";
        private static readonly string[] BrowserRules = { "Google Chrome - Cache", "Microsoft Edge - Cache" };

        private static List<string> RulesScanned(ScanJunkRequest? groups)
        {
            var names = new List<string>();
            new Winapp2Parser().Scan(ClientContext.ForCurrentProcess(), (rule, _) => { if (rule != "Scan Complete") names.Add(rule); },
                CancellationToken.None, groups);
            return names;
        }

        [Fact]
        public void AllSwitchesOff_ScansOnlyUngroupedRules()
        {
            var names = RulesScanned(new ScanJunkRequest(false, false, false));
            Assert.Equal(new[] { "Recycle Bin" }, names);
        }

        [Fact]
        public void EachSwitch_ScansOnlyItsOwnRules()
        {
            var temp = RulesScanned(new ScanJunkRequest(true, false, false));
            foreach (var r in TempRules) Assert.Contains(r, temp);
            Assert.DoesNotContain(UpdateRule, temp);
            foreach (var r in BrowserRules) Assert.DoesNotContain(r, temp);

            var update = RulesScanned(new ScanJunkRequest(false, true, false));
            Assert.Equal(new[] { UpdateRule, "Recycle Bin" }, update);

            var browser = RulesScanned(new ScanJunkRequest(false, false, true));
            Assert.Equal(new[] { BrowserRules[0], BrowserRules[1], "Recycle Bin" }, browser);
        }

        [Fact]
        public void NoPayload_ScansEveryRule()
        {
            Assert.Equal(8, RulesScanned(null).Count);
            Assert.Equal(RulesScanned(null), RulesScanned(new ScanJunkRequest(true, true, true)));
        }

        [Fact]
        public void Request_RoundTripsThroughThePipePayload()
        {
            var sent = new ScanJunkRequest(true, false, true);
            Assert.Equal(sent, IpcPayload.Deserialize<ScanJunkRequest>(IpcPayload.Serialize(sent)));
            Assert.Null(IpcPayload.Deserialize<ScanJunkRequest>(""));
        }
    }
}
