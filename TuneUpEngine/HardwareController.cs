using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace TuneUpEngine
{
    public class HardwareController
    {
        public static string CheckMsiCompliance()
        {
            var compliantDevices = new List<string>();
            try
            {
                using (var pciKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI"))
                {
                    if (pciKey == null) return "Error: Could not read PCI registry.";

                    foreach (var devId in pciKey.GetSubKeyNames())
                    {
                        using (var devKey = pciKey.OpenSubKey(devId))
                        {
                            if (devKey == null) continue;
                            foreach (var instId in devKey.GetSubKeyNames())
                            {
                                using (var instKey = devKey.OpenSubKey(instId))
                                {
                                    if (instKey == null) continue;
                                    
                                    string className = instKey.GetValue("Class") as string;
                                    string devDesc = instKey.GetValue("DeviceDesc") as string;
                                    
                                    if (className == "Display" || className == "Net")
                                    {
                                        // Found a relevant device. Check if it has Interrupt Management
                                        using (var paramKey = instKey.OpenSubKey("Device Parameters"))
                                        {
                                            if (paramKey != null)
                                            {
                                                using (var imKey = paramKey.OpenSubKey("Interrupt Management"))
                                                {
                                                    if (imKey != null)
                                                    {
                                                        // It has Interrupt Management. Likely supports MSI.
                                                        // Filter out very old known bad drivers here if needed.
                                                        compliantDevices.Add(devDesc != null ? devDesc.Split(';')[^1] : devId);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                
                if (compliantDevices.Count > 0)
                {
                    return "Compliant: " + string.Join(", ", compliantDevices);
                }
                return "No compliant PCIe Display or Network devices found.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        public static string EnableMSI()
        {
            int updatedCount = 0;
            try
            {
                using (var pciKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI", true))
                {
                    if (pciKey == null) return "Error: Could not read PCI registry.";

                    foreach (var devId in pciKey.GetSubKeyNames())
                    {
                        using (var devKey = pciKey.OpenSubKey(devId, true))
                        {
                            if (devKey == null) continue;
                            foreach (var instId in devKey.GetSubKeyNames())
                            {
                                using (var instKey = devKey.OpenSubKey(instId, true))
                                {
                                    if (instKey == null) continue;
                                    
                                    string className = instKey.GetValue("Class") as string;
                                    
                                    if (className == "Display" || className == "Net")
                                    {
                                        using (var paramKey = instKey.CreateSubKey("Device Parameters"))
                                        {
                                            using (var imKey = paramKey.CreateSubKey("Interrupt Management"))
                                            {
                                                using (var msiKey = imKey.CreateSubKey("MessageSignaledInterruptProperties"))
                                                {
                                                    msiKey.SetValue("MSISupported", 1, RegistryValueKind.DWord);
                                                    updatedCount++;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                return $"Success: Enabled MSI for {updatedCount} devices. Reboot required.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}
