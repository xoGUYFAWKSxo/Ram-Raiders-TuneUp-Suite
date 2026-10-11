using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TuneUpEngine
{
    // Decides whether the process on the other end of the pipe is the real TuneUp UI.
    //
    // The pipe ACL already limits connections to the user who launched the engine. On top of that,
    // the engine only talks to a TuneUpUI.exe that sits next to the engine itself — in an installed
    // copy that folder is under Program Files and only administrators can write there, so another
    // program can't simply drop its own "TuneUpUI.exe" alongside. (A development build also accepts
    // the UI from the solution's own build output, see TrustedClientPaths.)
    public static class ClientVerifier
    {
        public const string UiExecutableName = "TuneUpUI.exe";

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // Returns the full executable path of the connected client, or null if it can't be determined.
        public static string? GetClientImagePath(NamedPipeServerStream pipe)
        {
            try
            {
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid == 0)
                    return null;

                IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProcess == IntPtr.Zero) return null;
                try
                {
                    var buffer = new StringBuilder(1024);
                    uint size = (uint)buffer.Capacity;
                    return QueryFullProcessImageName(hProcess, 0, buffer, ref size)
                        ? buffer.ToString(0, (int)size)
                        : null;
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            catch
            {
                return null;
            }
        }

        // The UI locations the engine trusts, derived from where the engine itself lives:
        //   1. <engine folder>\TuneUpUI.exe — the installed layout, and also how the UI normally
        //      launches the engine (the UI's build output contains a copy of the engine).
        //   2. For an engine started from its own build output
        //      (...\TuneUpEngine\bin\<Config>\<TFM>\), the UI built from the same solution
        //      (...\TuneUpUI\bin\<Config>\<TFM>\TuneUpUI.exe).
        internal static IReadOnlyList<string> TrustedClientPaths(string engineDirectory)
        {
            var paths = new List<string>();
            if (string.IsNullOrWhiteSpace(engineDirectory)) return paths;

            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(engineDirectory));
            paths.Add(Path.Combine(dir, UiExecutableName));

            var tfmDir = new DirectoryInfo(dir);
            var configDir = tfmDir.Parent;
            var binDir = configDir?.Parent;
            var projectDir = binDir?.Parent;
            var solutionDir = projectDir?.Parent;

            if (binDir != null && projectDir != null && solutionDir != null &&
                binDir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                projectDir.Name.Equals("TuneUpEngine", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(Path.Combine(solutionDir.FullName, "TuneUpUI", "bin", configDir!.Name, tfmDir.Name, UiExecutableName));
            }

            return paths;
        }

        internal static bool IsTrustedClient(string? clientImagePath, IReadOnlyList<string> trustedPaths)
        {
            if (string.IsNullOrWhiteSpace(clientImagePath)) return false;

            string client;
            try { client = Path.GetFullPath(clientImagePath); }
            catch { return false; }

            return trustedPaths.Any(t => t.Equals(client, StringComparison.OrdinalIgnoreCase));
        }
    }
}
