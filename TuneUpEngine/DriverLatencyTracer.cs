using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace TuneUpEngine
{
    // One driver's measured DPC / interrupt work, from a kernel trace: every run is timed, so the
    // longest single run is a real measurement, not an estimate.
    public class DriverLatencyRecord
    {
        public string Driver { get; set; } = "";
        public string Description { get; set; } = "";
        public string Advice { get; set; } = "";
        public long DpcCount { get; set; }
        public long IsrCount { get; set; }
        public long RunCount => DpcCount + IsrCount;
        public double HighestDpcMicroseconds { get; set; }
        public double HighestIsrMicroseconds { get; set; }
        public double HighestMicroseconds { get; set; }
        public double TotalMilliseconds { get; set; }
        public string Status { get; set; } = "OK";   // OK | ELEVATED | HIGH
        public bool IsSpike { get; set; }
    }

    // Turns timed DPC / ISR runs into per-driver statistics. Kept apart from the ETW session so
    // tests can feed it known images and runs.
    internal sealed class DriverLatencyStats
    {
        // Longest single run. Audio buffers are a few milliseconds long, so one run of 2 ms can
        // empty one; half a millisecond is where LatencyMon-style tools start to warn.
        internal const double ElevatedMicroseconds = 500;
        internal const double HighMicroseconds = 2000;
        // A run can't last a second; a longer reading is a clock mix-up, not a measurement.
        private const double MaxPlausibleMicroseconds = 1_000_000;
        internal const string UnknownDriver = "Unknown driver";

        private readonly object _lock = new();
        private readonly List<(ulong Base, ulong Size, string Name)> _images = new(); // sorted by Base
        private readonly Dictionary<ulong, string> _routineCache = new();
        private readonly Dictionary<string, DriverLatencyRecord> _drivers = new(StringComparer.OrdinalIgnoreCase);

        public long RejectedReadings { get; private set; }

        public int ImageCount { get { lock (_lock) return _images.Count; } }

        public void AddImage(ulong imageBase, ulong size, string path)
        {
            string name = Path.GetFileName(path);
            if (imageBase == 0 || string.IsNullOrEmpty(name)) return;
            lock (_lock)
            {
                int i = FindIndex(imageBase);
                if (i >= 0 && _images[i].Base == imageBase)
                {
                    // Prefer a known size over the unknown (0) size of a seeded entry.
                    if (size != 0 || _images[i].Size == 0) _images[i] = (imageBase, size, name);
                }
                else
                {
                    _images.Insert(i + 1, (imageBase, size, name));
                }
                _routineCache.Clear();
            }
        }

        public void RemoveImage(ulong imageBase)
        {
            lock (_lock)
            {
                int i = FindIndex(imageBase);
                if (i >= 0 && _images[i].Base == imageBase)
                {
                    _images.RemoveAt(i);
                    _routineCache.Clear();
                }
            }
        }

        public void Record(bool isInterrupt, ulong routine, double microseconds)
        {
            if (double.IsNaN(microseconds) || microseconds < 0 || microseconds > MaxPlausibleMicroseconds)
            {
                lock (_lock) RejectedReadings++;
                return;
            }

            lock (_lock)
            {
                string name = Resolve(routine);
                if (!_drivers.TryGetValue(name, out var d))
                {
                    var info = DriverCatalog.Describe(name);
                    d = new DriverLatencyRecord { Driver = name, Description = info.Description, Advice = info.Advice };
                    _drivers[name] = d;
                }

                if (isInterrupt)
                {
                    d.IsrCount++;
                    d.HighestIsrMicroseconds = Math.Max(d.HighestIsrMicroseconds, microseconds);
                }
                else
                {
                    d.DpcCount++;
                    d.HighestDpcMicroseconds = Math.Max(d.HighestDpcMicroseconds, microseconds);
                }
                d.TotalMilliseconds += microseconds / 1000;
                if (microseconds > d.HighestMicroseconds)
                {
                    d.HighestMicroseconds = microseconds;
                    d.Status = microseconds >= HighMicroseconds ? "HIGH" : microseconds >= ElevatedMicroseconds ? "ELEVATED" : "OK";
                    d.IsSpike = microseconds >= HighMicroseconds;
                }
            }
        }

        public List<DriverLatencyRecord> Snapshot(int max)
        {
            lock (_lock)
            {
                return _drivers.Values
                    .OrderByDescending(d => d.HighestMicroseconds)
                    .Take(max)
                    .Select(d => new DriverLatencyRecord
                    {
                        Driver = d.Driver, Description = d.Description, Advice = d.Advice,
                        DpcCount = d.DpcCount, IsrCount = d.IsrCount,
                        HighestDpcMicroseconds = d.HighestDpcMicroseconds,
                        HighestIsrMicroseconds = d.HighestIsrMicroseconds,
                        HighestMicroseconds = d.HighestMicroseconds,
                        TotalMilliseconds = d.TotalMilliseconds,
                        Status = d.Status, IsSpike = d.IsSpike,
                    })
                    .ToList();
            }
        }

        // Caller holds _lock.
        private string Resolve(ulong routine)
        {
            if (_routineCache.TryGetValue(routine, out var cached)) return cached;

            string name = UnknownDriver;
            int i = FindIndex(routine);
            if (i >= 0)
            {
                var img = _images[i];
                // Size 0 = seeded without a size; a DPC routine is always code inside a loaded
                // image, so the nearest image below it is the owner.
                if (img.Size == 0 || routine - img.Base < img.Size) name = img.Name;
            }
            _routineCache[routine] = name;
            return name;
        }

        // Index of the last image whose base is <= address, or -1.
        private int FindIndex(ulong address)
        {
            int lo = 0, hi = _images.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_images[mid].Base <= address) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }
    }

    internal interface IDriverTraceSource : IDisposable
    {
        bool TryStart(DriverLatencyStats stats, out string? error);
        long EventsLost { get; }
    }

    // Real-time kernel ETW session with DPC, interrupt and image-load events: the same data
    // LatencyMon and Windows Performance Analyzer use to name drivers.
    internal sealed class EtwDriverTraceSource : IDriverTraceSource
    {
        // A private name (Windows 8+ allows several kernel sessions), so it doesn't take the one
        // "NT Kernel Logger" slot that LatencyMon or WPR may be using.
        private const string SessionName = "GalacticTuneUpDriverLatency";
        private const ulong KernelSpaceStart = 0xFFFF800000000000UL;

        private TraceEventSession? _session;
        private Thread? _thread;

        public long EventsLost => _session?.EventsLost ?? 0;

        public bool TryStart(DriverLatencyStats stats, out string? error)
        {
            error = null;
            try
            {
                SeedLoadedDrivers(stats);

                // Creating a session with an existing name replaces it, so one left behind by a
                // crashed engine doesn't block this one.
                _session = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = 32 };
                _session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                    KernelTraceEventParser.Keywords.Interrupt |
                    KernelTraceEventParser.Keywords.ImageLoad);

                var kernel = _session.Source.Kernel;
                kernel.ImageDCStart += e => { if (e.ImageBase >= KernelSpaceStart) stats.AddImage(e.ImageBase, (ulong)e.ImageSize, e.FileName); };
                kernel.ImageLoad += e => { if (e.ImageBase >= KernelSpaceStart) stats.AddImage(e.ImageBase, (ulong)e.ImageSize, e.FileName); };
                kernel.ImageUnload += e => { if (e.ImageBase >= KernelSpaceStart) stats.RemoveImage(e.ImageBase); };
                kernel.PerfInfoDPC += e => stats.Record(false, e.Routine, e.ElapsedTimeMSec * 1000);
                kernel.PerfInfoThreadedDPC += e => stats.Record(false, e.Routine, e.ElapsedTimeMSec * 1000);
                kernel.PerfInfoTimerDPC += e => stats.Record(false, e.Routine, e.ElapsedTimeMSec * 1000);
                kernel.PerfInfoISR += e => stats.Record(true, e.Routine, e.ElapsedTimeMSec * 1000);

                var source = _session.Source;
                _thread = new Thread(() =>
                {
                    try { source.Process(); }
                    catch (Exception ex) { EngineLog.Warn($"[DriverTrace] Trace stopped: {ex.Message}"); }
                }) { IsBackground = true, Name = "DriverLatencyTrace" };
                _thread.Start();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Dispose();
                return false;
            }
        }

        // The kernel session reports drivers that load from now on; drivers already loaded come
        // from the driver list (needs the debug privilege the service account holds).
        private static void SeedLoadedDrivers(DriverLatencyStats stats)
        {
            var bases = new IntPtr[2048];
            if (!NativeMethods.EnumDeviceDrivers(bases, (uint)(bases.Length * IntPtr.Size), out uint needed)) return;
            int count = Math.Min(bases.Length, (int)(needed / (uint)IntPtr.Size));
            var name = new char[260];
            for (int i = 0; i < count; i++)
            {
                if (bases[i] == IntPtr.Zero) continue;
                uint len = NativeMethods.GetDeviceDriverBaseNameW(bases[i], name, (uint)name.Length);
                if (len > 0) stats.AddImage((ulong)bases[i].ToInt64(), 0, new string(name, 0, (int)len));
            }
        }

        public void Dispose()
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            _thread?.Join(TimeSpan.FromSeconds(2));
            _thread = null;
        }
    }

    // Plain-English names and advice for the drivers that most often show up in DPC / ISR traces.
    internal static class DriverCatalog
    {
        private const string UpdateFromMaker = "Update it from your PC, motherboard or device maker. If the problem started after an update, roll it back in Device Manager.";

        private static readonly (string Prefix, string Description, string Advice)[] Known =
        {
            ("nvlddmkm.sys", "NVIDIA graphics driver", "Do a clean reinstall of the latest NVIDIA driver (or the previous one if this started after an update). Turn off GPU overlays and recording you don't use."),
            ("amdkmdag.sys", "AMD graphics driver", "Do a clean reinstall of the latest AMD driver (or the previous one if this started after an update)."),
            ("atikmdag.sys", "AMD graphics driver", "Do a clean reinstall of the latest AMD driver (or the previous one if this started after an update)."),
            ("igdkmd", "Intel graphics driver", "Update the Intel graphics driver from your PC maker or Intel."),
            ("dxgkrnl.sys", "Windows graphics kernel", "Usually the graphics card driver underneath. Reinstall the graphics driver cleanly."),
            ("ndis.sys", "Windows network stack", "Comes from a network adapter driver. Update your Wi-Fi or Ethernet driver, or test with a cable instead of Wi-Fi."),
            ("tcpip.sys", "Windows TCP/IP network stack", "Comes from network traffic. Update your network adapter driver; pause big downloads or sync apps while gaming."),
            ("netwtw", "Intel Wi-Fi driver", "Update the Intel Wi-Fi driver. Wi-Fi drivers often run long while scanning; a cable avoids it."),
            ("netwbw", "Intel Wi-Fi driver", "Update the Intel Wi-Fi driver. Wi-Fi drivers often run long while scanning; a cable avoids it."),
            ("rtwlan", "Realtek Wi-Fi driver", "Update the Realtek Wi-Fi driver from your PC or motherboard maker, or use a cable."),
            ("rt640x64.sys", "Realtek Ethernet driver", UpdateFromMaker),
            ("rtcx21", "Realtek Ethernet driver", UpdateFromMaker),
            ("e1d", "Intel Ethernet driver", UpdateFromMaker),
            ("e2f", "Intel Ethernet driver", UpdateFromMaker),
            ("bthport.sys", "Bluetooth", "Update the Bluetooth driver, or turn Bluetooth off if you don't use it."),
            ("storport.sys", "Windows storage driver", "Comes from a disk. Update chipset drivers and your SSD's firmware."),
            ("stornvme.sys", "NVMe SSD driver", "Update your SSD's firmware and chipset drivers."),
            ("storahci.sys", "SATA disk driver", "Update chipset drivers and disk firmware."),
            ("iastor", "Intel storage driver", "Update the Intel storage (RST) driver from your PC maker."),
            ("acpi.sys", "Power management (ACPI)", "Usually a BIOS issue, often battery or power reporting on laptops. Update the BIOS."),
            ("usbxhci.sys", "USB controller", "Try devices in different USB ports, unplug ones you don't need, and update chipset drivers."),
            ("usbport.sys", "USB controller", "Try devices in different USB ports, unplug ones you don't need, and update chipset drivers."),
            ("ucx01000.sys", "USB controller", "Try devices in different USB ports, unplug ones you don't need, and update chipset drivers."),
            ("hidclass.sys", "Keyboard, mouse or controller", "Update the device's software, or try a different USB port."),
            ("hdaudbus.sys", "Audio controller", "Update the audio driver from your PC or motherboard maker."),
            ("portcls.sys", "Windows audio driver", "Comes from an audio device. Update its driver; disable audio devices you don't use."),
            ("rtkvhd64.sys", "Realtek audio driver", "Update the Realtek audio driver from your PC or motherboard maker."),
            ("wdf01000.sys", "Windows driver framework", "Runs code for another driver, so the real cause is hidden. Check the other drivers in this list first."),
            ("ntoskrnl.exe", "Windows kernel", "Mostly timers and Windows' own work. Normal unless very high; if so, look at the other drivers here."),
            ("hal.dll", "Windows hardware layer", "Usually firmware. Update the BIOS and chipset drivers."),
        };

        public static (string Description, string Advice) Describe(string driver)
        {
            if (driver == DriverLatencyStats.UnknownDriver)
                return ("Code outside any known driver", "Can't be traced to a driver.");
            foreach (var k in Known)
            {
                if (driver.StartsWith(k.Prefix, StringComparison.OrdinalIgnoreCase))
                    return (k.Description, k.Advice);
            }
            return ("Third-party driver", UpdateFromMaker);
        }
    }
}
