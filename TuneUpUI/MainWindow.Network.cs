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
    // Network optimizer.
    public partial class MainWindow
    {
        // ── NetOptimizer Handlers ───────────────────────────────────────────────────

        private void RefreshNetworkStatus()
        {
            Task.Run(() =>
            {
                var resp = SendIpcRequest(IpcCommands.GetNetworkOptimizationStatus, null);
                if (resp?.Status == IpcStatus.Success && !string.IsNullOrEmpty(resp.Payload))
                {
                    try
                    {
                        var settings = JsonSerializer.Deserialize<NetSettings>(resp.Payload);
                        Dispatcher.Invoke(() =>
                        {
                            TxtNetAutoTune.Text = $"AutoTune: {settings?.AutoTuningLevel ?? "-"}";
                            TxtNetEcn.Text = $"ECN: {settings?.EcnCapability ?? "-"}";
                            TxtNetTimestamps.Text = $"Timestamps: {settings?.Timestamps ?? "-"}";
                            TxtNetRss.Text = $"RSS: {settings?.Rss ?? "-"}";
                            TxtNetNagle.Text = $"TcpNoDelay / TcpAckFrequency (connected adapters): " +
                                               (settings?.TcpNoDelay == true && settings.TcpAckFrequencyOne ? "on" : "off");
                            bool optimal = settings?.IsOptimal == true;
                            TxtNetSummary.Text = optimal
                                ? "✔ All optimizations are active."
                                : "Not optimized yet — Apply Optimizations sets the values above (Revert restores your previous ones).";
                            TxtNetSummary.Foreground = optimal
                                ? (System.Windows.Media.Brush)FindResource("BrushAccentGreen")
                                : (System.Windows.Media.Brush)FindResource("BrushTextMuted");
                        });
                    }
                    catch { }
                }
            });
        }

        private void BtnNetApply_Click(object sender, RoutedEventArgs e)
        {
            BtnNetApply.IsEnabled = false;
            BtnNetApply.Content = "Applying...";
            Task.Run(() =>
            {
                var resp = SendIpcRequestStreaming(IpcCommands.ApplyNetworkOptimizations, null,
                    progress => Dispatcher.Invoke(() => BtnNetApply.Content = progress.Message));
                Dispatcher.Invoke(() =>
                {
                    BtnNetApply.IsEnabled = true;
                    BtnNetApply.Content = "\u26A1 Apply Optimizations";
                    AppendMonitorEvent(resp?.Message ?? "[NetOptimizer] Applied.");
                    RefreshNetworkStatus();
                });
            });
        }

        private void BtnNetRevert_Click(object sender, RoutedEventArgs e)
        {
            BtnNetRevert.IsEnabled = false;
            Task.Run(() =>
            {
                var resp = SendIpcRequestStreaming(IpcCommands.RevertNetworkOptimizations, null,
                    progress => Dispatcher.Invoke(() => AppendMonitorEvent($"[NetOptimizer] {progress.Message}")));
                Dispatcher.Invoke(() =>
                {
                    BtnNetRevert.IsEnabled = true;
                    AppendMonitorEvent(resp?.Message ?? "[NetOptimizer] Reverted.");
                    RefreshNetworkStatus();
                });
            });
        }

        private void BtnNetRefresh_Click(object sender, RoutedEventArgs e) => RefreshNetworkStatus();

    }
}
