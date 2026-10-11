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
        public double PeakDpcPercent { get; set; }
        public double PeakInterruptPercent { get; set; }
        public double AvgDpcPercent { get; set; }
        public double AvgInterruptPercent { get; set; }
        public int HighSamples { get; set; }
        public string Status { get; set; } = "OK";   // OK | ELEVATED | HIGH
        public bool IsSpike { get; set; }

        internal double DpcSum;
        internal double InterruptSum;
        internal int SampleCount;
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
    // drivers, with real peaks and averages.
    public class LatencyAuditor
    {
        // Share of one core's time spent in DPC + interrupt work.
        internal const double ElevatedPercent = 3.0;
        internal const double HighPercent = 10.0;
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

        private readonly Func<ILatencyCounterSource> _sourceFactory;
        private ILatencyCounterSource? _source;
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
        {
            _sourceFactory = sourceFactory ?? (() => new PerformanceCounterLatencySource());
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
            }
            OnLogMessage?.Invoke("[LatencyAuditor] Driver DPC / interrupt monitoring stopped.");
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
                        .OrderByDescending(c => c.PeakDpcPercent + c.PeakInterruptPercent)
                        .Take(16)
                        .Select(Copy)
                        .ToList(),
                };
                report.Summary = BuildSummary(report, _cores);
                return report;
            }
        }

        private void AuditLoop()
        {
            while (_isRunning)
            {
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

                Thread.Sleep(SampleInterval);
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
                    double dpc = Math.Max(0, sample.DpcPercent[i]);
                    double isr = Math.Max(0, sample.InterruptPercent[i]);

                    core.CurrentDpcPercent = dpc;
                    core.CurrentInterruptPercent = isr;
                    core.PeakDpcPercent = Math.Max(core.PeakDpcPercent, dpc);
                    core.PeakInterruptPercent = Math.Max(core.PeakInterruptPercent, isr);
                    core.DpcSum += dpc;
                    core.InterruptSum += isr;
                    core.SampleCount++;
                    core.AvgDpcPercent = core.DpcSum / core.SampleCount;
                    core.AvgInterruptPercent = core.InterruptSum / core.SampleCount;

                    double peak = core.PeakDpcPercent + core.PeakInterruptPercent;
                    core.Status = peak >= HighPercent ? "HIGH" : peak >= ElevatedPercent ? "ELEVATED" : "OK";
                    core.IsSpike = peak >= HighPercent;

                    if (dpc + isr >= HighPercent)
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
            if (report.SampleCount == 0)
                return "Measuring… results appear after the first second.";

            var high = allCores.Where(c => c.Status == "HIGH").Select(c => c.Name).ToList();
            var elevated = allCores.Where(c => c.Status == "ELEVATED").Select(c => c.Name).ToList();
            const string attribution = " Performance counters can't name the driver — use LatencyMon or Windows Performance Analyzer to find which one.";

            if (high.Count > 0)
                return $"{string.Join(", ", high)} spent {HighPercent:F0}% or more of their time servicing driver DPCs/interrupts at peak — enough to cause stutter or audio dropouts.{attribution}";
            if (elevated.Count > 0)
                return $"{string.Join(", ", elevated)} peaked above {ElevatedPercent:F0}% driver DPC/interrupt time. Usually harmless; worth watching if you hear crackles or see stutter.";
            return $"Driver DPC/interrupt load stayed below {ElevatedPercent:F0}% on every core — healthy.";
        }

        private static CoreLatencyRecord Copy(CoreLatencyRecord c) => new()
        {
            Name = c.Name,
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
