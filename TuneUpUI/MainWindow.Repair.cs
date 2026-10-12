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
    // Component Repair: CBS log analysis and SFC / DISM repair.
    public partial class MainWindow
    {
        // ==========================================
        // TAB 3: COMPONENT REPAIR ACTIONS
        // ==========================================

        private void BtnScanComponent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            btn.IsEnabled = false;
            TxtComponentStatus.Text = "Analyzing CBS.log file structures...";

            Task.Run(() =>
            {
                var resp = SendIpcRequest(IpcCommands.GetSystemIntegrityStatus);
                Dispatcher.Invoke(() =>
                {
                    btn.IsEnabled = true;
                    if (resp != null && resp.Status == IpcStatus.Success)
                    {
                        var report = JsonSerializer.Deserialize<Dictionary<string, object>>(resp.Payload ?? "null");
                        if (report == null) return;

                        bool hasCorruption = ShowCbsLogs(report);

                        if (hasCorruption)
                        {
                            TxtComponentStatus.Text = "System corruption detected! Baseline Repair recommended.";
                        }
                        else
                        {
                            TxtComponentStatus.Text = "No structural system file errors found in recent CBS logs.";
                        }
                    }
                    else
                    {
                        TxtComponentStatus.Text = "Failed to fetch integrity status. Make sure the background engine is elevated.";
                    }
                });
            });
        }

        // Lists the CBS findings: red only when there is corruption (the "nothing found" line was
        // shown in red too). Returns whether corruption was found.
        private bool ShowCbsLogs(Dictionary<string, object> report)
        {
            bool hasCorruption = string.Equals(report["HasCorruption"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            var logsJson = report["CorruptionLogs"]?.ToString() ?? "null";
            LstCbsCorruptions.ItemsSource = JsonSerializer.Deserialize<List<string>>(logsJson);
            LstCbsCorruptions.Foreground = hasCorruption
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44))
                : (System.Windows.Media.Brush)FindResource("BrushTextMain");
            return hasCorruption;
        }

        private void BtnRepairComponent_Click(object sender, RoutedEventArgs e)
        {
            ProgComponent.Visibility = Visibility.Visible;
            ProgComponent.Value = 0;
            BtnRepairComponent.IsEnabled = false;

            SendAsyncIpcRequest(IpcCommands.RunSystemRepair, "",
                (progress) =>
                {
                    TxtComponentStatus.Text = progress.Message;
                    ProgComponent.Value = progress.ProgressPercent;
                },
                (completed) =>
                {
                    ProgComponent.Visibility = Visibility.Collapsed;
                    BtnRepairComponent.IsEnabled = true;
                    if (completed.Status == IpcStatus.Success)
                    {
                        // The engine now reports what DISM and SFC actually found and did.
                        MessageBox.Show(completed.Message ?? "System repair finished.", "System Repair", MessageBoxButton.OK, MessageBoxImage.Information);
                        TxtComponentStatus.Text = completed.Message ?? "Repair complete.";
                        // Re-scan CBS to refresh the log view
                        Task.Run(() =>
                        {
                            var r = SendIpcRequest(IpcCommands.GetSystemIntegrityStatus);
                            Dispatcher.Invoke(() =>
                            {
                                if (r != null && r.Status == IpcStatus.Success)
                                {
                                    var report = JsonSerializer.Deserialize<Dictionary<string, object>>(r.Payload ?? "null");
                                    if (report != null) ShowCbsLogs(report);
                                }
                            });
                        });
                    }
                    else
                    {
                        TxtComponentStatus.Text = $"Repair finished with problems: {completed.Message}";
                        MessageBox.Show(completed.Message ?? "The repair did not complete.", "System Repair", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            );
        }
    }
}
