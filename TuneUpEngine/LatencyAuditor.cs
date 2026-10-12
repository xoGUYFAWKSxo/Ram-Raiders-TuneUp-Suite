using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace TuneUpEngine
{
    // One CPU core's measured driver load: the share of its time spent running DPCs (deferred
    // procedure calls) and interrupt service routines — the work drivers do at elevated priority,
    // during which nothing else (games, audio) can run on that core.
    public class CoreLatencyRecord
    {
        public string Name { get; set; } = "";
        public double CurrentDpcPercent { get; set; }
        public double CurrentInterruptPercent { get; set; }
        // Highest DPC + interrupt load averaged over WindowSamples readings, and its two parts
        // from that same window, so the columns always add up to the total.
        public double PeakTotalPercent { get; set; }
        public double PeakDpcPercent { get; set; }
        public double PeakInterruptPercent { get; set; }
        // The latest window average; what a spike event reports.
        public double WindowDpcPercent { get; set; }
        public double WindowInterruptPercent { get; set; }
        public double AvgDpcPercent { get; set; }
        public double AvgInterruptPercent { get; set; }
        public int HighSamples { get; set; }
        public string Status { get; set; } = "OK";   // OK | ELEVATED | HIGH
        public bool IsSpike { get; set; }

        internal double DpcSum;
        internal double InterruptSum;
        internal int SampleCount;
        internal readonly Queue<(double Dpc, double Isr)> Window = new();
    }

    public class LatencyReport
    {
        public bool IsRunning { get; set; }
        public bool CountersAvailable { get; set; }
        public int SampleCount { get; set; }
        public double TotalDpcPercent { get; set; }
        public double TotalInterruptPercent { get; set; }
        public double DpcsQueuedPerSec { get; set; }
        public double InterruptsPerSec { get; set; }
        public List<CoreLatencyRecord> Cores { get; set; } = new();
        // Per-driver results from the kernel trace; empty when the trace couldn't start.
        public bool DriverTraceAvailable { get; set; }
        public string DriverTraceMessage { get; set; } = "";
        public long DriverEventsLost { get; set; }
        public List<DriverLatencyRecord> Drivers { get; set; } = new();
        public string Summary { get; set; } = "";
    }

    // One reading of the counters: per-core DPC / interrupt time (percent of that core's time)
    // and machine-wide totals.
    public record LatencySample(
        double[] DpcPercent,
        double[] InterruptPercent,
        double TotalDpcPercent,
        double TotalInterruptPercent,
        double DpcsQueuedPerSec,
        double InterruptsPerSec);

    public interface ILatencyCounterSource : IDisposable
    {
        bool TryInitialize(out string? error);
        LatencySample Sample();
    }

    // Measures driver DPC / interrupt load from Windows performance counters.
    //
    // What this replaced: per-driver "DPC microseconds" that were never measured — total interrupt
    // time multiplied by a fixed share per driver name (GPU 35%, network 20%, ...), so every driver
    // on the list "spiked" together. Windows performance counters can't attribute DPC time to a
    // particular driver (that needs a kernel trace, as LatencyMon or Windows Performance Analyzer
    // do), so this reports what they really measure: each core's share of time spent servicing
    // drivers, with real peaks and averages. A kernel trace (DriverLatencyTracer.cs), run
    // alongside, names the drivers and times each of their runs.
    public class LatencyAuditor
    {
        // Share of one core's time spent in DPC + interrupt work.
        internal const double ElevatedPercent = 3.0;
        internal const double HighPercent = 10.0;
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

        // Windows charges DPC/interrupt time a whole clock tick (15.6 ms) at a time, so a single
        // 1-second reading moves in 1.56% steps: one unlucky tick reads as 1.6%, two as 3.1%.
        // Peaks and status use a 5-second average (0.3% steps) so one tick can't flag a core.
        internal const int WindowSamples = 5;

        private readonly Func<ILatencyCounterSource> _sourceFactory;
        private readonly Func<IDriverTraceSource>? _traceFactory;
        private ILatencyCounterSource? _source;
        private IDriverTraceSource? _trace;
        private DriverLatencyStats? _driverStats;
        private string _driverTraceMessage = "";
        private Thread? _auditThread;
        private volatile bool _isRunning;
        private readonly object _lock = new();
        private readonly List<CoreLatencyRecord> _cores = new();
        private LatencySample? _lastSample;
        private int _sampleCount;
        private bool _countersAvailable;

        public event Action<CoreLatencyRecord>? OnSpikeDetected;
        public event Action<string>? OnLogMessage;

        public bool IsRunning => _isRunning;

        public LatencyAuditor(Func<ILatencyCounterSource>? sourceFactory = null)
            // Tests that supply counters get no kernel trace unless they ask for one.
            : this(sourceFactory, sourceFactory == null ? () => new EtwDriverTraceSource() : null)
        {
        }

        internal LatencyAuditor(Func<ILatencyCounterSource>? sourceFactory, Func<IDriverTraceSource>? traceFactory)
        {
            _sourceFactory = sourceFactory ?? (() => new PerformanceCounterLatencySource());
            _traceFactory = traceFactory;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_isRunning) return;

                _source = _sourceFactory();
                if (!_source.TryInitialize(out string? error))
                {
                    _source.Dispose();
                    _source = null;
                    _countersAvailable = false;
                    OnLogMessage?.Invoke($"[LatencyAuditor] Performance counters unavailable: {error}");
                    return;
                }

                _countersAvailable = true;
                _cores.Clear();
                _lastSample = null;
                _sampleCount = 0;
                _isRunning = true;
                StartDriverTrace();
            }

            _auditThread = new Thread(AuditLoop) { IsBackground = true, Name = "LatencyAuditor" };
            _auditThread.Start();
            OnLogMessage?.Invoke("[LatencyAuditor] Driver DPC / interrupt monitoring started.");
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning) return;
                _isRunning = false;
                // Disposed under the lock, so the sampling thread can't be mid-read.
                _source?.Dispose();
                _source = null;
                // Results stay readable after Stop; only the session ends.
                _trace?.Dispose();
                _trace = null;
            }
            OnLogMessage?.Invoke("[LatencyAuditor] Driver DPC / interrupt monitoring stopped.");
        }

        // Caller holds _lock. A trace failure leaves the counter results working on their own.
        private void StartDriverTrace()
        {
            _driverStats = null;
            _driverTraceMessage = "";
            if (_traceFactory == null) return;

            var stats = new DriverLatencyStats();
            var trace = _traceFactory();
            if (trace.TryStart(stats, out string? error))
            {
                _trace = trace;
                _driverStats = stats;
                OnLogMessage?.Invoke($"[LatencyAuditor] Driver trace started ({stats.ImageCount} drivers already loaded).");
            }
            else
            {
                trace.Dispose();
                _driverTraceMessage = $"Couldn't start the driver trace: {error}";
                OnLogMessage?.Invoke($"[LatencyAuditor] {_driverTraceMessage}");
            }
        }

        public LatencyReport GetReport()
        {
            lock (_lock)
            {
                var report = new LatencyReport
                {
                    IsRunning = _isRunning,
                    CountersAvailable = _countersAvailable,
                    SampleCount = _sampleCount,
                    TotalDpcPercent = _lastSample?.TotalDpcPercent ?? 0,
                    TotalInterruptPercent = _lastSample?.TotalInterruptPercent ?? 0,
                    DpcsQueuedPerSec = _lastSample?.DpcsQueuedPerSec ?? 0,
                    InterruptsPerSec = _lastSample?.InterruptsPerSec ?? 0,
                    Cores = _cores
                        .OrderByDescending(c => c.PeakTotalPercent)
                        .Take(16)
                        .Select(Copy)
                        .ToList(),
                    DriverTraceAvailable = _driverStats != null,
                    DriverTraceMessage = _driverTraceMessage,
                    DriverEventsLost = _trace?.EventsLost ?? 0,
                    Drivers = _driverStats?.Snapshot(15) ?? new List<DriverLatencyRecord>(),
                };
                report.Summary = BuildSummary(report, _cores);
                return report;
            }
        }

        private void AuditLoop()
        {
            while (_isRunning)
            {
                // Sleep first: TryInitialize has just taken the priming reading, and a rate read
                // microseconds later turns one clock tick into hundreds of percent (772% seen on CI).
                Thread.Sleep(SampleInterval);
                if (!_isRunning) break;

                try
                {
                    LatencySample? sample = null;
                    lock (_lock)
                    {
                        if (_source != null) sample = _source.Sample();
                    }
                    if (sample != null) ApplySample(sample);
                }
                catch (Exception ex)
                {
                    EngineLog.Warn($"[LatencyAuditor] Sampling error: {ex.Message}");
                }
            }
        }

        // Folds one reading into the per-core statistics. Internal so tests can feed known numbers.
        internal void ApplySample(LatencySample sample)
        {
            var spikes = new List<CoreLatencyRecord>();

            lock (_lock)
            {
                _lastSample = sample;
                _sampleCount++;

                int cores = Math.Min(sample.DpcPercent.Length, sample.InterruptPercent.Length);
                while (_cores.Count < cores)
                    _cores.Add(new CoreLatencyRecord { Name = $"CPU {_cores.Count}" });

                for (int i = 0; i < cores; i++)
                {
                    var core = _cores[i];
                    // A share of one core's time can't leave 0-100%; anything else is a bad read.
                    double dpc = Math.Clamp(sample.DpcPercent[i], 0, 100);
                    double isr = Math.Clamp(sample.InterruptPercent[i], 0, 100);

                    core.CurrentDpcPercent = dpc;
                    core.CurrentInterruptPercent = isr;
                    core.DpcSum += dpc;
                    core.InterruptSum += isr;
                    core.SampleCount++;
                    core.AvgDpcPercent = core.DpcSum / core.SampleCount;
                    core.AvgInterruptPercent = core.InterruptSum / core.SampleCount;

                    core.Window.Enqueue((dpc, isr));
                    if (core.Window.Count > WindowSamples) core.Window.Dequeue();
                    if (core.Window.Count < WindowSamples) continue;

                    double windowDpc = core.Window.Average(w => w.Dpc);
                    double windowIsr = core.Window.Average(w => w.Isr);
                    core.WindowDpcPercent = windowDpc;
                    core.WindowInterruptPercent = windowIsr;
                    if (windowDpc + windowIsr > core.PeakTotalPercent)
                    {
                        core.PeakTotalPercent = windowDpc + windowIsr;
                        core.PeakDpcPercent = windowDpc;
                        core.PeakInterruptPercent = windowIsr;
                    }

                    double peak = core.PeakTotalPercent;
                    core.Status = peak >= HighPercent ? "HIGH" : peak >= ElevatedPercent ? "ELEVATED" : "OK";
                    core.IsSpike = peak >= HighPercent;

                    if (windowDpc + windowIsr >= HighPercent)
                    {
                        core.HighSamples++;
                        // First occurrence, then every 30 high readings, so the log isn't flooded.
                        if (core.HighSamples == 1 || core.HighSamples % 30 == 0)
                            spikes.Add(Copy(core));
                    }
                }
            }

            foreach (var spike in spikes)
            {
                OnSpikeDetected?.Invoke(spike);
            }
        }

        internal static string BuildSummary(LatencyReport report, IReadOnlyList<CoreLatencyRecord> allCores)
        {
            if (!report.CountersAvailable)
                return "Windows performance counters are unavailable on this PC, so driver load can't be measured.";
            if (report.SampleCount < WindowSamples)
                return $"Measuring… results appear after {WindowSamples} seconds.";

            var high = allCores.Where(c => c.Status == "HIGH").Select(c => c.Name).ToList();
            var elevated = allCores.Where(c => c.Status == "ELEVATED").Select(c => c.Name).ToList();
            var worst = report.Drivers.FirstOrDefault();
            string attribution = !report.DriverTraceAvailable
                ? " This tool couldn't trace which driver it is — open \"What is this?\" for how to find and fix it."
                : worst != null
                    ? $" The longest single run was {worst.Driver} ({worst.Description}): {worst.HighestMicroseconds:N0} µs."
                    : "";

            if (high.Count > 0)
                return $"{JoinNames(high)} spent {HighPercent:F0}% or more of {(high.Count == 1 ? "its" : "their")} time on driver work over a 5-second stretch — enough to cause stutter or audio crackles.{attribution}";
            // Rare long runs crackle audio even when overall load is low, so they're reported on their own.
            if (worst != null && worst.Status == "HIGH")
                return $"Overall driver load is fine, but {worst.Driver} ({worst.Description}) once ran for {worst.HighestMicroseconds:N0} µs in one go — long enough to cause an audio crackle or a dropped frame.";
            if (elevated.Count > 0)
                return $"{JoinNames(elevated)} peaked above {ElevatedPercent:F0}% driver time. Usually harmless; worth a look only if you hear crackles or see stutter.{attribution}";
            if (worst != null && worst.Status == "ELEVATED")
                return $"Driver load stayed below {ElevatedPercent:F0}% on every core. Longest single run: {worst.Driver} ({worst.Description}), {worst.HighestMicroseconds:N0} µs — fine unless you hear crackles.";
            return $"Driver DPC/interrupt load stayed below {ElevatedPercent:F0}% on every core — healthy.";
        }

        private static string JoinNames(List<string> names) =>
            names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

        private static CoreLatencyRecord Copy(CoreLatencyRecord c) => new()
        {
            Name = c.Name,
            PeakTotalPercent = c.PeakTotalPercent,
            WindowDpcPercent = c.WindowDpcPercent,
            WindowInterruptPercent = c.WindowInterruptPercent,
            CurrentDpcPercent = c.CurrentDpcPercent,
            CurrentInterruptPercent = c.CurrentInterruptPercent,
            PeakDpcPercent = c.PeakDpcPercent,
            PeakInterruptPercent = c.PeakInterruptPercent,
            AvgDpcPercent = c.AvgDpcPercent,
            AvgInterruptPercent = c.AvgInterruptPercent,
            HighSamples = c.HighSamples,
            Status = c.Status,
            IsSpike = c.IsSpike,
        };
    }

    // The real counter source: "Processor" category, one instance per logical CPU plus _Total.
    public sealed class PerformanceCounterLatencySource : ILatencyCounterSource
    {
        private readonly List<PerformanceCounter> _dpc = new();
        private readonly List<PerformanceCounter> _isr = new();
        private PerformanceCounter? _totalDpc, _totalIsr, _dpcsQueued, _interrupts;

        public bool TryInitialize(out string? error)
        {
            error = null;
            try
            {
                var instances = new PerformanceCounterCategory("Processor").GetInstanceNames()
                    .Where(n => int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    .OrderBy(n => int.Parse(n, CultureInfo.InvariantCulture))
                    .ToList();
                if (instances.Count == 0)
                {
                    error = "no processor instances found";
                    return false;
                }

                foreach (var cpu in instances)
                {
                    _dpc.Add(new PerformanceCounter("Processor", "% DPC Time", cpu, readOnly: true));
                    _isr.Add(new PerformanceCounter("Processor", "% Interrupt Time", cpu, readOnly: true));
                }
                _totalDpc = new PerformanceCounter("Processor", "% DPC Time", "_Total", readOnly: true);
                _totalIsr = new PerformanceCounter("Processor", "% Interrupt Time", "_Total", readOnly: true);
                _dpcsQueued = new PerformanceCounter("Processor", "DPCs Queued/sec", "_Total", readOnly: true);
                _interrupts = new PerformanceCounter("Processor", "Interrupts/sec", "_Total", readOnly: true);

                // Rate counters need a first reading to compare against.
                Sample();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Dispose();
                return false;
            }
        }

        public LatencySample Sample() => new(
            _dpc.Select(c => (double)c.NextValue()).ToArray(),
            _isr.Select(c => (double)c.NextValue()).ToArray(),
            _totalDpc?.NextValue() ?? 0,
            _totalIsr?.NextValue() ?? 0,
            _dpcsQueued?.NextValue() ?? 0,
            _interrupts?.NextValue() ?? 0);

        public void Dispose()
        {
            foreach (var c in _dpc.Concat(_isr)) c.Dispose();
            _dpc.Clear();
            _isr.Clear();
            _totalDpc?.Dispose();
            _totalIsr?.Dispose();
            _dpcsQueued?.Dispose();
            _interrupts?.Dispose();
            _totalDpc = _totalIsr = _dpcsQueued = _interrupts = null;
        }
    }
}
