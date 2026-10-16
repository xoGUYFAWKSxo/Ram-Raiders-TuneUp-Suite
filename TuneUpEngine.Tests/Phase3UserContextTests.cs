using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    public class UserFoldersTests
    {
        private static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

        [Fact]
        public void ResolvingByMySid_MatchesWhatWindowsReportsForMe()
        {
            // The service resolves other users' folders from the profile list + their hive; for the
            // current user that must agree with the normal Windows APIs.
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            var bySid = UserFolders.ForSid(sid)!;
            var mine = UserFolders.ForCurrentProcess();

            Assert.NotNull(bySid);
            Assert.Equal(Norm(mine.Profile), Norm(bySid.Profile), ignoreCase: true);
            Assert.Equal(Norm(mine.Documents), Norm(bySid.Documents), ignoreCase: true);
            Assert.Equal(Norm(mine.Desktop), Norm(bySid.Desktop), ignoreCase: true);
            Assert.Equal(Norm(mine.LocalAppData), Norm(bySid.LocalAppData), ignoreCase: true);
            Assert.Equal(Norm(mine.RoamingAppData), Norm(bySid.RoamingAppData), ignoreCase: true);
            Assert.Equal(Norm(mine.Temp), Norm(bySid.Temp), ignoreCase: true);
        }

        [Fact]
        public void UnknownSid_ReturnsNull()
        {
            Assert.Null(UserFolders.ForSid("S-1-5-21-1-2-3-99999"));
        }

        [Fact]
        public void Contains_OnlyMatchesInsideTheProfile()
        {
            var f = new UserFolders { Profile = @"C:\Users\alice" };
            Assert.True(f.Contains(@"C:\Users\alice\AppData\Local\Temp\x.tmp"));
            Assert.False(f.Contains(@"C:\Users\alice2\x.tmp")); // shares a prefix, different user
            Assert.False(f.Contains(@"C:\Windows\Logs\CBS\CBS.log"));
        }
    }

    public class CleanerUserContextTests : IDisposable
    {
        private readonly string _dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "RRTU_Ctx_" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void TokensExpandToTheRequestingUsersFolders()
        {
            var bob = new UserFolders
            {
                Profile = @"D:\Profiles\bob",
                LocalAppData = @"D:\Profiles\bob\AppData\Local",
                RoamingAppData = @"D:\Profiles\bob\AppData\Roaming",
                Temp = @"D:\Profiles\bob\Temp$1", // '$' must not be treated as a regex substitution
                Documents = @"D:\Profiles\bob\Docs",
            };

            Assert.Equal(@"D:\Profiles\bob\AppData\Local\App\Cache", Winapp2Parser.ExpandSpecialFolder(@"%LocalAppData%\App\Cache", bob));
            Assert.Equal(@"D:\Profiles\bob\Temp$1\x", Winapp2Parser.ExpandSpecialFolder(@"%TEMP%\x", bob));
            Assert.Equal(@"D:\Profiles\bob\Docs", Winapp2Parser.ExpandSpecialFolder("%Documents%", bob));
        }

        [Fact]
        public void NonAdministrator_CannotCleanOutsideTheirProfile()
        {
            string systemFile = Path.Combine(_dir, "system.log");
            File.WriteAllText(systemFile, "not yours");

            // A standard user whose profile is somewhere else entirely.
            var standardUser = new ClientContext
            {
                Sid = "S-1-5-21-1-2-3-1001",
                IsAdministrator = false,
                Folders = new UserFolders { Profile = @"C:\Users\someone-else" },
            };
            var item = new ScanItem { Category = "System", FilePaths = new List<string> { systemFile } };

            new Winapp2Parser().Clean(new List<ScanItem> { item }, standardUser);

            Assert.True(File.Exists(systemFile));
        }

        [Fact]
        public void FilesInsideTheProfile_AreCleaned()
        {
            string junk = Path.Combine(_dir, "junk.tmp");
            File.WriteAllText(junk, "x");
            var me = ClientContext.ForCurrentProcess(); // _dir is under my %TEMP%, i.e. my profile
            Assert.True(me.Folders.Contains(junk));

            new Winapp2Parser().Clean(new List<ScanItem> { new ScanItem { Category = "System", FilePaths = new List<string> { junk } } }, me);

            Assert.False(File.Exists(junk));
        }

        [Fact]
        public void RecycleBinSize_ForUnknownUser_IsZero()
        {
            Assert.Equal(0, Winapp2Parser.RecycleBinSize("S-1-5-21-1-2-3-99999"));
        }
    }

    public class ScopedResultsTests
    {
        private record Item(string Id);

        [Fact]
        public void OneUser_CannotActOnAnotherUsersResults()
        {
            var store = new ResultStore<Item>(i => i.Id);
            store.Replace("junk", new[] { new Item("alice-1") }, scope: "S-alice");
            store.Replace("junk", new[] { new Item("bob-1") }, scope: "S-bob");

            Assert.Empty(store.Take(new[] { "alice-1" }, scope: "S-bob"));
            Assert.Single(store.Take(new[] { "alice-1" }, scope: "S-alice"));
            Assert.Single(store.Take(new[] { "bob-1" }, scope: "S-bob")); // bob's scan wasn't replaced by alice's
        }
    }

    public class MigrationProfileTests
    {
        private static string UsersDir => Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;

        [Theory]
        [InlineData("someone-else")]                         // any user's profile root, not just mine
        [InlineData(@"someone-else\AppData")]
        [InlineData(@"someone-else\AppData\Local")]
        [InlineData(@"someone-else\AppData\Roaming")]
        public void ProfileRootsAndAppData_AreRefused(string relative)
        {
            Assert.NotNull(StorageMigrator.ValidateSource(Path.Combine(UsersDir, relative)));
        }

        [Theory]
        [InlineData(@"someone-else\AppData\Local\SomeLauncher")]
        [InlineData(@"someone-else\Documents\My Games")]
        public void FoldersInsideAProfile_AreAllowed(string relative)
        {
            Assert.Null(StorageMigrator.ValidateSource(Path.Combine(UsersDir, relative)));
        }
    }

    public class EngineTrustTests
    {
        [Fact]
        public void UiTrustsOnlyTheEngineNextToIt()
        {
            var trusted = ClientVerifier.TrustedEnginePaths(@"C:\Program Files\Galactic Tune-Up\");
            Assert.True(ClientVerifier.IsTrustedClient(@"C:\Program Files\Galactic Tune-Up\TuneUpEngine.exe", trusted));
            Assert.False(ClientVerifier.IsTrustedClient(@"C:\Users\x\AppData\Local\Temp\TuneUpEngine.exe", trusted));
        }

        [Fact]
        public void DevUi_TrustsTheEngineFromTheSameSolution()
        {
            var trusted = ClientVerifier.TrustedEnginePaths(@"C:\src\Suite\TuneUpUI\bin\Debug\net11.0-windows\");
            Assert.True(ClientVerifier.IsTrustedClient(@"C:\src\Suite\TuneUpEngine\bin\Debug\net11.0-windows\TuneUpEngine.exe", trusted));
        }
    }

    // End-to-end over a real pipe, with the identity of the connecting user simulated.
    public class AdministratorGatingTests
    {
        private static ClientContext StandardUser() => new ClientContext
        {
            Sid = WindowsIdentity.GetCurrent().User!.Value,
            IsAdministrator = false,
            SessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId,
            Folders = UserFolders.ForCurrentProcess(),
        };

        private static string StartServer(bool isServiceMode = false, Func<NamedPipeServerStream, ClientContext>? ctx = null)
        {
            string name = "RRTU_Test_" + Guid.NewGuid().ToString("N");
            new IpcServer(
                new ProBalanceGovernor(), new StandbyListManager(), new Winapp2Parser(), new SystemIntegrityAgent(),
                new RegistryVerifier(), new GameModeManager(), new Winapp2Downloader(), new LatencyAuditor(),
                new EngineSettings(), pipeName: name, trustedClientPaths: new[] { Environment.ProcessPath! },
                isServiceMode: isServiceMode, contextFactory: ctx).Start();
            return name;
        }

        private static IpcResponse? Send(string pipeName, string command, string payload)
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.None, TokenImpersonationLevel.Impersonation);
            client.Connect(5000);
            var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            var reader = new StreamReader(client, Encoding.UTF8);
            writer.WriteLine(JsonSerializer.Serialize(new IpcRequest { Command = command, Payload = payload }));
            string? line = reader.ReadLine();
            return line == null ? null : JsonSerializer.Deserialize<IpcResponse>(line);
        }

        [Theory]
        [InlineData("SetServiceState", "SysMain|true")]
        [InlineData("PurgeStandbyList", "")]
        [InlineData("SetGameModeState", "false")]
        [InlineData("ProvisionMSIInterrupts", "")]
        [InlineData("MigrateDirectory", @"C:\Games\X|D:\Games\X")]
        public void StandardUser_CannotMakeMachineWideChanges(string command, string payload)
        {
            var reply = Send(StartServer(ctx: _ => StandardUser()), command, payload);
            Assert.Equal("Error", reply?.Status);
            Assert.Equal("This needs an administrator account.", reply?.Message);
        }

        [Fact]
        public void StandardUser_CannotApplyMachineWideTweaks()
        {
            var reply = Send(StartServer(ctx: _ => StandardUser()), "SetRegistryTweak", "NetworkThrottling|true");
            Assert.Equal("Error", reply?.Status);
        }

        [Fact]
        public void StandardUser_CanStillReadStatus()
        {
            Assert.Equal("Success", Send(StartServer(ctx: _ => StandardUser()), "GetEngineStats", "")?.Status);
        }

        [Fact]
        public void RealClientIdentity_IsResolvedFromThePipe()
        {
            // No context override: the engine impersonates this test process to find out who it is.
            var reply = Send(StartServer(), "GetRegistryTweaks", "");
            Assert.Equal("Success", reply?.Status);
            Assert.Contains("GameDVR", reply?.Payload);
        }

        [Fact]
        public void ServiceEngine_RefusesToBeShutDownByAUi()
        {
            var reply = Send(StartServer(isServiceMode: true), "ShutdownEngine", "");
            Assert.Equal("Error", reply?.Status);
        }

        [Fact]
        public void ForegroundReports_OnlyAcceptProcessesInTheUsersSession()
        {
            ForegroundTracker.Reset();
            string pipe = StartServer();
            int me = Environment.ProcessId;

            Assert.Equal("Success", Send(pipe, "ReportForeground", me.ToString())?.Status);
            Assert.Equal(me, ForegroundTracker.GetForegroundProcessId());

            // PID 4 is the System process, in session 0 — never in a user's session.
            Assert.Equal("Error", Send(pipe, "ReportForeground", "4")?.Status);
            Assert.Equal("Error", Send(pipe, "ReportForeground", "not-a-pid")?.Status);
            ForegroundTracker.Reset();
        }
    }
}
