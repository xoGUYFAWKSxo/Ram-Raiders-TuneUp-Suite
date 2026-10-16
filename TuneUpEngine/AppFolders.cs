using System;
using System.Collections.Concurrent;
using System.IO;

namespace TuneUpEngine
{
    // The app's data folder under ProgramData, AppData or LocalAppData. The app was called
    // "Ram Raiders TuneUp Suite" until 2026-10-16, and its folders were named after that. The old
    // folder holds the change journal (the record uninstall uses to undo everything) and the
    // registry backups, so it is moved to the new name the first time it is needed, never left
    // behind. If the move fails (a file in it is in use), the old folder is used as it is.
    public static class AppFolders
    {
        public const string Name = "GalacticTuneUp";
        public const string LegacyName = "RamRaidersTuneUpSuite";

        private static readonly ConcurrentDictionary<string, string> Resolved = new(StringComparer.OrdinalIgnoreCase);

        public static string Root(Environment.SpecialFolder folder) => Root(Environment.GetFolderPath(folder));

        public static string Root(string baseDir) => Resolved.GetOrAdd(baseDir, Resolve);

        internal static string Resolve(string baseDir)
        {
            string current = Path.Combine(baseDir, Name);
            string legacy = Path.Combine(baseDir, LegacyName);
            if (Directory.Exists(current) || !Directory.Exists(legacy)) return current;
            try
            {
                Directory.Move(legacy, current);
                return current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return legacy;
            }
        }
    }
}
