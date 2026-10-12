using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    public class Winapp2CoverageTests : IDisposable
    {
        private readonly string _dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "RRTU_W2_" + Guid.NewGuid().ToString("N"))).FullName;
        private readonly string _regKey = @"Software\RRTU_Test_" + Guid.NewGuid().ToString("N");

        public void Dispose()
        {
            // Remove junctions first (top-down, never looking inside one) so the recursive delete
            // can't reach their targets.
            foreach (var d in Directory.GetDirectories(_dir))
            {
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d);
                }
                catch { }
            }
            try { Directory.Delete(_dir, recursive: true); } catch { }
            Registry.CurrentUser.DeleteSubKeyTree(_regKey, throwOnMissingSubKey: false);
        }

        private string MakeDir(params string[] parts) => Directory.CreateDirectory(Path.Combine(new[] { _dir }.Concat(parts).ToArray())).FullName;

        private string MakeFile(string content, params string[] parts)
        {
            string path = Path.Combine(new[] { _dir }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private List<ScanItem> ScanIni(string ini)
        {
            string file = Path.Combine(_dir, "rules.ini");
            File.WriteAllText(file, ini);
            var parser = new Winapp2Parser(includeBuiltInRules: false);
            parser.LoadExternalWinapp2Ini(file);
            return parser.Scan();
        }

        [Fact]
        public void ProgramFiles_ExpandsToBothProgramFilesFolders()
        {
            var paths = Winapp2Parser.ExpandTokens(@"%ProgramFiles%\Vendor", UserFolders.ForCurrentProcess());
            Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vendor"), paths);
            Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Vendor"), paths);
        }

        [Fact]
        public void TokensThatUsedToBeIgnored_AreExpanded()
        {
            var f = UserFolders.ForCurrentProcess();
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "X"),
                Winapp2Parser.ExpandTokens(@"%ProgramData%\X", f).Single());
            Assert.Equal(Path.Combine(f.Profile, "AppData", "LocalLow", "X"),
                Winapp2Parser.ExpandTokens(@"%LocalLowAppData%\X", f).Single());
            Assert.DoesNotContain("%", Winapp2Parser.ExpandTokens(@"%SystemDrive%\X", f).Single());
        }

        [Fact]
        public void WildcardFolders_ExpandToEveryMatchingFolder()
        {
            MakeDir("User Data", "Default", "Cache");
            MakeDir("User Data", "Profile 1", "Cache");
            MakeDir("User Data", "Profile 2"); // no Cache folder

            var dirs = Winapp2Parser.ExpandWildcardDirectories(Path.Combine(_dir, "User Data", "*", "Cache"), allowLinksInFixedSegments: true);

            Assert.Equal(2, dirs.Count);
        }

        [Fact]
        public void WildcardFolders_NeverMatchLinks()
        {
            MakeDir("real", "Cache");
            string outside = MakeDir("outside", "Cache");
            CreateJunction(Path.Combine(_dir, "link"), Path.GetDirectoryName(outside)!);

            var dirs = Winapp2Parser.ExpandWildcardDirectories(Path.Combine(_dir, "*", "Cache"), allowLinksInFixedSegments: true);

            Assert.DoesNotContain(dirs, d => d.Contains(@"\link\", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void OutsideTheProfile_FixedLinksAreNotFollowedEither()
        {
            string target = MakeDir("target", "Logs");
            CreateJunction(Path.Combine(_dir, "planted"), Path.GetDirectoryName(target)!);

            Assert.Empty(Winapp2Parser.ExpandWildcardDirectories(Path.Combine(_dir, "planted", "Logs"), allowLinksInFixedSegments: false));
            Assert.Single(Winapp2Parser.ExpandWildcardDirectories(Path.Combine(_dir, "planted", "Logs"), allowLinksInFixedSegments: true));
        }

        [Fact]
        public void DetectRegistryKey_DetectsTheRule()
        {
            Registry.CurrentUser.CreateSubKey(_regKey).Dispose();
            MakeFile("x", "app", "cache.tmp");

            var items = ScanIni($"[Present]\nDetect=HKCU\\{_regKey}\nFileKey1={_dir}\\app|*.tmp\n\n" +
                                $"[Absent]\nDetect=HKCU\\{_regKey}_missing\nFileKey1={_dir}\\app|*.tmp\n");

            Assert.Single(items, i => i.RuleName == "Present");
            Assert.DoesNotContain(items, i => i.RuleName == "Absent");
        }

        [Fact]
        public void NumberedDetectFile_WithWildcards_IsHonoured()
        {
            MakeFile("x", "AppV2", "cache", "a.tmp");
            var items = ScanIni($"[Numbered]\nDetectFile1={_dir}\\Missing\nDetectFile2={_dir}\\App*\nFileKey1={_dir}\\App*\\cache|*.tmp\n");
            Assert.Single(items);
        }

        [Fact]
        public void WarningSectionAndCategory_AreCarriedThrough()
        {
            MakeFile("x", "w", "a.tmp");
            var items = ScanIni($"[Warned]\nLangSecRef=3021\nSection=Games\nWarning=This will also delete settings.\nFileKey1={_dir}\\w|*.tmp\n");

            var item = Assert.Single(items);
            Assert.Equal("Games", item.Category);
            Assert.Equal("This will also delete settings.", item.Warning);
        }

        [Fact]
        public void RemoveSelf_ListsFilesThenFoldersDeepestFirst_AndCleanRemovesTheFolder()
        {
            MakeFile("x", "TempState", "a.tmp");
            MakeFile("y", "TempState", "sub", "b.bin");
            var items = ScanIni($"[Self]\nFileKey1={_dir}\\TempState|*|REMOVESELF\n");

            var item = Assert.Single(items);
            int deeper = item.FilePaths.IndexOf(Path.Combine(_dir, "TempState", "sub"));
            int folder = item.FilePaths.IndexOf(Path.Combine(_dir, "TempState"));
            Assert.True(deeper >= 0 && folder > deeper);

            new Winapp2Parser(includeBuiltInRules: false).Clean(items);
            Assert.False(Directory.Exists(Path.Combine(_dir, "TempState")));
        }

        [Fact]
        public void RemoveSelf_KeepsAFolderThatStillHasExcludedFiles()
        {
            MakeFile("keep", "Data", "settings.json");
            MakeFile("junk", "Data", "x.tmp");
            var items = ScanIni($"[Self]\nFileKey1={_dir}\\Data|*|REMOVESELF\nExcludeKey1=FILE|{_dir}\\Data\\|settings.json\n");

            new Winapp2Parser(includeBuiltInRules: false).Clean(items);

            Assert.True(File.Exists(Path.Combine(_dir, "Data", "settings.json")));
            Assert.False(File.Exists(Path.Combine(_dir, "Data", "x.tmp")));
        }

        [Fact]
        public void RemoveSelf_NeverRemovesAKeyFolder()
        {
            // A rule pointing REMOVESELF at %Temp% itself must not schedule the Temp folder for removal.
            string probe = MakeFile("x", "probe.tmp");
            string ini = Path.Combine(_dir, "rules.ini");
            File.WriteAllText(ini, "[TempSelf]\nFileKey1=%Temp%|probe-does-not-exist.none|REMOVESELF\n");
            var parser = new Winapp2Parser(includeBuiltInRules: false);
            parser.LoadExternalWinapp2Ini(ini);

            var temp = Path.TrimEndingDirectorySeparator(UserFolders.ForCurrentProcess().Temp);
            Assert.DoesNotContain(parser.Scan().SelectMany(i => i.FilePaths), p => p.Equals(temp, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(probe));
        }

        [Fact]
        public void EventLogsAndRegistryHives_AreNeverCleaned()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Assert.True(Winapp2Parser.IsNeverClean(Path.Combine(windows, "System32", "winevt", "Logs", "Security.evtx")));
            Assert.True(Winapp2Parser.IsNeverClean(Path.Combine(windows, "Logs", "CBS", "CBS.log")));
            Assert.True(Winapp2Parser.IsNeverClean(Path.Combine(windows, "System32", "config", "SAM")));
            Assert.False(Winapp2Parser.IsNeverClean(Path.Combine(windows, "Logs", "DISM", "dism.log")));
        }

        [Fact]
        public void WildcardExclusions_Protect_MatchingFiles()
        {
            var rules = Winapp2Parser.ParseExcludeKeys($@"PATH|{_dir}\plugins\*\|*.db", UserFolders.ForCurrentProcess());
            Assert.True(Winapp2Parser.IsExcluded(Path.Combine(_dir, "plugins", "a1", "store.db"), rules));
            Assert.False(Winapp2Parser.IsExcluded(Path.Combine(_dir, "plugins", "a1", "cache.tmp"), rules));
        }

        [Fact]
        public void OverlappingRules_DoNotListTheSameFileTwice()
        {
            MakeFile("x", "shared", "a.tmp");
            var items = ScanIni($"[First]\nFileKey1={_dir}\\shared|*.tmp\n\n[Second]\nFileKey1={_dir}\\shared|*\n");
            Assert.Equal(1, items.Sum(i => i.FilePaths.Count));
        }

        internal static void CreateJunction(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            Assert.True(Directory.Exists(link));
        }
    }

    public class MigrationSafetyTests : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "RRTU_Mig_" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            string src = Path.Combine(_root, "GameSrc");
            if (Directory.Exists(src) && (File.GetAttributes(src) & FileAttributes.ReparsePoint) != 0)
                try { Directory.Delete(src); } catch { }
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void Migrate_EitherCompletesOrChangesNothing_AndUndoRestores()
        {
            string source = Path.Combine(_root, "GameSrc");
            Directory.CreateDirectory(Path.Combine(source, "data"));
            File.WriteAllText(Path.Combine(source, "game.exe"), "binary");
            File.WriteAllText(Path.Combine(source, "data", "level1.pak"), "level");
            string destination = Path.Combine(_root, "Elsewhere", "GameSrc");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            string result = StorageMigrator.Migrate(source, destination, (_, _) => { });

            // Nothing may be left half-done, whichever way it went.
            Assert.Empty(Directory.GetDirectories(_root, "GameSrc.rrtu-*"));

            if (!result.StartsWith("Success"))
            {
                // Creating links needs admin rights or Developer Mode. Then nothing must change.
                Assert.Contains("Nothing was changed", result);
                Assert.Equal(0, (int)(File.GetAttributes(source) & FileAttributes.ReparsePoint));
                Assert.True(File.Exists(Path.Combine(source, "data", "level1.pak")));
                Assert.False(Directory.Exists(destination));
                return;
            }

            // Success: the old path is a link and the files are reachable through it.
            Assert.NotEqual(0, (int)(File.GetAttributes(source) & FileAttributes.ReparsePoint));
            Assert.Equal("level", File.ReadAllText(Path.Combine(source, "data", "level1.pak")));

            string undo = StorageMigrator.UndoMigration(source, (_, _) => { });
            Assert.StartsWith("Success", undo);
            Assert.Equal(0, (int)(File.GetAttributes(source) & FileAttributes.ReparsePoint));
            Assert.Equal("level", File.ReadAllText(Path.Combine(source, "data", "level1.pak")));
            Assert.False(Directory.Exists(destination));
        }

        [Fact]
        public void Undo_RefusesFoldersThatWereNeverMigrated()
        {
            Assert.StartsWith("Error", StorageMigrator.UndoMigration(Path.Combine(_root, "never"), (_, _) => { }));
        }
    }

    public class CoreTopologyTests
    {
        [Theory]
        [InlineData(1, 0x1L)]
        [InlineData(8, 0xFFL)]
        [InlineData(63, 0x7FFFFFFFFFFFFFFFL)]
        [InlineData(64, -1L)]   // used to wrap to 0 — an empty affinity mask
        [InlineData(0, 0L)]
        public void MaskFor_HandlesEveryCoreCount(int count, long expected)
        {
            Assert.Equal(expected, CoreDirector.MaskFor(count));
        }

        [Fact]
        public void AllButFirst_NeverReturnsAnEmptyMask()
        {
            Assert.Equal(0x1L, CoreDirector.AllButFirst(0x1L));
            Assert.Equal(0xEL, CoreDirector.AllButFirst(0xFL));
        }

        [Fact]
        public void HigherEfficiencyClass_IsThePerformanceCore()
        {
            // Windows: "a core with a higher value for the efficiency class has intrinsically greater
            // performance". The old code treated class 0 as the P-cores — backwards.
            var t = new CoreTopology();
            CoreDirector.ApplyCoreClasses(t, new List<(byte, ushort, long)>
            {
                (1, 0, 0x0FL),  // P-cores: class 1
                (0, 0, 0xF0L),  // E-cores: class 0
            });

            Assert.True(t.HasHybridCores);
            Assert.Equal(0x0FL, t.PCoresMask.ToInt64());
            Assert.Equal(0xF0L, t.ECoresMask.ToInt64());
        }

        [Fact]
        public void SingleCoreClass_IsNotHybrid_And64ThreadsCountCorrectly()
        {
            var t = new CoreTopology();
            CoreDirector.ApplyCoreClasses(t, new List<(byte, ushort, long)> { (0, 0, -1L) });

            Assert.False(t.HasHybridCores);
            Assert.Equal(64, t.PCoreCount); // the old bit-count loop never terminated on this mask
        }

        [Fact]
        public void OnlyProcessorGroupZeroIsUsed()
        {
            var t = new CoreTopology();
            CoreDirector.ApplyCoreClasses(t, new List<(byte, ushort, long)>
            {
                (1, 0, 0x3L), (0, 0, 0xCL),
                (1, 1, 0xFFL), // group 1 can't be reached by a process affinity mask
            });
            Assert.Equal(0x3L, t.PCoresMask.ToInt64());
        }
    }

    public class RepairReportingTests
    {
        [Fact]
        public void HealthySystem_IsReportedAsSuccess()
        {
            var r = SystemIntegrityAgent.Summarize(0, 0, "Windows Resource Protection did not find any integrity violations.");
            Assert.True(r.Success);
            Assert.Contains("No damaged Windows files", r.Message);
        }

        [Fact]
        public void DismFailure_IsReportedAsFailure()
        {
            var r = SystemIntegrityAgent.Summarize(87, 0, "did not find any integrity violations");
            Assert.False(r.Success);
            Assert.Contains("DISM error code 87", r.Message);
        }

        [Fact]
        public void DismRestartRequired_IsStillSuccess()
        {
            Assert.True(SystemIntegrityAgent.Summarize(3010, 0, "found corrupt files and successfully repaired them.").Success);
        }

        [Fact]
        public void SfcUnableToFix_IsReportedAsFailure()
        {
            var r = SystemIntegrityAgent.Summarize(0, 0, "Windows Resource Protection found corrupt files but was unable to fix some of them.");
            Assert.False(r.Success);
            Assert.Contains("could NOT be fixed", r.Message);
        }
    }

    public class DebloatSafetyTests
    {
        [Fact]
        public void XboxIdentityProvider_IsNotOfferedForRemoval()
        {
            // Removing it breaks Xbox / Game Pass sign-in in PC games.
            Assert.False(DebloatGuard.IsKnownRemovablePackage("Microsoft.XboxIdentityProvider"));
        }
    }
}
