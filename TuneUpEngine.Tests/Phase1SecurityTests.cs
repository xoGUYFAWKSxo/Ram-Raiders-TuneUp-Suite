using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // Destructive commands act only on the engine's own scan results, by ID.
    public class ResultStoreTests
    {
        private record Item(string Id, string Name);

        [Fact]
        public void Take_ReturnsOnlyKnownIds_AndConsumesThem()
        {
            var store = new ResultStore<Item>(i => i.Id);
            store.Replace("junk", new[] { new Item("a", "A"), new Item("b", "B") });

            var taken = store.Take(new[] { "a", "forged", "a" });

            Assert.Equal(new[] { "A" }, taken.Select(i => i.Name));
            Assert.Empty(store.Take(new[] { "a" })); // can't be replayed
            Assert.Single(store.Take(new[] { "b" }));
        }

        [Fact]
        public void Replace_OnlyReplacesThatKind()
        {
            var store = new ResultStore<Item>(i => i.Id);
            store.Replace("junk", new[] { new Item("j1", "old junk") });
            store.Replace("duplicates", new[] { new Item("d1", "dup") });
            store.Replace("junk", new[] { new Item("j2", "new junk") });

            Assert.Empty(store.Take(new[] { "j1" }));  // stale result from the previous junk scan
            Assert.Single(store.Take(new[] { "j2" }));
            Assert.Single(store.Take(new[] { "d1" }));  // other kinds untouched
        }

        [Fact]
        public void Take_Null_ReturnsEmpty()
        {
            Assert.Empty(new ResultStore<Item>(i => i.Id).Take(null));
        }
    }

    public class DuplicateReverificationTests : IDisposable
    {
        private readonly string _dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "RRTU_Reverify_" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Write(string name, string content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static ScanItem Set(params string[] paths) => new ScanItem
        {
            Category = AdvancedScanner.DuplicateFilesCategory,
            FilePaths = paths.ToList()
        };

        [Fact]
        public void KeptCopyMissing_NothingIsDeleted()
        {
            string kept = Write("kept.txt", "same");
            string extra = Write("extra.txt", "same");
            File.Delete(kept); // gone between scan and clean

            new Winapp2Parser().Clean(new List<ScanItem> { Set(kept, extra) });

            Assert.True(File.Exists(extra)); // still the user's only copy
        }

        [Fact]
        public void ExtraCopyChangedSinceScan_IsNotDeleted()
        {
            string kept = Write("kept.txt", "same");
            string extra = Write("extra.txt", "same");
            File.WriteAllText(extra, "edited after the scan");

            new Winapp2Parser().Clean(new List<ScanItem> { Set(kept, extra) });

            Assert.True(File.Exists(extra));
        }

        [Fact]
        public void StillIdentical_ExtraIsDeleted()
        {
            string kept = Write("kept.txt", "same");
            string extra = Write("extra.txt", "same");

            new Winapp2Parser().Clean(new List<ScanItem> { Set(kept, extra) });

            Assert.True(File.Exists(kept));
            Assert.False(File.Exists(extra));
        }
    }

    public class MigrationPathTests
    {
        private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        [Fact]
        public void RefusesDriveRoot() => Assert.NotNull(StorageMigrator.ValidateSource(Path.GetPathRoot(Windows)));

        [Fact]
        public void RefusesWindowsAndAnythingInside()
        {
            Assert.NotNull(StorageMigrator.ValidateSource(Windows));
            Assert.NotNull(StorageMigrator.ValidateSource(Path.Combine(Windows, "System32")));
        }

        [Fact]
        public void RefusesFoldersThatContainOtherData()
        {
            Assert.NotNull(StorageMigrator.ValidateSource(Profile));
            Assert.NotNull(StorageMigrator.ValidateSource(Path.GetDirectoryName(Profile))); // C:\Users
            Assert.NotNull(StorageMigrator.ValidateSource(ProgramFiles));
        }

        [Theory]
        [InlineData("")]
        [InlineData("relative\\folder")]
        [InlineData("C:\\bad|name")]
        public void RefusesInvalidPaths(string path) => Assert.NotNull(StorageMigrator.ValidateSource(path));

        [Fact]
        public void AllowsAGameFolder()
        {
            Assert.Null(StorageMigrator.ValidateSource(Path.Combine(ProgramFiles, "Steam", "steamapps", "common", "SomeGame")));
            Assert.Null(StorageMigrator.ValidateSource(Path.Combine(Path.GetPathRoot(Windows)!, "Games", "SomeGame")));
        }

        [Fact]
        public void DestinationInsideSource_IsRefused()
        {
            string source = @"C:\Games\SomeGame";
            Assert.NotNull(StorageMigrator.ValidateDestination(source, @"C:\Games\SomeGame\copy"));
            Assert.NotNull(StorageMigrator.ValidateDestination(source, @"C:\Games"));
            Assert.Null(StorageMigrator.ValidateDestination(source, @"D:\Games\SomeGame"));
        }

        [Fact]
        public void DestinationInsideWindows_IsRefused()
        {
            Assert.NotNull(StorageMigrator.ValidateDestination(@"C:\Games\SomeGame", Path.Combine(Windows, "SomeGame")));
        }
    }

    public class EngineInputValidationTests
    {
        [Theory]
        [InlineData("SysMain", true)]
        [InlineData("diagtrack", true)]
        [InlineData("WinDefend", false)]
        [InlineData("SysMain\\Parameters", false)]
        [InlineData("", false)]
        public void OnlyListedServicesAreManaged(string name, bool expected)
        {
            Assert.Equal(expected, DebloatGuard.IsManagedService(name));
        }

        [Fact]
        public void SetServiceState_RefusesUnlistedService_WithoutTouchingIt()
        {
            Assert.StartsWith("Refused", DebloatGuard.SetServiceState("WinDefend", disable: true, new ChangeJournal(null)));
        }

        [Fact]
        public void AllowSid_AcceptsUserAccounts_RejectsGroupsAndGarbage()
        {
            string me = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            var sids = Program.ParseAllowedSids(new[]
            {
                "--allow-sid", me,
                "--allow-sid", "S-1-1-0",        // Everyone
                "--allow-sid", "S-1-5-32-545",   // BUILTIN\Users
                "--allow-sid", "not-a-sid",
                "--allow-sid"                     // dangling
            });

            Assert.Equal(new[] { me }, sids.Select(s => s.Value));
        }

        [Fact]
        public void ReadLineBounded_ReadsLines_AndStopsOversizedOnes()
        {
            var reader = new StringReader("first\r\nsecond\n" + new string('x', 50) + "\n");

            Assert.Equal("first", IpcServer.ReadLineBounded(reader, 20, out bool tooLong1));
            Assert.False(tooLong1);
            Assert.Equal("second", IpcServer.ReadLineBounded(reader, 20, out _));
            Assert.Null(IpcServer.ReadLineBounded(reader, 20, out bool tooLong3));
            Assert.True(tooLong3);
        }
    }

    public class ClientVerifierTests
    {
        [Fact]
        public void InstalledLayout_TrustsOnlyTheUiNextToTheEngine()
        {
            var trusted = ClientVerifier.TrustedClientPaths(@"C:\Program Files\Galactic Tune-Up\");

            Assert.True(ClientVerifier.IsTrustedClient(@"C:\Program Files\Galactic Tune-Up\TuneUpUI.exe", trusted));
            Assert.True(ClientVerifier.IsTrustedClient(@"c:\program files\galactic tune-up\tuneupui.exe", trusted));
            Assert.False(ClientVerifier.IsTrustedClient(@"C:\Users\x\Downloads\TuneUpUI.exe", trusted));
            Assert.False(ClientVerifier.IsTrustedClient(@"C:\Program Files\Galactic Tune-Up\other.exe", trusted));
            Assert.False(ClientVerifier.IsTrustedClient(null, trusted));
        }

        [Fact]
        public void DevLayout_AlsoTrustsTheUiBuiltFromTheSameSolution()
        {
            var trusted = ClientVerifier.TrustedClientPaths(@"C:\src\Suite\TuneUpEngine\bin\Release\net11.0-windows\");

            Assert.True(ClientVerifier.IsTrustedClient(@"C:\src\Suite\TuneUpUI\bin\Release\net11.0-windows\TuneUpUI.exe", trusted));
            Assert.False(ClientVerifier.IsTrustedClient(@"C:\src\Suite\TuneUpUI\bin\Debug\net11.0-windows\TuneUpUI.exe", trusted));
        }
    }

    // End-to-end over a real named pipe (with a test-only name, never the real engine's).
    public class PipeSecurityTests
    {
        // Modules are constructed but never enabled, so no system settings are touched.
        private static IpcServer NewServer(string pipeName, IReadOnlyList<string>? trustedClients = null) => new IpcServer(
            new ProBalanceGovernor(), new StandbyListManager(), new Winapp2Parser(), new SystemIntegrityAgent(),
            new RegistryVerifier(), new GameModeManager(), new Winapp2Downloader(), new LatencyAuditor(),
            new EngineSettings(), pipeName: pipeName, trustedClientPaths: trustedClients);

        // A server that trusts THIS test process, to exercise the real client lookup and the
        // request/response path end to end.
        private static string StartTrustingThisProcess()
        {
            string name = "RRTU_Test_" + Guid.NewGuid().ToString("N");
            NewServer(name, new[] { Environment.ProcessPath! }).Start();
            return name;
        }

        private static IpcResponse? Send(string pipeName, string command, string payload)
        {
            // Like the UI: allow the engine to act as this user, which it needs for per-user work.
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Impersonation);
            client.Connect(5000);
            var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            var reader = new StreamReader(client, Encoding.UTF8);
            writer.WriteLine(JsonSerializer.Serialize(new IpcRequest { Command = command, Payload = payload }));
            string? line = reader.ReadLine();
            return line == null ? null : JsonSerializer.Deserialize<IpcResponse>(line);
        }

        [Fact]
        public void TrustedClient_IsServed()
        {
            var reply = Send(StartTrustingThisProcess(), "GetEngineStats", "");
            Assert.Equal("Success", reply?.Status);
        }

        [Fact]
        public void CleanJunk_WithForgedIds_DeletesNothing()
        {
            string victim = Path.Combine(Path.GetTempPath(), "rrtu-victim-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(victim, "must survive");
            try
            {
                // Old-style payload (a full ScanItem with a path) and a made-up ID: both must be refused.
                string oldStyle = JsonSerializer.Serialize(new[] { new ScanItem { FilePaths = new List<string> { victim } } });
                var reply1 = Send(StartTrustingThisProcess(), "CleanJunk", oldStyle);
                var reply2 = Send(StartTrustingThisProcess(), "CleanJunk", "[\"made-up-id\"]");

                Assert.Equal("Error", reply1?.Status);
                Assert.Equal("Error", reply2?.Status);
                Assert.True(File.Exists(victim));
            }
            finally
            {
                File.Delete(victim);
            }
        }

        [Fact]
        public void RepairRegistry_WithForgedIds_IsRefused()
        {
            var reply = Send(StartTrustingThisProcess(), "RepairRegistry", "[\"made-up-id\"]");
            Assert.Equal("Error", reply?.Status);
        }

        [Fact]
        public void SetServiceState_ForUnlistedService_IsRefused()
        {
            var reply = Send(StartTrustingThisProcess(), "SetServiceState", IpcPayload.Serialize(new SetServiceRequest("WinDefend", true)));
            Assert.Equal("Error", reply?.Status);
            Assert.StartsWith("Refused", reply?.Message);
        }

        [Fact]
        public void InvalidBooleanPayload_IsAnErrorNotACrash()
        {
            string pipe = StartTrustingThisProcess();
            Assert.Equal("Error", Send(pipe, "SetGovernorState", "maybe")?.Status);
            Assert.Equal("Success", Send(pipe, "GetEngineStats", "")?.Status); // engine still serving
        }

        [Fact]
        public void OversizedRequest_IsRejected()
        {
            var reply = Send(StartTrustingThisProcess(), "GetEngineStats", new string('x', IpcServer.MaxRequestChars + 10));
            Assert.Equal("Error", reply?.Status);
            Assert.Equal("Request too large.", reply?.Message);
        }

        [Fact]
        public void SecondServer_CannotClaimAnExistingPipeName()
        {
            string name = "RRTU_Test_" + Guid.NewGuid().ToString("N");
            NewServer(name).Start();

            Assert.ThrowsAny<Exception>(() => NewServer(name).Start());
        }

        [Fact]
        public void UntrustedClient_IsDisconnectedWithoutAnAnswer()
        {
            string name = "RRTU_Test_" + Guid.NewGuid().ToString("N");
            NewServer(name).Start();

            // This test process is not TuneUpUI.exe, so the engine must refuse to serve it.
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            client.Connect(5000);

            string? reply = null;
            try
            {
                var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
                var reader = new StreamReader(client, Encoding.UTF8);
                writer.WriteLine(JsonSerializer.Serialize(new IpcRequest { Command = "GetEngineStats", Payload = "" }));
                reply = reader.ReadLine();
            }
            catch (IOException)
            {
                // Pipe already closed by the server — also a refusal.
            }

            Assert.Null(reply);
        }
    }
}
