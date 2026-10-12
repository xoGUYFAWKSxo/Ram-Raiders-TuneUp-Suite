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
    // Driver DPC & interrupt load monitor.
    public partial class MainWindow
    {
        // ── LatencyAuditor Handlers ─────────────────────────────────────────────────

        private DispatcherTimer? _latencyRefreshTimer;

        private void BtnStartLatency_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() =>
            {
                var resp = SendIpcRequest(IpcCommands.StartLatencyAudit, null);
                Dispatcher.Invoke(() =>
                {
                    BtnStartLatency.IsEnabled = false;
                    BtnStopLatency.IsEnabled = true;
                    TxtLatencyStatus.Text = "Auditing in progress — results update every 2 seconds.";

                    _latencyRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    _latencyRefreshTimer.Tick += LatencyRefreshTimer_Tick;
                    _latencyRefreshTimer.Start();
                });
            });
        }

        private void BtnStopLatency_Click(object sender, RoutedEventArgs e)
        {
            _latencyRefreshTimer?.Stop();
            _latencyRefreshTimer = null;
            Task.Run(() =>
            {
                SendIpcRequest(IpcCommands.StopLatencyAudit, null);
                Dispatcher.Invoke(() =>
                {
                    BtnStartLatency.IsEnabled = true;
                    BtnStopLatency.IsEnabled = false;
                    TxtLatencyStatus.Text = "Audit stopped. Results are the last sampled values.";
                });
            });
        }

        private void LatencyRefreshTimer_Tick(object? sender, EventArgs e)
        {
            Task.Run(() =>
            {
                var resp = SendIpcRequest(IpcCommands.GetLatencyReport, null);
                if (resp?.Status == IpcStatus.Success && !string.IsNullOrEmpty(resp.Payload))
                {
                    try
                    {
                        var report = JsonSerializer.Deserialize<LatencyReport>(resp.Payload);
                        Dispatcher.Invoke(() =>
                        {
                            IcLatencyDrivers.ItemsSource = report?.Cores;
                            if (report?.Summary != null)
                                TxtLatencyStatus.Text = report.Summary;
                        });
                    }
                    catch { }
                }
            });
        }
    }
}
