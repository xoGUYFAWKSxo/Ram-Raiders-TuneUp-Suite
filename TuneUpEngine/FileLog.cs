using System;
using System.IO;
using System.Linq;

namespace TuneUpEngine
{
    // A small, dependency-free file logger: one file per day ("<prefix>-yyyyMMdd.log"), capped in
    // size, with old days deleted automatically. Logging must never break the app, so every
    // failure here is swallowed (a log that can't be written simply isn't).
    public sealed class FileLog
    {
        private readonly string _directory;
        private readonly string _prefix;
        private readonly long _maxBytesPerDay;
        private readonly object _lock = new();
        private string? _cappedFile; // the day's file that hit the size cap (noted once)

        public bool Enabled { get; }
        public string Directory => _directory;

        public FileLog(string directory, string prefix, long maxBytesPerDay = 10 * 1024 * 1024, int keepDays = 7)
        {
            _directory = directory;
            _prefix = prefix;
            _maxBytesPerDay = maxBytesPerDay;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                DeleteOldFiles(keepDays);
                Enabled = true;
            }
            catch
            {
                Enabled = false;
            }
        }

        public string CurrentFile => Path.Combine(_directory, $"{_prefix}-{DateTime.Now:yyyyMMdd}.log");

        public void Write(string level, string message)
        {
            if (!Enabled) return;
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                try
                {
                    string file = CurrentFile;
                    var info = new FileInfo(file);
                    if (info.Exists && info.Length >= _maxBytesPerDay)
                    {
                        if (_cappedFile != file)
                        {
                            _cappedFile = file;
                            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN] Log size limit reached; further messages today are dropped.{Environment.NewLine}");
                        }
                        return;
                    }
                    File.AppendAllText(file, line);
                }
                catch
                {
                    // Disk full, file locked, no permission — never let logging take the app down.
                }
            }
        }

        private void DeleteOldFiles(int keepDays)
        {
            var cutoff = DateTime.Now.Date.AddDays(-keepDays);
            foreach (var file in new DirectoryInfo(_directory).GetFiles($"{_prefix}-*.log").Where(f => f.LastWriteTime < cutoff))
            {
                try { file.Delete(); } catch { }
            }
        }
    }

    // The engine's log. Writes to %ProgramData%\GalacticTuneUp\logs\engine-yyyyMMdd.log (the
    // folder the change journal locks down to administrators; everyone can read it, which helps
    // when reporting a problem) and echoes to the console. Debug messages — per-request chatter —
    // go to the console only, unless TUNEUP_DEBUG_LOG=1 is set.
    public static class EngineLog
    {
        private static FileLog? _file;
        private static readonly bool DebugToFile =
            Environment.GetEnvironmentVariable("TUNEUP_DEBUG_LOG") == "1";

        public static string DefaultDirectory => Path.Combine(
            AppFolders.Root(Environment.SpecialFolder.CommonApplicationData), "logs");

        public static string? CurrentFile => _file is { Enabled: true } f ? f.CurrentFile : null;

        public static void Initialize(string? directory = null) =>
            _file = new FileLog(directory ?? DefaultDirectory, "engine");

        public static void Info(string message) => Write("INFO", message, toFile: true);
        public static void Warn(string message) => Write("WARN", message, toFile: true);
        public static void Error(string message) => Write("ERROR", message, toFile: true);
        public static void Debug(string message) => Write("DEBUG", message, toFile: DebugToFile);

        private static void Write(string level, string message, bool toFile)
        {
            try { Console.WriteLine(level == "INFO" || level == "DEBUG" ? message : $"[{level}] {message}"); } catch { }
            if (toFile) _file?.Write(level, message);
        }
    }
}
