using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
                report.CorruptionLogs.Add("Windows hasn't made a repair log yet, so there is nothing to report. Click Repair Windows Files to run a check.");
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
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (lastLines.Count >= 5000)
                        {
                            lastLines.Dequeue();
                        }
                        lastLines.Enqueue(line);
                    }

                    report = Interpret(lastLines);
                }
            }
            catch (Exception ex)
            {
                report.CorruptionLogs.Add($"Couldn't read the Windows repair log: {ex.Message}");
            }

            return report;
        }

        // Turns the CBS.log tail into plain-English results. Only the latest DISM summary and the
        // latest SFC run count: matching any line with "Corrupt" and "Repair" in it flagged a
        // successful repair ("Total Repaired Corruption: 876") as corruption.
        internal static IntegrityReport Interpret(IEnumerable<string> lines)
        {
            var report = new IntegrityReport { ScanTime = DateTime.Now };

            DateTime? dismTime = null, sfcTime = null;
            bool inSummary = false, dismRepairs = false;
            int detected = 0, repaired = 0, fileFlags = 0;
            string dismResult = "0x0";
            int sfcRepairing = 0, pendingRepairing = 0;
            var pendingBad = new List<string>();
            var sfcBad = new List<string>();

            foreach (var line in lines)
            {
                if (line.Contains("Checking System Update Readiness."))
                {
                    dismTime = TimeOf(line);
                    inSummary = true;
                    dismRepairs = false;
                    detected = repaired = fileFlags = 0;
                    dismResult = "0x0";
                }
                else if (inSummary)
                {
                    if (line.Contains("Operation: ")) dismRepairs = line.Contains("Repair");
                    else if (line.Contains("Operation result:")) dismResult = Regex.Match(line, @"0x[0-9A-Fa-f]+").Value;
                    else if (line.Contains("Total Detected Corruption:")) detected = CountIn(line);
                    else if (line.Contains("CSI FileFlags Corrupt:")) fileFlags = CountIn(line);
                    else if (line.Contains("Total Repaired Corruption:")) repaired = CountIn(line);
                    else if (line.Contains("Total Operation Time:") || line.Contains("Exec: Processing complete")) inSummary = false;
                }

                if (line.Contains("[SR] Cannot repair member file") || line.Contains("[SR] Could not reproject corrupted file"))
                {
                    pendingBad.Add(FileNameIn(line));
                }
                else if (Regex.Match(line, @"\[SR\] Repairing (\d+) components") is { Success: true } m)
                {
                    pendingRepairing = int.Parse(m.Groups[1].Value);
                }
                else if (line.Contains("[SR] Repair complete"))
                {
                    sfcTime = TimeOf(line);
                    sfcRepairing = pendingRepairing;
                    sfcBad = pendingBad.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    pendingBad.Clear();
                    pendingRepairing = 0;
                }
            }

            if (dismTime is DateTime d)
            {
                string on = $"On {When(d)}, Windows checked its backup copy of system files";
                if (!IsZero(dismResult))
                {
                    report.HasCorruption = true;
                    report.CorruptionLogs.Add($"{on}, but the check didn't finish (error {dismResult}). Run the repair again.");
                }
                else if (detected == 0)
                    report.CorruptionLogs.Add($"{on}. No problems found.");
                else if (repaired >= detected)
                    report.CorruptionLogs.Add($"{on} and fixed all {Plural(detected, "problem")} it found."
                        + (fileFlags == detected ? " They were minor file-setting mismatches, not damaged files." : "")
                        + " Nothing is left to fix.");
                else if (!dismRepairs)
                {
                    report.HasCorruption = true;
                    report.CorruptionLogs.Add($"{on} and found {Plural(detected, "problem")}. They haven't been fixed yet.");
                }
                else
                {
                    report.HasCorruption = true;
                    report.CorruptionLogs.Add($"{on} and found {Plural(detected, "problem")}, but could only fix {repaired}. {detected - repaired} still need fixing.");
                }
            }

            if (sfcTime is DateTime s)
            {
                string on = $"On {When(s)}, Windows checked its own program files";
                if (sfcBad.Count > 0)
                {
                    report.HasCorruption = true;
                    string names = string.Join(", ", sfcBad.Take(5)) + (sfcBad.Count > 5 ? $" and {sfcBad.Count - 5} more" : "");
                    report.CorruptionLogs.Add($"{on} and found {Plural(sfcBad.Count, "damaged file")} it couldn't fix: {names}.");
                }
                else if (sfcRepairing > 0)
                    report.CorruptionLogs.Add($"{on}, found some damaged files and fixed them.");
                else
                    report.CorruptionLogs.Add($"{on}. No damaged files found.");
            }

            if (dismTime == null && sfcTime == null)
                report.CorruptionLogs.Add("Windows hasn't checked its system files recently, so there is nothing to report. Click Repair Windows Files to run a check.");

            return report;
        }

        private static DateTime? TimeOf(string line) =>
            DateTime.TryParseExact(line.Length >= 19 ? line[..19] : line, "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

        private static string When(DateTime t) => t.ToString("d MMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture);

        private static int CountIn(string line) =>
            Regex.Match(line, @":\s*(\d+)\s*$") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

        private static bool IsZero(string hr) =>
            hr.Length > 2 && long.TryParse(hr.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v == 0;

        // SFC names the file in quotes, e.g. [l:34{17}]"Amd64\CNBJ2530.DPB"
        private static string FileNameIn(string line) =>
            Regex.Match(line, "\"([^\"]+)\"") is { Success: true } m ? Path.GetFileName(m.Groups[1].Value) : "an unnamed file";

        private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

        // Runs DISM RestoreHealth then SFC, and reports what actually happened. It used to report
        // "complete" regardless of exit codes, so a failed repair looked like a successful one.
        public RepairResult RunIntegrityRepair(Action<string, double> progressCallback)
        {
            try
            {
                // Stage 1: DISM RestoreHealth (repairs the component store SFC restores files from)
                progressCallback?.Invoke("Step 1 of 2: repairing Windows' backup copy of system files...", 0.0);
                int dismExit = ExecuteCommand("dism.exe", "/Online /Cleanup-Image /RestoreHealth", (line) =>
                {
                    // Parse DISM progress, e.g. "[=================== 30.5% ===================]"
                    var match = Regex.Match(line, @"(\d+\.\d+)%");
                    if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent))
                    {
                        progressCallback?.Invoke($"Step 1 of 2: repairing Windows' backup copy of system files ({percent:F0}%)", percent * 0.5); // Map to 0-50%
                    }
                });

                // Stage 2: SFC. sfc.exe writes UTF-16; without the matching encoding the text comes
                // back full of NUL bytes and neither progress nor the verdict can be read.
                progressCallback?.Invoke("Step 2 of 2: checking Windows' own program files...", 50.0);
                var sfcOutput = new StringBuilder();
                int sfcExit = ExecuteCommand("sfc.exe", "/scannow", (line) =>
                {
                    sfcOutput.AppendLine(line);
                    var match = Regex.Match(line, @"Verification\s+(\d+)%\s+complete", RegexOptions.IgnoreCase);
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double percent))
                    {
                        progressCallback?.Invoke($"Step 2 of 2: checking Windows' own program files ({percent:F0}%)", 50.0 + (percent * 0.5)); // Map to 50-100%
                    }
                }, System.Text.Encoding.Unicode);

                var result = Summarize(dismExit, sfcExit, sfcOutput.ToString());
                progressCallback?.Invoke(result.Success ? "Repair finished." : "Repair finished, but some problems are left.", 100.0);
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

            if (dismExit == 0) parts.Add("Windows' backup copy of its system files is healthy.");
            else if (dismExit == 3010) parts.Add("Windows repaired its backup copy of system files. Restart your PC to finish.");
            else { ok = false; parts.Add($"Windows couldn't repair its backup copy of system files (DISM error code {dismExit}). Details are in C:\\Windows\\Logs\\DISM\\dism.log."); }

            string sfc = sfcOutput.ToLowerInvariant();
            if (sfc.Contains("did not find any integrity violations"))
                parts.Add("No damaged Windows files were found.");
            else if (sfc.Contains("successfully repaired"))
                parts.Add("Some damaged Windows files were found and fixed.");
            else if (sfc.Contains("unable to fix some of them"))
            { ok = false; parts.Add("Some damaged Windows files were found that could NOT be fixed. Details are in C:\\Windows\\Logs\\CBS\\CBS.log."); }
            else if (sfc.Contains("could not perform the requested operation") || sfc.Contains("pending system repair"))
            { ok = false; parts.Add("The file check couldn't run, probably because an earlier repair is waiting for a restart. Restart your PC and try again."); }
            else if (sfcExit != 0)
            { ok = false; parts.Add($"The file check failed (SFC error code {sfcExit})."); }
            else
                parts.Add("The file check finished.");

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
    }
}
