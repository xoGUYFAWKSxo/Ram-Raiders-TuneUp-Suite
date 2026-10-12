using System;
using System.IO.Pipes;
using System.Threading.Tasks;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // A different engine holding the pipe (e.g. one left running by an earlier install) used to
    // show only "Engine Offline". The client must flag it so the UI can say what to do.
    public class EngineBlockedTests
    {
        [Fact]
        public async Task UntrustedEngineOnThePipe_IsReportedAsAnotherEngine()
        {
            string pipe = "RRTU_Test_" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accept = server.WaitForConnectionAsync();

            // Trusts no engine path, so the test process serving the pipe is "a different copy".
            var client = new EngineClient(Array.Empty<string>(), pipe);
            Assert.Null(client.Send(IpcCommands.GetEngineStats, timeoutMs: 2000));

            Assert.True(client.OtherEngineRunning);
            Assert.StartsWith("A different copy of the TuneUp engine is running", client.LastConnectionProblem);
            try { await accept; } catch (System.IO.IOException) { } // the client hung up, as it should
        }

        [Fact]
        public void NoEngine_IsNotReportedAsAnotherEngine()
        {
            var client = new EngineClient(Array.Empty<string>(), "RRTU_Test_" + Guid.NewGuid().ToString("N"));
            Assert.Null(client.Send(IpcCommands.GetEngineStats, timeoutMs: 200));

            Assert.False(client.OtherEngineRunning);
            Assert.Equal("The TuneUp engine isn't running.", client.LastConnectionProblem);
        }

        [Fact]
        public void EngineOfUnknownLocation_TellsTheUserHowToClearIt()
        {
            string text = EngineClient.DescribeOtherEngine(null);
            Assert.Contains("earlier install", text);
            Assert.Contains("taskkill /im TuneUpEngine.exe /f", text);
            Assert.Contains("Restart the PC", text);
        }
    }
}
