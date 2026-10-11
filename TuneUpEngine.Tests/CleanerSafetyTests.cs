using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // Locks in the Phase 0 cleaner safety fixes: a duplicate set always keeps one copy, winapp2
    // ExcludeKeys are honoured, re-loading definitions doesn't stack rules, and the elevated scan
    // never follows a junction out of the folder it was pointed at.
    public class CleanerSafetyTests : IDisposable
    {
        private readonly string _dir;

        public CleanerSafetyTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "RRTU_Cleaner_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            // Remove junctions first so a recursive delete can never reach their targets.
            foreach (var d in Directory.GetDirectories(_dir, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                    try { Directory.Delete(d); } catch { }
            }
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static ScanItem DuplicateSet(params string[] paths) => new ScanItem
        {
            Category = AdvancedScanner.DuplicateFilesCategory,
            RuleName = "Duplicate Set",
            FilePaths = paths.ToList()
        };

        [Fact]
        public void DuplicateSet_KeepsFirstCopy()
        {
            var targets = Winapp2Parser.GetDeletionTargets(DuplicateSet(@"C:\a\one.jpg", @"C:\b\one.jpg", @"C:\c\one.jpg")).ToList();
            Assert.Equal(new[] { @"C:\b\one.jpg", @"C:\c\one.jpg" }, targets);
        }

        [Fact]
        public void DuplicateSet_OfTheSameFileListedTwice_DeletesNothing()
        {
            // The old scanner indexed nested folders twice, producing exactly this "set".
            var targets = Winapp2Parser.GetDeletionTargets(DuplicateSet(@"C:\Users\x\Documents\cv.docx", @"C:\USERS\X\Documents\cv.docx"));
            Assert.Empty(targets);
        }

        [Fact]
        public void NonDuplicateItem_DeletesAllListedPaths()
        {
            var item = new ScanItem { Category = "System", FilePaths = new List<string> { @"C:\t\1.tmp", @"C:\t\2.tmp" } };
            Assert.Equal(2, Winapp2Parser.GetDeletionTargets(item).Count());
        }

        [Fact]
        public void Clean_DuplicateSet_LeavesOriginalOnDisk()
        {
            string a = Path.Combine(_dir, "a.bin");
            string b = Path.Combine(_dir, "b.bin");
            File.WriteAllBytes(a, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(b, new byte[] { 1, 2, 3 });

            new Winapp2Parser().Clean(new List<ScanItem> { DuplicateSet(a, b) });

            Assert.True(File.Exists(a));
            Assert.False(File.Exists(b));
        }

        [Fact]
        public void ExcludeKey_PathRule_CoversSubfolders()
        {
            var parser = new Winapp2Parser();
            var rule = parser.ParseExcludeKey($@"PATH|{_dir}\keep\")!;
            Assert.NotNull(rule);
            Assert.True(Winapp2Parser.IsExcluded(Path.Combine(_dir, "keep", "deep", "x.dat"), new[] { rule }));
            Assert.False(Winapp2Parser.IsExcluded(Path.Combine(_dir, "other", "x.dat"), new[] { rule }));
        }

        [Fact]
        public void ExcludeKey_FileRule_MatchesPatternInThatFolderOnly()
        {
            var parser = new Winapp2Parser();
            var rule = parser.ParseExcludeKey($@"FILE|{_dir}\|*.sqlite;prefs.js")!;
            Assert.True(Winapp2Parser.IsExcluded(Path.Combine(_dir, "cookies.sqlite"), new[] { rule }));
            Assert.True(Winapp2Parser.IsExcluded(Path.Combine(_dir, "prefs.js"), new[] { rule }));
            Assert.False(Winapp2Parser.IsExcluded(Path.Combine(_dir, "cache.tmp"), new[] { rule }));
            Assert.False(Winapp2Parser.IsExcluded(Path.Combine(_dir, "sub", "cookies.sqlite"), new[] { rule }));
        }

        [Fact]
        public void ExcludeKey_RegistryRules_AreIgnored()
        {
            Assert.Null(new Winapp2Parser().ParseExcludeKey(@"REG|HKCU\Software\Foo"));
        }

        private string WriteIni(string ruleName, string body)
        {
            string ini = Path.Combine(_dir, "winapp2.ini");
            File.WriteAllText(ini, $"[{ruleName}]\n{body}\n");
            return ini;
        }

        private static List<ScanItem> ScanRule(Winapp2Parser parser, string ruleName) =>
            parser.Scan().Where(i => i.RuleName == ruleName).ToList();

        [Fact]
        public void Scan_SkipsExcludedFiles()
        {
            string cache = Directory.CreateDirectory(Path.Combine(_dir, "cache")).FullName;
            File.WriteAllText(Path.Combine(cache, "junk.tmp"), "x");
            File.WriteAllText(Path.Combine(cache, "settings.json"), "keep me");

            string rule = "RRTU Test Exclude " + Guid.NewGuid().ToString("N");
            var parser = new Winapp2Parser(includeBuiltInRules: false);
            parser.LoadExternalWinapp2Ini(WriteIni(rule,
                $"FileKey1={cache}|*.*\nExcludeKey1=FILE|{cache}\\|settings.json"));

            var item = Assert.Single(ScanRule(parser, rule));
            Assert.Contains(item.FilePaths, p => p.EndsWith("junk.tmp"));
            Assert.DoesNotContain(item.FilePaths, p => p.EndsWith("settings.json"));
        }

        [Fact]
        public void ReloadingDefinitions_DoesNotDuplicateRules()
        {
            string cache = Directory.CreateDirectory(Path.Combine(_dir, "cache2")).FullName;
            File.WriteAllText(Path.Combine(cache, "junk.tmp"), "x");

            string rule = "RRTU Test Reload " + Guid.NewGuid().ToString("N");
            string ini = WriteIni(rule, $"FileKey1={cache}|*.*");
            var parser = new Winapp2Parser(includeBuiltInRules: false);
            parser.LoadExternalWinapp2Ini(ini);
            parser.LoadExternalWinapp2Ini(ini);

            Assert.Single(ScanRule(parser, rule));
        }

        [Fact]
        public void Scan_DoesNotFollowJunctions()
        {
            string outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "precious.txt"), "not junk");

            string cache = Directory.CreateDirectory(Path.Combine(_dir, "cache3")).FullName;
            File.WriteAllText(Path.Combine(cache, "junk.tmp"), "x");
            string link = Path.Combine(cache, "link");
            CreateJunction(link, outside);

            string rule = "RRTU Test Junction " + Guid.NewGuid().ToString("N");
            var parser = new Winapp2Parser(includeBuiltInRules: false);
            parser.LoadExternalWinapp2Ini(WriteIni(rule, $"FileKey1={cache}|*.*|RECURSE"));

            var item = Assert.Single(ScanRule(parser, rule));
            Assert.Contains(item.FilePaths, p => p.EndsWith("junk.tmp"));
            Assert.DoesNotContain(item.FilePaths, p => p.EndsWith("precious.txt"));
        }

        // Directory junctions don't need admin rights or Developer Mode (unlike symlinks).
        private static void CreateJunction(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);
            using var p = Process.Start(psi);
            p!.WaitForExit(10000);
            Assert.True(Directory.Exists(link), "Failed to create test junction");
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        }
    }
}
