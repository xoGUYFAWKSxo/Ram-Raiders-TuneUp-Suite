using System;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // Locks in the startup-entry false-positive fix: an unquoted path that contains spaces must be
    // recognized as a single executable, not truncated at the first space (which previously caused
    // valid entries to be flagged as "broken" and offered up for deletion).
    public class RegistryVerifierTests
    {
        private readonly RegistryVerifier _verifier = new RegistryVerifier();

        [Fact]
        public void UnquotedPathWithSpaces_IsNotTruncated()
        {
            string result = _verifier.ExtractExecutablePath(@"C:\Program Files\App\app.exe --flag");
            Assert.Equal(@"C:\Program Files\App\app.exe", result);
        }

        [Fact]
        public void QuotedPath_ReturnsInsideQuotes()
        {
            string result = _verifier.ExtractExecutablePath("\"C:\\Program Files\\App\\app.exe\" -x -y");
            Assert.Equal(@"C:\Program Files\App\app.exe", result);
        }

        [Fact]
        public void UnquotedPathNoArguments_ReturnedAsIs()
        {
            string result = _verifier.ExtractExecutablePath(@"C:\Tools\run.exe");
            Assert.Equal(@"C:\Tools\run.exe", result);
        }

        [Fact]
        public void EnvironmentVariable_IsExpanded()
        {
            string result = _verifier.ExtractExecutablePath(@"%SystemRoot%\system32\notepad.exe");
            Assert.EndsWith(@"\system32\notepad.exe", result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%", result);
        }

        [Fact]
        public void CommandWithoutExe_FallsBackToFirstToken()
        {
            string result = _verifier.ExtractExecutablePath("someCommand --arg value");
            Assert.Equal("someCommand", result);
        }

        [Fact]
        public void EmptyInput_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, _verifier.ExtractExecutablePath(""));
        }
    }
}
