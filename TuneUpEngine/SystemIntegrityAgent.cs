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

    public class SystemIntegrityAgent
    {
        private const string CbsLogPath = @"C:\Windows\Logs\CBS\CBS.log";

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
                    // Read last 5000 lines to keep scan fast and relevant
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

        public void RunIntegrityRepair(Action<string, double> progressCallback)
        {
            try
            {
                // Stage 1: Running DISM RestoreHealth
                progressCallback?.Invoke("Running DISM System Repair...", 0.0);
                ExecuteCommand("dism.exe", "/Online /Cleanup-Image /RestoreHealth", (line) =>
                {
                    // Parse DISM progress, e.g. "[=================== 30.5% ===================]"
                    var match = Regex.Match(line, @"(\d+\.\d+)%");
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double percent))
                    {
                        progressCallback?.Invoke($"DISM Repair in Progress: {percent:F1}%", percent * 0.5); // Map to 0-50%
                    }
                });

                // Stage 2: Running SFC Scannow
                progressCallback?.Invoke("Running System File Checker (SFC)...", 50.0);
                ExecuteCommand("sfc.exe", "/scannow", (line) =>
                {
                    // Parse SFC progress, e.g. "Verification 45% complete."
                    var match = Regex.Match(line, @"Verification\s+(\d+)%\s+complete", RegexOptions.IgnoreCase);
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double percent))
                    {
                        progressCallback?.Invoke($"SFC Verification in Progress: {percent:F0}%", 50.0 + (percent * 0.5)); // Map to 50-100%
                    }
                });

                progressCallback?.Invoke("System Integrity Repair Complete", 100.0);
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke($"Repair failed: {ex.Message}", -1.0);
            }
        }

        private void ExecuteCommand(string exe, string args, Action<string> outputLineCallback)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Verb = "runas" // Elevate privilege
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.Start();

                // Read output line by line
                while (!process.StandardOutput.EndOfStream)
                {
                    string line = process.StandardOutput.ReadLine();
                    if (!string.IsNullOrEmpty(line))
                    {
                        outputLineCallback?.Invoke(line);
                    }
                }

                process.WaitForExit();
            }
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
