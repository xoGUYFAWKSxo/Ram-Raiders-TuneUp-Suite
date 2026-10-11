using System;
using System.IO;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // Duplicate-scan scoping: nested roots must collapse so no file is indexed twice, and cloud-only
    // files must be recognised so hashing never downloads them.
    public class AdvancedScannerScopeTests
    {
        [Fact]
        public void RemoveNestedRoots_DropsRootsInsideOtherRoots()
        {
            var roots = AdvancedScanner.RemoveNestedRoots(new[]
            {
                @"C:\Users\x\OneDrive",
                @"C:\Users\x\OneDrive\Documents",
                @"C:\Users\x\Documents",
                @"C:\Users\x\documents\", // same folder, different case + trailing slash
                @"C:\Users\x\Documents2"   // sibling sharing a prefix — must be kept
            });

            Assert.Equal(new[] { @"C:\Users\x\OneDrive", @"C:\Users\x\Documents", @"C:\Users\x\Documents2" }, roots);
        }

        [Theory]
        [InlineData(FileAttributes.Offline, true)]
        [InlineData((FileAttributes)0x00400000, true)] // RECALL_ON_DATA_ACCESS (OneDrive online-only)
        [InlineData((FileAttributes)0x00040000, true)] // RECALL_ON_OPEN
        [InlineData(FileAttributes.Archive | FileAttributes.ReadOnly, false)]
        public void IsCloudPlaceholder_DetectsOnlineOnlyFiles(FileAttributes attributes, bool expected)
        {
            Assert.Equal(expected, AdvancedScanner.IsCloudPlaceholder(attributes));
        }
    }

    // Registry Guard must only report targets it can prove are missing, because repair deletes them.
    public class RegistryMissingTargetTests
    {
        [Theory]
        [InlineData("rundll32.exe")]   // bare name: resolved via the search path
        [InlineData("mscoree.dll")]    // bare name: .NET shell extensions
        [InlineData(@"C:\Program")]    // truncated command line — no extension
        [InlineData("")]
        public void UnprovableTargets_AreNotReported(string path)
        {
            Assert.False(RegistryVerifier.IsMissingTarget(path));
        }

        [Fact]
        public void MissingFileUnderWindowsDirectory_IsNotReported_RegardlessOfCase()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string path = Path.Combine(windows.ToLowerInvariant(), "system32", "definitely-missing-rrtu.dll");
            Assert.False(RegistryVerifier.IsMissingTarget(path));
        }

        [Fact]
        public void MissingFullyQualifiedFile_IsReported()
        {
            string path = Path.Combine(Path.GetTempPath(), "rrtu-missing-" + Guid.NewGuid().ToString("N") + ".exe");
            Assert.True(RegistryVerifier.IsMissingTarget(path));
        }

        [Fact]
        public void ExistingFile_IsNotReported()
        {
            string path = Path.GetTempFileName();
            try { Assert.False(RegistryVerifier.IsMissingTarget(path)); }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(@"C:\Program Files\Vendor\ext.dll", @"C:\Program Files\Vendor\ext.dll")]
        [InlineData("\"C:\\Program Files\\Vendor\\ext.dll\"", @"C:\Program Files\Vendor\ext.dll")]
        public void InprocServerPath_WithSpaces_IsNotTruncated(string raw, string expected)
        {
            Assert.Equal(expected, new RegistryVerifier().ExtractServerPath(raw, isCommandLine: false));
        }
    }

    public class UltimatePowerPlanTests
    {
        private const string ListWithUserCopy =
            "Existing Power Schemes (* Active)\r\n" +
            "-----------------------------------\r\n" +
            "Power Scheme GUID: 05a5332a-9b3a-4cb6-ab67-8c654b65d4cd  (Ultimate Performance) *\r\n" +
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\r\n";

        [Fact]
        public void ReusesTheUsersExistingUltimatePlan()
        {
            Assert.Equal("05a5332a-9b3a-4cb6-ab67-8c654b65d4cd", GameModeManager.FindUltimatePlanGuid(ListWithUserCopy));
        }

        [Fact]
        public void ReturnsEmpty_WhenNoUltimatePlanExists()
        {
            const string list = "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *\r\n";
            Assert.Equal("", GameModeManager.FindUltimatePlanGuid(list));
        }

        [Fact]
        public void NeverReturnsTheTemplateGuid_UnlessItIsListed()
        {
            Assert.NotEqual("e9a42b02-d5df-448d-aa00-03f14749eb61", GameModeManager.FindUltimatePlanGuid(ListWithUserCopy));
        }
    }

    public class RetiredTweakTests
    {
        [Theory]
        [InlineData("DisableUAC")]
        [InlineData("DisableSmartScreen")]
        [InlineData("DisableSecurityWarning")]
        public void RetiredSecurityTweaks_CannotBeEnabled(string tweak)
        {
            // Refused before any registry access, so this is safe to run anywhere.
            Assert.False(RegistryTweaker.SetTweakStatus(tweak, enable: true));
        }
    }
}
