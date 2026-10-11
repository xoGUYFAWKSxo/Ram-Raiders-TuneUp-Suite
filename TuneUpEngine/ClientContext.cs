using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace TuneUpEngine
{
    // The personal folders of one user, resolved from that user's own profile and registry hive —
    // NOT from the engine's environment. When the engine runs as a service (SYSTEM) or was elevated
    // with a different administrator account, Environment.GetFolderPath / %TEMP% / HKCU describe
    // the wrong person; everything per-user must go through this instead.
    public class UserFolders
    {
        public string Profile { get; init; } = "";
        public string Documents { get; init; } = "";
        public string Pictures { get; init; } = "";
        public string Videos { get; init; } = "";
        public string Music { get; init; } = "";
        public string Desktop { get; init; } = "";
        public string Downloads { get; init; } = "";
        public string OneDrive { get; init; } = "";
        public string RoamingAppData { get; init; } = "";
        public string LocalAppData { get; init; } = "";
        public string Temp { get; init; } = "";

        private const string ShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
        private const string DownloadsGuid = "{374DE290-123F-4565-9164-39C4925E467B}";

        // The folders of whoever this process runs as. Used in console/dev mode and by tests.
        public static UserFolders ForCurrentProcess() => new UserFolders
        {
            Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            OneDrive = Environment.GetEnvironmentVariable("OneDrive") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive"),
            RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
        };

        // Resolves another user's folders from the machine profile list and that user's hive
        // (HKEY_USERS\<sid>, loaded while they are signed in). Returns null if the profile can't
        // be found.
        public static UserFolders? ForSid(string sid)
        {
            string? profile;
            using (var profileKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}"))
            {
                profile = profileKey?.GetValue("ProfileImagePath") as string;
            }
            if (string.IsNullOrEmpty(profile)) return null;
            profile = Environment.ExpandEnvironmentVariables(profile);

            using var hive = Registry.Users.OpenSubKey(sid);
            using var shell = hive?.OpenSubKey(ShellFoldersKey);
            using var env = hive?.OpenSubKey("Environment");

            // Expands %USERPROFILE% (and the user's own environment variables) for THIS user, then
            // anything left (e.g. %SystemDrive%) from the machine environment.
            string Expand(string? raw, string fallback)
            {
                string value = string.IsNullOrWhiteSpace(raw) ? fallback : raw!;
                value = ReplaceVar(value, "USERPROFILE", profile);
                if (env != null)
                {
                    foreach (var name in env.GetValueNames())
                    {
                        if (env.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string v)
                            value = ReplaceVar(value, name, ReplaceVar(v, "USERPROFILE", profile));
                    }
                }
                return Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(value));
            }

            string Shell(string name, string fallback) =>
                Expand(shell?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, fallback);

            return new UserFolders
            {
                Profile = Path.TrimEndingDirectorySeparator(profile),
                Documents = Shell("Personal", @"%USERPROFILE%\Documents"),
                Pictures = Shell("My Pictures", @"%USERPROFILE%\Pictures"),
                Videos = Shell("My Video", @"%USERPROFILE%\Videos"),
                Music = Shell("My Music", @"%USERPROFILE%\Music"),
                Desktop = Shell("Desktop", @"%USERPROFILE%\Desktop"),
                Downloads = Shell(DownloadsGuid, @"%USERPROFILE%\Downloads"),
                OneDrive = Expand(env?.GetValue("OneDrive", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, @"%USERPROFILE%\OneDrive"),
                RoamingAppData = Shell("AppData", @"%USERPROFILE%\AppData\Roaming"),
                LocalAppData = Shell("Local AppData", @"%USERPROFILE%\AppData\Local"),
                Temp = Expand(env?.GetValue("TEMP", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, @"%USERPROFILE%\AppData\Local\Temp"),
            };
        }

        private static string ReplaceVar(string value, string name, string replacement) =>
            value.Replace($"%{name}%", replacement, StringComparison.OrdinalIgnoreCase);

        // True when the path is inside this user's profile (where the user owns the files).
        public bool Contains(string path)
        {
            if (string.IsNullOrEmpty(Profile) || string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                return full.StartsWith(Profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed class UserHiveHandle : IDisposable
    {
        private readonly bool _owned;
        public RegistryKey? Key { get; }

        public UserHiveHandle(RegistryKey? key, bool owned)
        {
            Key = key;
            _owned = owned;
        }

        public void Dispose()
        {
            if (_owned) Key?.Dispose();
        }
    }

    // Who a request is for. Per-user work (HKCU tweaks, cleaning your files, scanning your folders)
    // uses this user's hive and folders, and file deletion inside the profile runs AS this user —
    // so Windows enforces that a request can only delete what that user could delete themselves,
    // and a junction planted in a cache folder can't redirect the engine's own rights anywhere.
    // Machine-wide changes are only allowed for administrators.
    public class ClientContext
    {
        public string Sid { get; init; } = "";
        public bool IsAdministrator { get; init; }
        public int SessionId { get; init; } = -1;
        public UserFolders Folders { get; init; } = new();

        // The client's token, when the request arrived over the pipe. Null means "this process's
        // own user" (console mode and tests), in which case RunAsUser just runs the action.
        private WindowsIdentity? Identity { get; init; }

        public static ClientContext ForCurrentProcess()
        {
            using var me = WindowsIdentity.GetCurrent();
            return new ClientContext
            {
                Sid = me.User?.Value ?? "",
                IsAdministrator = IsAdministratorToken(me),
                SessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId,
                Folders = UserFolders.ForCurrentProcess(),
            };
        }

        // Builds the context for the connected client. The client must have opened the pipe with
        // TokenImpersonationLevel.Impersonation (the UI does) — that is what lets the engine act as
        // that user rather than with its own rights.
        public static ClientContext FromPipe(NamedPipeServerStream pipe)
        {
            WindowsIdentity? identity = null;
            pipe.RunAsClient(() => identity = WindowsIdentity.GetCurrent(TokenAccessLevels.MaximumAllowed));
            if (identity?.User == null)
                throw new UnauthorizedAccessException("Could not identify the connecting user.");

            string sid = identity.User.Value;
            bool sameUser = string.Equals(sid, WindowsIdentity.GetCurrent().User?.Value, StringComparison.OrdinalIgnoreCase);

            return new ClientContext
            {
                Sid = sid,
                IsAdministrator = IsAdministratorToken(identity),
                SessionId = GetClientSessionId(pipe),
                Folders = (sameUser ? UserFolders.ForCurrentProcess() : UserFolders.ForSid(sid))
                          ?? throw new InvalidOperationException("The connecting user's profile could not be found."),
                Identity = identity,
            };
        }

        public void RunAsUser(Action action)
        {
            if (Identity == null) action();
            else WindowsIdentity.RunImpersonated(Identity.AccessToken, action);
        }

        public T RunAsUser<T>(Func<T> func)
        {
            if (Identity == null) return func();
            return WindowsIdentity.RunImpersonated(Identity.AccessToken, func);
        }

        // This user's HKEY_CURRENT_USER, wherever the engine runs. Dispose the handle when done (it
        // leaves the process-wide Registry.CurrentUser open). Key is null if the hive isn't loaded.
        public UserHiveHandle OpenUserHive(bool writable = false)
        {
            string? me = WindowsIdentity.GetCurrent().User?.Value;
            if (string.Equals(Sid, me, StringComparison.OrdinalIgnoreCase))
                return new UserHiveHandle(Registry.CurrentUser, owned: false);
            return new UserHiveHandle(Registry.Users.OpenSubKey(Sid, writable), owned: true);
        }

        // ── Administrator detection ─────────────────────────────────────────────
        // A UAC-filtered (non-elevated) admin token doesn't report membership via IsInRole, so the
        // elevation type is checked: "Full" or "Limited" means the account is an administrator.

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

        private const int TokenElevationType = 18;
        private const int TokenElevationTypeDefault = 1;

        internal static bool IsAdministratorToken(WindowsIdentity identity)
        {
            try
            {
                if (GetTokenInformation(identity.Token, TokenElevationType, out int type, sizeof(int), out _) &&
                    type != TokenElevationTypeDefault)
                    return true; // Split token: an administrator account, elevated or not.
            }
            catch
            {
            }
            // No split token: UAC is off, or this is a standard user / SYSTEM.
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
                   identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint sessionId);

        private static int GetClientSessionId(NamedPipeServerStream pipe)
        {
            try
            {
                return GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint id) ? (int)id : -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
