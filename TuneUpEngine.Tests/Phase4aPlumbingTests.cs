using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    public class ContractTests
    {
        [Fact]
        public void EveryCommandConstant_IsItsOwnName()
        {
            // Guards against a copy-paste typo turning into "Unknown command" at runtime.
            foreach (var field in typeof(IpcCommands).GetFields(BindingFlags.Public | BindingFlags.Static))
                Assert.Equal(field.Name, (string)field.GetValue(null)!);
        }

        [Fact]
        public void TypedPayloads_RoundTrip_AndCarryCharactersTheOldFormatCouldNot()
        {
            // The old "source|destination" format broke on a '|' in a value.
            var original = new MigrateRequest(@"C:\Games\A|B", @"D:\Games\A|B");
            var back = IpcPayload.Deserialize<MigrateRequest>(IpcPayload.Serialize(original));
            Assert.Equal(original, back);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("SysMain|true")]  // the retired format
        [InlineData("{not json")]
        public void MalformedPayloads_DeserializeToNull(string? payload)
        {
            Assert.Null(IpcPayload.Deserialize<SetServiceRequest>(payload));
        }
    }

    public class EngineClientTests
    {
        private static string StartServer()
        {
            string name = "RRTU_Test_" + Guid.NewGuid().ToString("N");
            new IpcServer(
                new ProBalanceGovernor(), new StandbyListManager(), new Winapp2Parser(), new SystemIntegrityAgent(),
                new RegistryVerifier(), new GameModeManager(), new Winapp2Downloader(), new LatencyAuditor(),
                new EngineSettings(), pipeName: name, trustedClientPaths: new[] { Environment.ProcessPath! }).Start();
            return name;
        }

        // The server runs inside this test process, so "the genuine engine" is this process.
        private static EngineClient ClientFor(string pipe) => new(new[] { Environment.ProcessPath! }, pipe);

        [Fact]
        public void Client_TalksToTheEngine()
        {
            var reply = ClientFor(StartServer()).Send(IpcCommands.GetEngineStats);
            Assert.Equal(IpcStatus.Success, reply?.Status);
        }

        [Fact]
        public void Client_RefusesAnEngineThatIsNotGenuine()
        {
            // Same server, but the client only trusts an engine at a different path.
            var client = new EngineClient(new[] { @"C:\Program Files\Ram Raiders TuneUp Suite\TuneUpEngine.exe" }, StartServer());
            Assert.Null(client.Send(IpcCommands.GetEngineStats));
            Assert.Equal(IpcStatus.Error, client.SendStreaming(IpcCommands.ScanJunk, null, null).Status);
        }

        [Fact]
        public void TypedPayloads_AreUnderstoodByTheEngine()
        {
            var reply = ClientFor(StartServer()).Send(IpcCommands.SetServiceState, new SetServiceRequest("WinDefend", true));
            Assert.Equal(IpcStatus.Error, reply?.Status);
            Assert.StartsWith("Refused", reply?.Message); // parsed, and refused by the allowlist
        }

        [Fact]
        public void StreamingRequests_ReturnTheFinalReply_EvenWhenCancelledUpFront()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var reply = ClientFor(StartServer()).SendStreaming(IpcCommands.ScanAdvancedEmptyFolders, null, null, cts.Token);

            // Either the cancel won the race or the (small) scan finished first — but a final reply
            // always arrives; the client never hangs.
            Assert.True(reply.Status == IpcStatus.Success ||
                        (reply.Status == IpcStatus.Error && reply.Message!.Contains("cancelled")), reply.Message);
        }

        private static List<IpcResponse> RawExchange(string pipe, params IpcRequest[] requests)
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation);
            client.Connect(5000);
            var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            var reader = new StreamReader(client, Encoding.UTF8);
            foreach (var r in requests) writer.WriteLine(JsonSerializer.Serialize(r));

            var replies = new List<IpcResponse>();
            int expected = requests.Count(r => r.Command != IpcCommands.CancelRequest);
            while (replies.Count < expected)
            {
                string? line = reader.ReadLine();
                if (line == null) break;
                replies.Add(JsonSerializer.Deserialize<IpcResponse>(line)!);
            }
            return replies;
        }

        [Fact]
        public void Replies_EchoTheRequestId()
        {
            var replies = RawExchange(StartServer(), new IpcRequest { Command = IpcCommands.GetEngineStats, RequestId = "req-42" });
            Assert.Equal("req-42", Assert.Single(replies).RequestId);
        }

        [Fact]
        public void CancellingAnUnknownRequest_IsHarmless_AndGetsNoReplyOfItsOwn()
        {
            var replies = RawExchange(StartServer(),
                new IpcRequest { Command = IpcCommands.CancelRequest, Payload = "no-such-request" },
                new IpcRequest { Command = IpcCommands.GetEngineStats, RequestId = "after" });

            var reply = Assert.Single(replies);
            Assert.Equal("after", reply.RequestId);
            Assert.Equal(IpcStatus.Success, reply.Status);
        }
    }

    public class OperationCancellationTests : IDisposable
    {
        private readonly string _dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "RRTU_Cancel_" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static CancellationToken Cancelled()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            return cts.Token;
        }

        [Fact]
        public void CancelledScan_Throws()
        {
            Assert.Throws<OperationCanceledException>(() =>
                new Winapp2Parser().Scan(ClientContext.ForCurrentProcess(), null, Cancelled()));
        }

        [Fact]
        public void CancelledClean_DeletesNothing()
        {
            string file = Path.Combine(_dir, "junk.tmp");
            File.WriteAllText(file, "x");
            var item = new ScanItem { Category = "System", FilePaths = new List<string> { file } };

            long freed = new Winapp2Parser().Clean(new List<ScanItem> { item }, ClientContext.ForCurrentProcess(), null, Cancelled());

            Assert.Equal(0, freed);
            Assert.True(File.Exists(file));
        }

        [Fact]
        public void CancelledDuplicateAndEmptyFolderScans_Throw()
        {
            var folders = new UserFolders { Documents = _dir };
            Directory.CreateDirectory(Path.Combine(_dir, "empty"));
            File.WriteAllText(Path.Combine(_dir, "a.bin"), "same");
            File.WriteAllText(Path.Combine(_dir, "b.bin"), "same");

            Assert.Throws<OperationCanceledException>(() => new AdvancedScanner().ScanEmptyFolders(folders, _ => { }, Cancelled()));
            Assert.Throws<OperationCanceledException>(() => new AdvancedScanner().ScanDuplicateFiles(folders, _ => { }, Cancelled()));
        }

        [Fact]
        public void CancelledMigration_LeavesTheOriginalAndRemovesThePartialCopy()
        {
            string source = Path.Combine(_dir, "Game");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "game.exe"), "x");
            string destination = Path.Combine(_dir, "Moved", "Game");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            string result = StorageMigrator.Migrate(source, destination, (_, _) => { }, Cancelled());

            Assert.Contains("cancelled", result, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(source, "game.exe")));
            Assert.False(Directory.Exists(destination));
        }
    }

    public class FileLogTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "RRTU_Log_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void WritesTimestampedLevelledLines()
        {
            var log = new FileLog(_dir, "test");
            log.Write("WARN", "something happened");

            string text = File.ReadAllText(log.CurrentFile);
            Assert.Contains("[WARN] something happened", text);
        }

        [Fact]
        public void StopsAtTheDailySizeCap_AndSaysSoOnce()
        {
            var log = new FileLog(_dir, "test", maxBytesPerDay: 200);
            for (int i = 0; i < 50; i++) log.Write("INFO", $"line {i} with some padding to fill the file");

            string text = File.ReadAllText(log.CurrentFile);
            Assert.True(new FileInfo(log.CurrentFile).Length < 600);
            Assert.Equal(1, text.Split("Log size limit reached").Length - 1);
        }

        [Fact]
        public void DeletesFilesOlderThanTheRetention()
        {
            Directory.CreateDirectory(_dir);
            string old = Path.Combine(_dir, "test-20200101.log");
            string recent = Path.Combine(_dir, "test-recent.log");
            File.WriteAllText(old, "old");
            File.WriteAllText(recent, "recent");
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));

            _ = new FileLog(_dir, "test", keepDays: 7);

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
        }

        [Fact]
        public void AnUnusableFolder_DisablesLoggingInsteadOfThrowing()
        {
            var log = new FileLog(@"Z:\definitely\not\here\" + Guid.NewGuid().ToString("N") + "\\" + new string('x', 300), "test");
            log.Write("ERROR", "goes nowhere"); // must not throw
            Assert.False(log.Enabled);
        }
    }
}
