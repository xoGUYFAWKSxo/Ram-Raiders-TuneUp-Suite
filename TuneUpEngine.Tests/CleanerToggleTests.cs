using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // The Cleaner tab's "Scan Temp Files", "Scan Windows Update Cache" and "Scan Browser Cache"
    // switches decide which rules the junk scan runs (the browser switch also covers winapp2's
    // browser entries). Recycle Bin and other winapp2 entries always run. The progress callback names every rule the scan considers,
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

        // Entries modelled on Winapp2.ini 260915: a per-browser LangSecRef group, a browser named by
        // its Section line, a one-entry browser in the Internet group, and entries that must stay
        // outside the switch.
        [Fact]
        public void BrowserSwitch_CoversWinapp2BrowserEntries()
        {
            string dir = Path.Combine(Path.GetTempPath(), "RRTU_Toggle_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string ini = Path.Combine(dir, "winapp2.ini");
                File.WriteAllText(ini, string.Join(Environment.NewLine,
                    "[Google Chrome Caches *]", "LangSecRef=3029", $"FileKey1={dir}|*.tmp",
                    "[Zen Caches *]", "Section=Zen Web Browser", $"FileKey1={dir}|*.tmp",
                    "[NetSurf Browser *]", "LangSecRef=3022", $"FileKey1={dir}|*.tmp",
                    "[Thunderbird Caches *]", "LangSecRef=3030", $"FileKey1={dir}|*.tmp",
                    "[DB Browser for SQLite *]", "LangSecRef=3024", $"FileKey1={dir}|*.tmp",
                    "[Some App Logs *]", "LangSecRef=3021", $"FileKey1={dir}|*.log"));
                var parser = new Winapp2Parser(includeBuiltInRules: false);
                parser.LoadExternalWinapp2Ini(ini);

                List<string> Scanned(bool browser)
                {
                    var names = new List<string>();
                    parser.Scan(ClientContext.ForCurrentProcess(), (rule, _) => { if (rule != "Scan Complete") names.Add(rule); },
                        CancellationToken.None, new ScanJunkRequest(true, true, browser));
                    return names;
                }

                Assert.Equal(new[] { "Google Chrome Caches *", "Zen Caches *", "NetSurf Browser *", "Thunderbird Caches *",
                    "DB Browser for SQLite *", "Some App Logs *" }, Scanned(true));
                Assert.Equal(new[] { "Thunderbird Caches *", "DB Browser for SQLite *", "Some App Logs *" }, Scanned(false));
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
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
