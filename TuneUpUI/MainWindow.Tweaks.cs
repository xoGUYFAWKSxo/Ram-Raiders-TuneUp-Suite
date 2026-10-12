using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.ComponentModel;
using System.Windows.Data;
using TuneUpEngine;

// Disambiguate WPF types from System.Windows.Forms types
using Application  = System.Windows.Application;
using MessageBox   = System.Windows.MessageBox;
using Button       = System.Windows.Controls.Button;
using CheckBox     = System.Windows.Controls.CheckBox;
using RadioButton  = System.Windows.Controls.RadioButton;
using TextBlock    = System.Windows.Controls.TextBlock;
using StackPanel   = System.Windows.Controls.StackPanel;
using Grid         = System.Windows.Controls.Grid;
using GridLength   = System.Windows.GridLength;
using WpfColor     = System.Windows.Media.Color;


namespace TuneUpUI
{
    // Gaming / OS registry tweaks.
    public partial class MainWindow
    {
        // ==========================================
        // GAMING REGISTRY TWEAKS
        // ==========================================

        private void FetchRegistryTweaks()
        {
            Task.Run(() =>
            {
                var resp = SendIpcRequest(IpcCommands.GetRegistryTweaks);
                if (resp != null && resp.Status == IpcStatus.Success)
                {
                    Dispatcher.Invoke(() =>
                    {
                        var tweaks = JsonSerializer.Deserialize<Dictionary<string, bool>>(resp.Payload);
                        if (tweaks == null) return;

                        // Dynamically update checkboxes without hardcoding each one
                        foreach (var kvp in tweaks)
                        {
                            if (FindName("Tgl" + kvp.Key) is CheckBox chk)
                            {
                                chk.Click -= TweakToggle_Click;
                                chk.IsChecked = kvp.Value;
                                chk.Click += TweakToggle_Click;
                            }

                            // Retired security-reducing tweaks are only shown while they are still
                            // active on this PC, so the user can switch them off.
                            if (RegistryTweaker.RetiredTweaks.Contains(kvp.Key) &&
                                FindName("Row" + kvp.Key) is Grid row)
                            {
                                row.Visibility = kvp.Value ? Visibility.Visible : Visibility.Collapsed;
                            }
                        }
                    });
                }
            });
        }

        private void TweakToggle_Click(object sender, RoutedEventArgs e)
        {
            // CheckBox derives from ToggleButton — Tag is set as a string in XAML
            if (sender is CheckBox chk && chk.Tag is string tweakName)
            {
                bool enable = chk.IsChecked ?? false;
                Task.Run(() =>
                {
                    var resp = _engine.SendJson(IpcCommands.SetRegistryTweak, new SetTweakRequest(tweakName, enable));
                    if (resp?.Status != IpcStatus.Success)
                    {
                        // The toggle flipped optimistically; re-read the real state so it doesn't
                        // claim a change that never happened.
                        Dispatcher.Invoke(() => AppendMonitorEvent($"[Tweaks] Could not {(enable ? "apply" : "revert")} '{tweakName}'. Is the engine running as Administrator?"));
                    }

                    // Always re-read the real state. Besides failures, a setting shared with another
                    // feature (e.g. SystemResponsiveness, also used by Game Mode) correctly stays in
                    // place when only one of them is switched off, and retired rows hide once off.
                    FetchRegistryTweaks();
                });
            }
        }

        private void BtnApplyCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string category)
            {
                string[] tweaksToApply = null;
                switch (category)
                {
                    case "SystemTweaks":
                        tweaksToApply = new[] { "DisableDriverUpdates", "DisableStoreUpdates", "DisableTelemetry", "ClearPageFile" };
                        break;
                    case "WindowsUsage":
                        tweaksToApply = new[] { "DisableShortcutSuffix", "DisablePreventShutdown" };
                        break;
                    case "SecurityStability":
                        // The retired security-reducing tweaks (UAC, SmartScreen, download zone
                        // warning) are deliberately not part of "apply all".
                        tweaksToApply = new[] { "CloseUnresponsive", "TakeOwnership", "ShowBSODDetails", "EnableNetworkDrivesElevated", "EnableAntiAdware", "ForceCrashDump" };
                        break;
                    case "DesktopTweaks":
                        tweaksToApply = new[] { "SpeedUpMouseHover", "SpeedUpDesktopLoading", "DisableBingSearch", "TaskbarEndTask", "TaskbarSeconds" };
                        break;
                    case "FileExplorer":
                        tweaksToApply = new[] { "DisableNewAppPrompt", "CmdAdminContextMenu", "ExplorerAutoComplete", "ShowDriveLettersFirst" };
                        break;
                    case "WindowsAppearance":
                        tweaksToApply = new[] { "SpeedUpAeroSnap", "SpeedUpWindowsPeek", "SpeedUpMenus", "Win10ContextMenu", "DisableWin11Ads", "DisableSigninBlur", "EnableBatteryEstimatedTime", "RemoveShortcutArrow" };
                        break;
                }

                if (tweaksToApply != null)
                {
                    foreach (var twk in tweaksToApply)
                    {
                        if (FindName("Tgl" + twk) is CheckBox chk && chk.IsChecked != true)
                        {
                            chk.IsChecked = true;
                            TweakToggle_Click(chk, null);
                        }
                    }
                }
            }
        }
    }
}
