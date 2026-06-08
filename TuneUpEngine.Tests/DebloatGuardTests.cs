using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // Locks in the AppX-removal hardening (the PowerShell-injection fix). If either gate regresses,
    // these fail before a crafted package name can ever reach PowerShell in the elevated engine.
    public class DebloatGuardTests
    {
        [Theory]
        [InlineData("Microsoft.BingWeather")]
        [InlineData("DellInc.DellSupportAssistRemediation")]
        [InlineData("Microsoft.549981C3F5F10")]
        public void IsSafePackageIdentity_AcceptsLegitimateIdentities(string identity)
        {
            Assert.True(DebloatGuard.IsSafePackageIdentity(identity));
        }

        [Theory]
        [InlineData("x'; Start-Process calc; '")] // quote + semicolon breakout attempt
        [InlineData("Microsoft.App | Remove-Item")] // pipe
        [InlineData("App & whoami")]                 // ampersand
        [InlineData("App With Spaces")]              // spaces
        [InlineData("App`nNewline")]                 // backtick
        [InlineData("")]                              // empty
        public void IsSafePackageIdentity_RejectsShellMetacharacters(string identity)
        {
            Assert.False(DebloatGuard.IsSafePackageIdentity(identity));
        }

        [Theory]
        [InlineData("Microsoft.BingWeather")]  // curated exact match
        [InlineData("Microsoft.XboxGamingOverlay")]
        [InlineData("DellInc.SomethingElse")]  // known OEM prefix
        [InlineData("HPInc.HPSupport")]
        public void IsKnownRemovablePackage_AllowsCuratedAndOemPackages(string identity)
        {
            Assert.True(DebloatGuard.IsKnownRemovablePackage(identity));
        }

        [Theory]
        [InlineData("Microsoft.WindowsStore")]   // a real package, but NOT one we offer to remove
        [InlineData("Contoso.TotallyArbitrary")]
        [InlineData("Microsoft.Windows.Cortana")] // not the curated "Microsoft.Cortana" exact key
        public void IsKnownRemovablePackage_RefusesArbitraryPackages(string identity)
        {
            Assert.False(DebloatGuard.IsKnownRemovablePackage(identity));
        }
    }
}
