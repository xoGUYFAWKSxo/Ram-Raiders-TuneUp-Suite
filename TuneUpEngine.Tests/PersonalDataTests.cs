using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TuneUpEngine;
using TuneUpUI.Presentation;
using Xunit;

namespace TuneUpEngine.Tests
{
    // winapp2 lists saved passwords, cookies, sessions, form entries, synced tabs and history next to
    // its caches. "Select All" followed by "Empty Junk Files" used to delete them all with only a
    // generic "Permanently delete N items?" prompt. Entry names are from Winapp2.ini 260915.
    public class PersonalDataTests
    {
        private static readonly string[] PersonalEntries =
        {
            "Google Chrome Saved Usernames & Passwords *",
            "Microsoft Edge Web Browsing Cookies *",
            "Google Chrome Extension Cookies *",
            "Brave Web Browsing Session *",
            "Mozilla Firefox Web Browsing Session Backups *",
            "Mozilla Firefox Autofill Data *",
            "Waterfox Autocomplete History *",
            "Mozilla Firefox Synced Tabs *",
            "Google Chrome Web Browsing History *",
            "Vivaldi Download History *",
            "Windows Credential Manager Saved Credentials *",
        };

        private static readonly string[] JunkEntries =
        {
            "Google Chrome Caches *",
            "Mozilla Firefox Caches *",
            "Google Chrome Bookmark Favicons *",
            "Some App Logs *",
        };

        [Fact]
        public void Parser_MarksPersonalEntries_AndLeavesCachesUnmarked()
        {
            string dir = Path.Combine(Path.GetTempPath(), "RRTU_Personal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var lines = new List<string>();
                var all = PersonalEntries.Concat(JunkEntries).ToList();
                for (int i = 0; i < all.Count; i++)
                {
                    File.WriteAllText(Path.Combine(dir, $"f{i}.dat"), "x");
                    lines.AddRange(new[] { $"[{all[i]}]", "LangSecRef=3029", $"FileKey1={dir}|f{i}.dat" });
                }
                string ini = Path.Combine(dir, "winapp2.ini");
                File.WriteAllText(ini, string.Join(Environment.NewLine, lines));
                var parser = new Winapp2Parser(includeBuiltInRules: false);
                parser.LoadExternalWinapp2Ini(ini);

                var items = parser.Scan(ClientContext.ForCurrentProcess(), null, CancellationToken.None, null)
                    .ToDictionary(i => i.RuleName);
                Assert.Equal(all.Count, items.Count);
                foreach (var name in PersonalEntries) Assert.NotNull(items[name].PersonalData);
                foreach (var name in JunkEntries) Assert.Null(items[name].PersonalData);
                Assert.Equal("saved passwords", items["Google Chrome Saved Usernames & Passwords *"].PersonalData);
                Assert.Equal("saved passwords", items["Windows Credential Manager Saved Credentials *"].PersonalData);
                Assert.Equal("browsing history", items["Google Chrome Web Browsing History *"].PersonalData);
                Assert.All(items.Values, i => Assert.False(i.IsSelected));
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        }

        [Fact]
        public void AppName_DropsTheDataKind()
        {
            Assert.Equal("Google Chrome", PersonalBrowserData.AppName("Google Chrome Saved Usernames & Passwords *"));
            Assert.Equal("Maxthon", PersonalBrowserData.AppName("Maxthon Autofill Data & Search Engine Preferences *"));
            Assert.Equal("Mozilla Firefox", PersonalBrowserData.AppName("Mozilla Firefox Web Browsing Session Backups *"));
        }

        private static List<ScanItem> MixedResults() => new()
        {
            new() { RuleName = "Google Chrome Caches *", TotalSizeBytes = 1024 },
            new() { RuleName = "Google Chrome Saved Usernames & Passwords *", PersonalData = "saved passwords" },
            new() { RuleName = "Microsoft Edge Saved Usernames & Passwords *", PersonalData = "saved passwords" },
            new() { RuleName = "Microsoft Edge Web Browsing Cookies *", PersonalData = "website sign-ins and cookies" },
            new() { RuleName = "Recycle Bin", TotalSizeBytes = 2048 },
        };

        [Fact]
        public void SelectAll_LeavesPersonalDataUnchecked_AndDeselectAllClearsEverything()
        {
            var items = MixedResults();
            JunkSelection.SetAll(items, true);
            Assert.Equal(new[] { "Google Chrome Caches *", "Recycle Bin" }, items.Where(i => i.IsSelected).Select(i => i.RuleName));

            items[1].IsSelected = true; // ticked by hand
            JunkSelection.SetAll(items, false);
            Assert.All(items, i => Assert.False(i.IsSelected));
        }

        [Fact]
        public void Confirmation_NamesSelectedPersonalData()
        {
            var text = CleanConfirmation.Build(MixedResults());
            Assert.Contains("This deletes saved passwords for: Google Chrome, Microsoft Edge", text);
            Assert.Contains("This deletes website sign-ins and cookies for: Microsoft Edge", text);

            var junkOnly = CleanConfirmation.Build(MixedResults().Where(i => i.PersonalData == null).ToList());
            Assert.DoesNotContain("personal data", junkOnly);
        }
    }
}
