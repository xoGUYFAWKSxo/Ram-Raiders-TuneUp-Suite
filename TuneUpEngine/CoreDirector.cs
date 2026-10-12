using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TuneUpEngine
{
    public class CoreTopology
    {
        public bool HasHybridCores { get; set; }
        public int PCoreCount { get; set; }
        public int ECoreCount { get; set; }
        public IntPtr PCoresMask { get; set; }
        public IntPtr ECoresMask { get; set; }
        public IntPtr AllCoresMask { get; set; }
        public string CpuModel { get; set; } = string.Empty;
        public CpuArchitecture Architecture { get; set; }
        public string DetectionMethod { get; set; } = string.Empty;
    }

    public enum CpuArchitecture
    {
        Unknown,
        IntelHybrid,      // 12th Gen Alder Lake and later
        IntelStandard,
        AmdX3D,           // 3D V-Cache (topology aware but not P/E)
        AmdStandard,
        Other
    }

    public static class CoreDirector
    {
        #region Win32 P/Invoke

        private const int RelationProcessorCore = 0;
        private const int RelationAll = 0xFFFF;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSOR_RELATIONSHIP
        {
            public byte Flags;           // LTP_PC_SMT (0x1) = SMT enabled
            public byte EfficiencyClass; // 0 = P-core, > 0 = E-core (on hybrid)
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public byte[] Reserved;
            public ushort GroupCount;
            // Followed by GROUP_AFFINITY array — handled via offset
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GROUP_AFFINITY
        {
            public UIntPtr Mask;
            public ushort Group;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public ushort[] Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
        {
            public int Relationship;
            public uint Size;
            // Union follows — we only use PROCESSOR_RELATIONSHIP
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetActiveProcessorCount(ushort groupNumber);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(
            int relationship, IntPtr buffer, ref uint returnedLength);

        #endregion

        private static CoreTopology? _cachedTopology;
        private static readonly object _lock = new object();

        public static CoreTopology GetTopology()
        {
            lock (_lock)
            {
                if (_cachedTopology != null) return _cachedTopology;
                _cachedTopology = BuildTopology();
                return _cachedTopology;
            }
        }

        // A mask with the lowest `count` bits set. (1L << 64) wraps around to (1L << 0) in C#, so the
        // old "(1L << ProcessorCount) - 1" produced 0 on a 64-thread CPU — an empty affinity mask.
        internal static long MaskFor(int count) =>
            count >= 64 ? -1L : count <= 0 ? 0L : (1L << count) - 1;

        // Process affinity masks only cover the process's processor group (64 logical processors at
        // most), so masks are built for group 0 — not Environment.ProcessorCount, which counts every
        // group on CPUs with more than 64 threads.
        internal static int Group0ProcessorCount()
        {
            try
            {
                uint count = GetActiveProcessorCount(0);
                if (count > 0) return (int)Math.Min(count, 64u);
            }
            catch { }
            return Math.Min(Environment.ProcessorCount, 64);
        }

        // "All cores except core 0" (core 0 handles most interrupts), but never an empty mask.
        internal static long AllButFirst(long mask) => (mask & ~1L) != 0 ? mask & ~1L : mask;

        private static CoreTopology BuildTopology()
        {
            var topology = new CoreTopology();
            topology.CpuModel = GetCpuModelString();
            topology.Architecture = DetectArchitecture(topology.CpuModel);

            long allCoresMask = MaskFor(Group0ProcessorCount());
            topology.AllCoresMask = new IntPtr(allCoresMask);

            // Windows reports each core's efficiency class directly, so ask it on every CPU rather
            // than only for model names we recognise (which missed new or renamed hybrid parts).
            if (!TryParseHybridCores(topology))
            {
                topology.HasHybridCores = false;
                topology.PCoreCount = Group0ProcessorCount();
                topology.ECoreCount = 0;
                topology.PCoresMask = new IntPtr(AllButFirst(allCoresMask));
                topology.ECoresMask = IntPtr.Zero;
                topology.DetectionMethod = $"Standard ({topology.Architecture})";
            }
            else if (topology.HasHybridCores && topology.Architecture == CpuArchitecture.IntelStandard)
            {
                // The name didn't look hybrid, but Windows says it is.
                topology.Architecture = CpuArchitecture.IntelHybrid;
            }

            EngineLog.Info($"[CoreDirector] CPU: {topology.CpuModel}");
            EngineLog.Info($"[CoreDirector] Architecture: {topology.Architecture} | Hybrid: {topology.HasHybridCores}");
            if (topology.HasHybridCores)
                EngineLog.Info($"[CoreDirector] P-cores: {topology.PCoreCount} (mask: 0x{topology.PCoresMask.ToInt64():X}) | E-cores: {topology.ECoreCount} (mask: 0x{topology.ECoresMask.ToInt64():X})");

            return topology;
        }

        private static bool TryParseHybridCores(CoreTopology topology)
        {
            try
            {
                uint returnedLength = 0;
                GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref returnedLength);
                if (returnedLength == 0) return false;

                IntPtr buffer = Marshal.AllocHGlobal((int)returnedLength);
                try
                {
                    if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref returnedLength))
                        return false;

                    var cores = new List<(byte EfficiencyClass, ushort Group, long Mask)>();
                    IntPtr ptr = buffer;
                    IntPtr end = IntPtr.Add(buffer, (int)returnedLength);

                    while (ptr.ToInt64() < end.ToInt64())
                    {
                        // Header: Relationship (int) + Size (uint); PROCESSOR_RELATIONSHIP at offset 8:
                        // Flags (byte), EfficiencyClass (byte), Reserved[20], GroupCount (ushort at 30),
                        // then GROUP_AFFINITY[] at 32, 16 bytes each: Mask (8), Group (ushort at +8).
                        int relationship = Marshal.ReadInt32(ptr, 0);
                        uint size = (uint)Marshal.ReadInt32(ptr, 4);
                        if (size == 0) break;

                        if (relationship == RelationProcessorCore)
                        {
                            byte efficiencyClass = Marshal.ReadByte(ptr, 9);
                            ushort groupCount = (ushort)Marshal.ReadInt16(ptr, 30);
                            for (int g = 0; g < groupCount; g++)
                            {
                                IntPtr affinityPtr = IntPtr.Add(ptr, 32 + g * 16);
                                long mask = Marshal.ReadInt64(affinityPtr, 0);
                                ushort group = (ushort)Marshal.ReadInt16(affinityPtr, 8);
                                cores.Add((efficiencyClass, group, mask));
                            }
                        }

                        ptr = IntPtr.Add(ptr, (int)size);
                    }

                    return ApplyCoreClasses(topology, cores);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                EngineLog.Warn($"[CoreDirector] Failed to read core topology: {ex.Message}");
                return false;
            }
        }

        // Splits group-0 cores into performance (the highest efficiency class) and efficiency
        // (lower classes). On a hybrid CPU Windows gives P-cores the HIGHER class (e.g. P = 1,
        // E = 0); the old code assumed 0 meant P-core, which is backwards. A CPU where every core
        // reports the same class isn't hybrid.
        internal static bool ApplyCoreClasses(CoreTopology topology, IReadOnlyList<(byte EfficiencyClass, ushort Group, long Mask)> cores)
        {
            var group0 = cores.Where(c => c.Group == 0).ToList();
            if (group0.Count == 0) return false;

            byte maxClass = group0.Max(c => c.EfficiencyClass);
            byte minClass = group0.Min(c => c.EfficiencyClass);
            long all = group0.Aggregate(0L, (m, c) => m | c.Mask);

            if (maxClass == minClass)
            {
                topology.HasHybridCores = false;
                topology.PCoreCount = CountBits(all);
                topology.ECoreCount = 0;
                topology.PCoresMask = new IntPtr(AllButFirst(all));
                topology.ECoresMask = IntPtr.Zero;
                topology.DetectionMethod = "GetLogicalProcessorInformationEx (no E-cores)";
                return true;
            }

            long pMask = group0.Where(c => c.EfficiencyClass == maxClass).Aggregate(0L, (m, c) => m | c.Mask);
            long eMask = group0.Where(c => c.EfficiencyClass != maxClass).Aggregate(0L, (m, c) => m | c.Mask);

            topology.HasHybridCores = true;
            topology.PCoreCount = CountBits(pMask);
            topology.ECoreCount = CountBits(eMask);
            topology.PCoresMask = new IntPtr(pMask);
            topology.ECoresMask = new IntPtr(eMask);
            topology.DetectionMethod = "GetLogicalProcessorInformationEx";
            return true;
        }

        private static string GetCpuModelString()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        internal static CpuArchitecture DetectArchitecture(string cpuModel)
        {
            if (string.IsNullOrEmpty(cpuModel)) return CpuArchitecture.Unknown;
            string model = cpuModel.ToUpperInvariant();

            // Intel Hybrid: 12th Gen (Alder Lake) and later all use P+E topology
            // Also includes Core Ultra (Meteor Lake, Arrow Lake, Lunar Lake)
            if (model.Contains("INTEL"))
            {
                // Core Ultra = always hybrid (Meteor Lake and newer). Match on "ULTRA" alone: the
                // model string carries trademark noise between the words — e.g.
                // "Intel(R) Core(TM) Ultra 7 155H" — so a "CORE\s+ULTRA" pattern misses the "(TM)".
                // "Ultra" is unique to Intel's hybrid Core Ultra line, so this stays specific.
                if (model.Contains("ULTRA"))
                    return CpuArchitecture.IntelHybrid;

                // 12th Gen: i3/i5/i7/i9 12xxx
                if (Regex.IsMatch(model, @"I[3579]-1[2-9]\d{3}[A-Z]*"))
                    return CpuArchitecture.IntelHybrid;

                // 13th Gen: 13xxx
                if (Regex.IsMatch(model, @"I[3579]-13\d{3}[A-Z]*"))
                    return CpuArchitecture.IntelHybrid;

                // 14th Gen: 14xxx
                if (Regex.IsMatch(model, @"I[3579]-14\d{3}[A-Z]*"))
                    return CpuArchitecture.IntelHybrid;

                return CpuArchitecture.IntelStandard;
            }

            // AMD 3D V-Cache: Ryzen X3D models (topology-aware, no classic P/E split)
            if (model.Contains("AMD"))
            {
                if (model.Contains("X3D"))
                    return CpuArchitecture.AmdX3D;

                return CpuArchitecture.AmdStandard;
            }

            return CpuArchitecture.Other;
        }

        // (The old loop shifted a signed value right, which never reaches 0 for a mask with the top
        // bit set — an infinite loop on a 64-thread CPU.)
        private static int CountBits(long value) => System.Numerics.BitOperations.PopCount(unchecked((ulong)value));
    }
}
