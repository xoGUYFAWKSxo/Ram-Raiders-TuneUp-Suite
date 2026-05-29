using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace TuneUpEngine
{
    public class RegistryTweaker
    {
        // Helper methods to keep code clean
        private static bool CheckDword(RegistryKey root, string keyPath, string valueName, int expectedValue)
        {
            using (var key = root.OpenSubKey(keyPath))
            {
                if (key != null && key.GetValue(valueName) is int val)
                {
                    return val == expectedValue;
                }
                return false;
            }
        }

        private static bool CheckString(RegistryKey root, string keyPath, string valueName, string expectedValue)
        {
            using (var key = root.OpenSubKey(keyPath))
            {
                if (key != null && key.GetValue(valueName) is string val)
                {
                    return val == expectedValue;
                }
                return false;
            }
        }

        private static void SetDword(RegistryKey root, string keyPath, string valueName, int value, bool enable, int? defaultValue = null)
        {
            using (var key = root.CreateSubKey(keyPath))
            {
                if (enable)
                {
                    key.SetValue(valueName, value, RegistryValueKind.DWord);
                }
                else
                {
                    if (defaultValue.HasValue) key.SetValue(valueName, defaultValue.Value, RegistryValueKind.DWord);
                    else key.DeleteValue(valueName, false);
                }
            }
        }

        private static void SetString(RegistryKey root, string keyPath, string valueName, string value, bool enable, string defaultValue = null)
        {
            using (var key = root.CreateSubKey(keyPath))
            {
                if (enable)
                {
                    key.SetValue(valueName, value, RegistryValueKind.String);
                }
                else
                {
                    if (defaultValue != null) key.SetValue(valueName, defaultValue, RegistryValueKind.String);
                    else key.DeleteValue(valueName, false);
                }
            }
        }

        public static Dictionary<string, bool> GetTweakStatuses()
        {
            var statuses = new Dictionary<string, bool>();

            try
            {
                // Original Tweaks
                statuses["NetworkThrottling"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF));
                statuses["SystemResponsiveness"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 0);
                
                using (var key1 = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore"))
                using (var key2 = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR"))
                {
                    bool dvrEnabled = (key1 != null && key1.GetValue("GameDVR_Enabled") is int dvr && dvr == 0);
                    bool appCapture = (key2 != null && key2.GetValue("AppCaptureEnabled") is int app && app == 0);
                    statuses["GameDVR"] = (dvrEnabled && appCapture);
                }

                statuses["PowerThrottling"] = CheckDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1);

                // System Tweaks
                statuses["DisableDriverUpdates"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1);
                statuses["DisableStoreUpdates"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", 2);
                statuses["DisableTelemetry"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0);
                statuses["ClearPageFile"] = CheckDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "ClearPageFileAtShutdown", 1);

                // Windows Usage Tweaks
                statuses["DisableShortcutSuffix"] = CheckDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "link", 0);
                statuses["DisablePreventShutdown"] = CheckString(Registry.CurrentUser, @"Control Panel\Desktop", "AutoEndTasks", "1");

                // Security & Stability
                statuses["CloseUnresponsive"] = CheckString(Registry.CurrentUser, @"Control Panel\Desktop", "WaitToKillAppTimeout", "2000");
                statuses["DisableSecurityWarning"] = CheckDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Attachments", "SaveZoneInformation", 1);
                statuses["TakeOwnership"] = Registry.ClassesRoot.OpenSubKey(@"*\shell\TakeOwnership") != null;
                statuses["DisableSmartScreen"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 0);
                statuses["ShowBSODDetails"] = CheckDword(Registry.LocalMachine, @"System\CurrentControlSet\Control\CrashControl", "DisplayParameters", 1);
                statuses["EnableNetworkDrivesElevated"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLinkedConnections", 1);
                statuses["DisableUAC"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 0);
                statuses["EnableAntiAdware"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender\MpEngine", "MpEnablePus", 1);
                statuses["ForceCrashDump"] = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps") != null;

                // Desktop Tweaks
                statuses["SpeedUpMouseHover"] = CheckString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseHoverTime", "10");
                statuses["SpeedUpDesktopLoading"] = CheckString(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "Max Cached Icons", "4096");
                statuses["DisableBingSearch"] = CheckDword(Registry.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1);
                statuses["TaskbarEndTask"] = CheckDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 1);
                statuses["TaskbarSeconds"] = CheckDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSecondsInSystemClock", 1);

                // File Explorer Tweaks
                statuses["DisableNewAppPrompt"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "NoNewAppAlert", 1);
                statuses["CmdAdminContextMenu"] = Registry.ClassesRoot.OpenSubKey(@"Directory\shell\runas") != null;
                statuses["ExplorerAutoComplete"] = CheckString(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoComplete", "AutoSuggest", "yes");
                statuses["ShowDriveLettersFirst"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "ShowDriveLettersFirst", 4);

                // Windows Appearance Tweaks
                statuses["SpeedUpAeroSnap"] = CheckString(Registry.CurrentUser, @"Control Panel\Desktop", "WindowArrangementActive", "1");
                statuses["SpeedUpWindowsPeek"] = CheckDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DesktopLivePreviewHoverTime", 100);
                statuses["SpeedUpMenus"] = CheckString(Registry.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "10");
                statuses["Win10ContextMenu"] = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32") != null;
                statuses["DisableWin11Ads"] = CheckDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0);
                statuses["DisableSigninBlur"] = CheckDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "DisableAcrylicBackgroundOnLogon", 1);
                statuses["EnableBatteryEstimatedTime"] = CheckDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "EnergyEstimationEnabled", 1);
                statuses["RemoveShortcutArrow"] = CheckString(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons", "29", @"%windir%\System32\shell32.dll,-50");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RegistryTweaker] Failed to read statuses: {ex.Message}");
            }

            return statuses;
        }

        public static bool SetTweakStatus(string tweakName, bool enable)
        {
            try
            {
                switch (tweakName)
                {
                    case "NetworkThrottling":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), enable, 10);
                        break;
                    case "SystemResponsiveness":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 0, enable, 20);
                        break;
                    case "GameDVR":
                        using (var key1 = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore"))
                            key1.SetValue("GameDVR_Enabled", enable ? 0 : 1, RegistryValueKind.DWord);
                        using (var key2 = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR"))
                            key2.SetValue("AppCaptureEnabled", enable ? 0 : 1, RegistryValueKind.DWord);
                        break;
                    case "PowerThrottling":
                        SetDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, enable);
                        break;

                    // System Tweaks
                    case "DisableDriverUpdates":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, enable, 0);
                        break;
                    case "DisableStoreUpdates":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", 2, enable, 4);
                        break;
                    case "DisableTelemetry":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, enable, 3);
                        break;
                    case "ClearPageFile":
                        SetDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "ClearPageFileAtShutdown", 1, enable, 0);
                        break;

                    // Windows Usage Tweaks
                    case "DisableShortcutSuffix":
                        using (var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer"))
                        {
                            if (enable) key.SetValue("link", new byte[] { 0, 0, 0, 0 }, RegistryValueKind.Binary);
                            else key.DeleteValue("link", false);
                        }
                        break;
                    case "DisablePreventShutdown":
                        SetString(Registry.CurrentUser, @"Control Panel\Desktop", "AutoEndTasks", "1", enable, "0");
                        break;

                    // Security & Stability
                    case "CloseUnresponsive":
                        SetString(Registry.CurrentUser, @"Control Panel\Desktop", "WaitToKillAppTimeout", "2000", enable, "20000");
                        SetString(Registry.CurrentUser, @"Control Panel\Desktop", "HungAppTimeout", "2000", enable, "5000");
                        break;
                    case "DisableSecurityWarning":
                        SetDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Attachments", "SaveZoneInformation", 1, enable, 2);
                        break;
                    case "TakeOwnership":
                        if (enable)
                        {
                            using (var key = Registry.ClassesRoot.CreateSubKey(@"*\shell\TakeOwnership"))
                            {
                                key.SetValue("", "Take Ownership");
                                key.SetValue("HasLUAShield", "");
                                key.SetValue("NoWorkingDirectory", "");
                                using (var cmd = key.CreateSubKey("command"))
                                {
                                    cmd.SetValue("", "cmd.exe /c takeown /f \"%1\" && icacls \"%1\" /grant administrators:F");
                                    cmd.SetValue("IsolatedCommand", "cmd.exe /c takeown /f \"%1\" && icacls \"%1\" /grant administrators:F");
                                }
                            }
                            using (var key = Registry.ClassesRoot.CreateSubKey(@"Directory\shell\TakeOwnership"))
                            {
                                key.SetValue("", "Take Ownership");
                                key.SetValue("HasLUAShield", "");
                                key.SetValue("NoWorkingDirectory", "");
                                using (var cmd = key.CreateSubKey("command"))
                                {
                                    cmd.SetValue("", "cmd.exe /c takeown /f \"%1\" /r /d y && icacls \"%1\" /grant administrators:F /t");
                                    cmd.SetValue("IsolatedCommand", "cmd.exe /c takeown /f \"%1\" /r /d y && icacls \"%1\" /grant administrators:F /t");
                                }
                            }
                        }
                        else
                        {
                            Registry.ClassesRoot.DeleteSubKeyTree(@"*\shell\TakeOwnership", false);
                            Registry.ClassesRoot.DeleteSubKeyTree(@"Directory\shell\TakeOwnership", false);
                        }
                        break;
                    case "DisableSmartScreen":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 0, enable, 1);
                        break;
                    case "ShowBSODDetails":
                        SetDword(Registry.LocalMachine, @"System\CurrentControlSet\Control\CrashControl", "DisplayParameters", 1, enable, 0);
                        break;
                    case "EnableNetworkDrivesElevated":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLinkedConnections", 1, enable, 0);
                        break;
                    case "DisableUAC":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 0, enable, 1);
                        break;
                    case "EnableAntiAdware":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender\MpEngine", "MpEnablePus", 1, enable, 0);
                        break;
                    case "ForceCrashDump":
                        if (enable) Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps");
                        else Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps", false);
                        break;

                    // Desktop Tweaks
                    case "SpeedUpMouseHover":
                        SetString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseHoverTime", "10", enable, "400");
                        break;
                    case "SpeedUpDesktopLoading":
                        SetString(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "Max Cached Icons", "4096", enable);
                        break;
                    case "DisableBingSearch":
                        SetDword(Registry.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, enable, 0);
                        break;
                    case "TaskbarEndTask":
                        SetDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 1, enable, 0);
                        break;
                    case "TaskbarSeconds":
                        SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSecondsInSystemClock", 1, enable, 0);
                        break;

                    // File Explorer Tweaks
                    case "DisableNewAppPrompt":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "NoNewAppAlert", 1, enable, 0);
                        break;
                    case "CmdAdminContextMenu":
                        if (enable)
                        {
                            using (var key = Registry.ClassesRoot.CreateSubKey(@"Directory\shell\runas"))
                            {
                                key.SetValue("", "Open Command Window Here as Administrator");
                                key.SetValue("HasLUAShield", "");
                                using (var cmd = key.CreateSubKey("command"))
                                    cmd.SetValue("", "cmd.exe /s /k pushd \"%V\"");
                            }
                        }
                        else Registry.ClassesRoot.DeleteSubKeyTree(@"Directory\shell\runas", false);
                        break;
                    case "ExplorerAutoComplete":
                        SetString(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoComplete", "AutoSuggest", "yes", enable, "no");
                        SetString(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoComplete", "AppendCompletion", "yes", enable, "no");
                        break;
                    case "ShowDriveLettersFirst":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "ShowDriveLettersFirst", 4, enable, 0);
                        break;

                    // Windows Appearance Tweaks
                    case "SpeedUpAeroSnap":
                        SetString(Registry.CurrentUser, @"Control Panel\Desktop", "WindowArrangementActive", "1", enable, "0");
                        break;
                    case "SpeedUpWindowsPeek":
                        SetDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DesktopLivePreviewHoverTime", 100, enable, 1000);
                        break;
                    case "SpeedUpMenus":
                        SetString(Registry.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "10", enable, "400");
                        break;
                    case "Win10ContextMenu":
                        if (enable)
                        {
                            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"))
                                key.SetValue("", "");
                        }
                        else Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", false);
                        break;
                    case "DisableWin11Ads":
                        SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0, enable, 1);
                        SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0, enable, 1);
                        SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SoftLandingEnabled", 0, enable, 1);
                        break;
                    case "DisableSigninBlur":
                        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "DisableAcrylicBackgroundOnLogon", 1, enable, 0);
                        break;
                    case "EnableBatteryEstimatedTime":
                        SetDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "EnergyEstimationEnabled", 1, enable, 0);
                        break;
                    case "RemoveShortcutArrow":
                        SetString(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons", "29", @"%windir%\System32\shell32.dll,-50", enable);
                        break;

                    default:
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RegistryTweaker] Failed to set tweak {tweakName}: {ex.Message}");
                return false;
            }
        }
    }
}
