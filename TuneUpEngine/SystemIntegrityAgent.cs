using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TuneUpEngine
{
    public class IntegrityReport
    {
        public bool HasCorruption { get; set; }
        public List<string> CorruptionLogs { get; set; } = new List<string>();
        public DateTime ScanTime { get; set; }
    }

    public record RepairResult(bool Success, string Message);

    public class SystemIntegrityAgent
    {
        private static readonly string CbsLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS", "CBS.log");

        // Only the end of CBS.log is relevant (it grows to hundreds of MB); reading it whole on every
        // scan was slow and memory-hungry.
        internal const long CbsTailBytes = 8L * 1024 * 1024;

        public IntegrityReport ScanIntegrityLogs()
        {
            var report = new IntegrityReport
            {
                HasCorruption = false,
                ScanTime = DateTime.Now
            };

            if (!File.Exists(CbsLogPath))
            {
                report.CorruptionLogs.Add("CBS.log was not found. System integrity scan has never been executed.");
                return report;
            }

            try
            {
                // Open with FileShare.ReadWrite to avoid locking CBS.log
                using (var fs = new FileStream(CbsLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    if (fs.Length > CbsTailBytes)
                    {
                        fs.Seek(-CbsTailBytes, SeekOrigin.End);
                        reader.DiscardBufferedData();
                        reader.ReadLine(); // Skip the partial line we landed in
                    }

                    // Keep the last 5000 lines of that tail
                    var lastLines = new Queue<string>(5000);
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (lastLines.Count >= 5000)
                        {
                            lastLines.Dequeue();
                        }
                        lastLines.Enqueue(line);
                    }

                    // Parse lines for corruption indicators
                    foreach (var logLine in lastLines)
                    {
                        // Match sfc/dism corruption flags
                        if (logLine.Contains("[SR] Cannot repair member file") || 
                            logLine.Contains("[SR] Could not reproject corrupted file") ||
                            logLine.Contains("Failed to repair") ||
                            (logLine.Contains("Corrupt") && logLine.Contains("Repair")) ||
                            logLine.Contains("CSI Payload Corrupt"))
                        {
                            report.HasCorruption = true;
                            // Clean up line to make it human readable in the UI
                            string cleanLine = CleanLogLine(logLine);
                            if (!report.CorruptionLogs.Contains(cleanLine))
                            {
                                report.CorruptionLogs.Add(cleanLine);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.CorruptionLogs.Add($"Error reading integrity logs: {ex.Message}");
            }

            if (!report.HasCorruption && report.CorruptionLogs.Count == 0)
            {
                report.CorruptionLogs.Add("No system file corruptions detected in recent CBS logs.");
            }

            return report;
        }

        // Runs DISM RestoreHealth then SFC, and reports what actually happened. It used to report
        // "complete" regardless of exit codes, so a failed repair looked like a successful one.
        public RepairResult RunIntegrityRepair(Action<string, double> progressCallback)
        {
            try
            {
                // Stage 1: DISM RestoreHealth (repairs the component store SFC restores files from)
                progressCallback?.Invoke("Running DISM System Repair...", 0.0);
                int dismExit = ExecuteCommand("dism.exe", "/Online /Cleanup-Image /RestoreHealth", (line) =>
                {
                    // Parse DISM progress, e.g. "[=================== 30.5% ===================]"
                    var match = Regex.Match(line, @"(\d+\.\d+)%");
                    if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent))
                    {
                        progressCallback?.Invoke($"DISM Repair in Progress: {percent:F1}%", percent * 0.5); // Map to 0-50%
                    }
                });

                // Stage 2: SFC. sfc.exe writes UTF-16; without the matching encoding the text comes
                // back full of NUL bytes and neither progress nor the verdict can be read.
                progressCallback?.Invoke("Running System File Checker (SFC)...", 50.0);
                var sfcOutput = new StringBuilder();
                int sfcExit = ExecuteCommand("sfc.exe", "/scannow", (line) =>
                {
                    sfcOutput.AppendLine(line);
                    var match = Regex.Match(line, @"Verification\s+(\d+)%\s+complete", RegexOptions.IgnoreCase);
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double percent))
                    {
                        progressCallback?.Invoke($"SFC Verification in Progress: {percent:F0}%", 50.0 + (percent * 0.5)); // Map to 50-100%
                    }
                }, System.Text.Encoding.Unicode);

                var result = Summarize(dismExit, sfcExit, sfcOutput.ToString());
                progressCallback?.Invoke(result.Success ? "System Integrity Repair Complete" : "System Integrity Repair Finished With Problems", 100.0);
                return result;
            }
            catch (Exception ex)
            {
                return new RepairResult(false, $"Repair could not run: {ex.Message}");
            }
        }

        // DISM: 0 = success, 3010 = success but a restart is needed; anything else failed.
        // SFC: its exit code is unreliable, so its final verdict line is read (English Windows);
        // on other languages the exit code is used.
        internal static RepairResult Summarize(int dismExit, int sfcExit, string sfcOutput)
        {
            var parts = new List<string>();
            bool ok = true;

            if (dismExit == 0) parts.Add("DISM: component store is healthy or was repaired.");
            else if (dismExit == 3010) parts.Add("DISM: repaired — restart required.");
            else { ok = false; parts.Add($"DISM failed (exit code {dismExit}). Check C:\\Windows\\Logs\\DISM\\dism.log."); }

            string sfc = sfcOutput.ToLowerInvariant();
            if (sfc.Contains("did not find any integrity violations"))
                parts.Add("SFC: no integrity violations found.");
            else if (sfc.Contains("successfully repaired"))
                parts.Add("SFC: found corrupt files and repaired them.");
            else if (sfc.Contains("unable to fix some of them"))
            { ok = false; parts.Add("SFC: found corrupt files it could NOT fully repair. Details are in CBS.log."); }
            else if (sfc.Contains("could not perform the requested operation") || sfc.Contains("pending system repair"))
            { ok = false; parts.Add("SFC could not run (a repair may be pending — restart and try again)."); }
            else if (sfcExit != 0)
            { ok = false; parts.Add($"SFC failed (exit code {sfcExit})."); }
            else
                parts.Add("SFC: completed.");

            return new RepairResult(ok, string.Join(" ", parts));
        }

        private int ExecuteCommand(string exe, string args, Action<string> outputLineCallback,
            System.Text.Encoding? outputEncoding = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
                // NOTE: no Verb is set. A verb such as "runas" only applies when UseShellExecute is
                // true; with UseShellExecute=false it is silently ignored. The engine already runs
                // elevated, so DISM/SFC inherit its administrator token without any verb.
            };

            // Some tools (notably sfc.exe) emit UTF-16; the caller can specify the matching encoding
            // so the redirected text decodes correctly instead of arriving riddled with NUL bytes.
            if (outputEncoding != null)
            {
                psi.StandardOutputEncoding = outputEncoding;
                psi.StandardErrorEncoding = outputEncoding;
            }

            using var process = new Process { StartInfo = psi };

            // Both stdout AND stderr must be drained concurrently. The previous code read only stdout
            // to EOF; if the child wrote enough to stderr to fill that pipe's buffer, the child would
            // block on the stderr write while we blocked reading stdout — a classic redirection
            // deadlock that hangs the repair indefinitely. Reading both via the async events lets the
            // thread pool keep both pipes drained.
            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    outputLineCallback?.Invoke(e.Data);
            };
            process.ErrorDataReceived += (s, e) =>
            {
                // Surface stderr through the same callback (so failures are visible in the progress
                // stream) and, crucially, keep the stderr pipe continuously drained.
                if (!string.IsNullOrEmpty(e.Data))
                    outputLineCallback?.Invoke(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Parameterless WaitForExit() also waits for the async output handlers to finish, so all
            // buffered output is delivered before this method returns.
            process.WaitForExit();
            return process.ExitCode;
        }

        private string CleanLogLine(string rawLine)
        {
            // Extract the timestamp and actual message, stripping out internal CSI IDs
            var match = Regex.Match(rawLine, @"^([\d\-:\s]+),\s+Info\s+[\s\w]+\[SR\]\s+(.*)$");
            if (match.Success)
            {
                return $"[{match.Groups[1].Value}] {match.Groups[2].Value}";
            }

            // Fallback: Strip common prefix clutter
            string pattern = @"(?:\d{4}-\d{2}-\d{2}\s\d{2}:\d{2}:\d{2},\sInfo\s+CSI\s+\d+\s+)";
            return Regex.Replace(rawLine, pattern, "").Trim();
        }
    }
}
