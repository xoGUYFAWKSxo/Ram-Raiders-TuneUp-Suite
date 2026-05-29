using System;
using System.Collections.Generic;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(
            int relationship, IntPtr buffer, ref uint returnedLength);

        #endregion

        private static CoreTopology _cachedTopology;
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

        private static CoreTopology BuildTopology()
        {
            var topology = new CoreTopology();
            topology.CpuModel = GetCpuModelString();
            topology.Architecture = DetectArchitecture(topology.CpuModel);

            long allCoresMask = (1L << Environment.ProcessorCount) - 1;
            topology.AllCoresMask = new IntPtr(allCoresMask);

            // Only attempt P/E core enumeration on known hybrid architectures
            if (topology.Architecture == CpuArchitecture.IntelHybrid)
            {
                bool parsed = TryParseHybridCores(topology);
                if (!parsed)
                {
                    // API call failed — fall back to standard behavior
                    topology.HasHybridCores = false;
                    topology.PCoresMask = new IntPtr(allCoresMask & ~1L);
                    topology.ECoresMask = IntPtr.Zero;
                    topology.DetectionMethod = "Fallback (API unavailable)";
                }
            }
            else
            {
                // Standard topology: no E-cores, all cores except 0 for games
                topology.HasHybridCores = false;
                topology.PCoreCount = Environment.ProcessorCount;
                topology.ECoreCount = 0;
                topology.PCoresMask = new IntPtr(allCoresMask & ~1L);
                topology.ECoresMask = IntPtr.Zero;
                topology.DetectionMethod = $"Standard ({topology.Architecture})";
            }

            Console.WriteLine($"[CoreDirector] CPU: {topology.CpuModel}");
            Console.WriteLine($"[CoreDirector] Architecture: {topology.Architecture} | Hybrid: {topology.HasHybridCores}");
            if (topology.HasHybridCores)
                Console.WriteLine($"[CoreDirector] P-cores: {topology.PCoreCount} (mask: 0x{topology.PCoresMask.ToInt64():X}) | E-cores: {topology.ECoreCount} (mask: 0x{topology.ECoresMask.ToInt64():X})");

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

                    long pMask = 0;
                    long eMask = 0;
                    int pCount = 0;
                    int eCount = 0;

                    IntPtr ptr = buffer;
                    IntPtr end = IntPtr.Add(buffer, (int)returnedLength);

                    while (ptr.ToInt64() < end.ToInt64())
                    {
                        // Read the header: Relationship (int) + Size (uint)
                        int relationship = Marshal.ReadInt32(ptr, 0);
                        uint size = (uint)Marshal.ReadInt32(ptr, 4);

                        if (relationship == RelationProcessorCore)
                        {
                            // PROCESSOR_RELATIONSHIP starts at offset 8
                            // EfficiencyClass is byte at offset 9
                            byte efficiencyClass = Marshal.ReadByte(ptr, 9);

                            // GroupCount is at offset 28 (after Flags[1] + EfficiencyClass[1] + Reserved[20] + pad)
                            // GROUP_AFFINITY starts at offset 32
                            // Each GROUP_AFFINITY is 16 bytes (UIntPtr[8] + ushort + ushort[3])
                            ushort groupCount = (ushort)Marshal.ReadInt16(ptr, 30);

                            for (int g = 0; g < groupCount; g++)
                            {
                                IntPtr affinityPtr = IntPtr.Add(ptr, 32 + g * 16);
                                long mask = Marshal.ReadInt64(affinityPtr, 0);

                                if (efficiencyClass == 0)
                                {
                                    pMask |= mask;
                                    pCount += CountBits(mask);
                                }
                                else
                                {
                                    eMask |= mask;
                                    eCount += CountBits(mask);
                                }
                            }
                        }

                        ptr = IntPtr.Add(ptr, (int)size);
                    }

                    if (eCount > 0)
                    {
                        topology.HasHybridCores = true;
                        topology.PCoreCount = pCount;
                        topology.ECoreCount = eCount;
                        topology.PCoresMask = new IntPtr(pMask);
                        topology.ECoresMask = new IntPtr(eMask);
                        topology.DetectionMethod = "GetLogicalProcessorInformationEx";
                        return true;
                    }

                    // API succeeded but reported no E-cores (e.g., i9-12900KS with all P-cores)
                    topology.HasHybridCores = false;
                    topology.PCoreCount = pCount > 0 ? pCount : Environment.ProcessorCount;
                    topology.ECoreCount = 0;
                    topology.PCoresMask = new IntPtr(pMask != 0 ? pMask & ~1L : ((1L << Environment.ProcessorCount) - 1) & ~1L);
                    topology.ECoresMask = IntPtr.Zero;
                    topology.DetectionMethod = "GetLogicalProcessorInformationEx (no E-cores found)";
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CoreDirector] Failed to parse hybrid cores: {ex.Message}");
                return false;
            }
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

        private static CpuArchitecture DetectArchitecture(string cpuModel)
        {
            if (string.IsNullOrEmpty(cpuModel)) return CpuArchitecture.Unknown;
            string model = cpuModel.ToUpperInvariant();

            // Intel Hybrid: 12th Gen (Alder Lake) and later all use P+E topology
            // Also includes Core Ultra (Meteor Lake, Arrow Lake, Lunar Lake)
            if (model.Contains("INTEL"))
            {
                // Core Ultra = always hybrid (Meteor Lake+)
                if (Regex.IsMatch(model, @"CORE\s+ULTRA"))
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

        private static int CountBits(long value)
        {
            int count = 0;
            while (value != 0) { count += (int)(value & 1); value >>= 1; }
            return count;
        }
    }
}
