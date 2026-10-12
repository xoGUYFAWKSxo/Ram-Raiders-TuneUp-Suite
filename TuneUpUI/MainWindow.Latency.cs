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
                    TxtLatencyStatus.Text = "Measuring… results appear after 5 seconds and update every 2 seconds.";

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

        private void BtnLatencyHelp_Click(object sender, RoutedEventArgs e)
        {
            bool show = PnlLatencyHelp.Visibility != Visibility.Visible;
            PnlLatencyHelp.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            // The core and driver tables fill the card, which left the help panel no height at all,
            // so the help takes their place while it's open.
            var tables = show ? Visibility.Collapsed : Visibility.Visible;
            GrdLatencyCoreHeader.Visibility = SepLatencyCores.Visibility = SvLatencyCores.Visibility = PnlDriverSection.Visibility = tables;
            BtnLatencyHelp.Content = show ? " Back to the results" : " What is this, and how do I fix it?";
            // While open, the card shares the leftover height with the event log so the help text
            // scrolls inside the window instead of running off the bottom.
            RowLatencyCard.Height = show ? new GridLength(2, GridUnitType.Star) : GridLength.Auto;
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
                            if (report != null) ShowDriverTrace(report);
                            BtnSaveLatency.IsEnabled = report?.SampleCount > 0;
                        });
                    }
                    catch { }
                }
            });
        }

        private void BtnSaveLatency_Click(object sender, RoutedEventArgs e)
        {
            BtnSaveLatency.IsEnabled = false;
            Task.Run(() =>
            {
                // A fresh report, so the file matches the moment of saving, not the last refresh.
                var resp = SendIpcRequest(IpcCommands.GetLatencyReport, null);
                LatencyReport? report = null;
                if (resp?.Status == IpcStatus.Success && !string.IsNullOrEmpty(resp.Payload))
                {
                    try { report = JsonSerializer.Deserialize<LatencyReport>(resp.Payload); } catch { }
                }
                Dispatcher.Invoke(() =>
                {
                    BtnSaveLatency.IsEnabled = true;
                    if (report == null || report.SampleCount == 0)
                    {
                        MessageBox.Show(this, "There are no results to save yet. Start the audit first.", "Save Trace",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    SaveLatencyTrace(report);
                });
            });
        }

        private void SaveLatencyTrace(LatencyReport report)
        {
            var now = DateTime.Now;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save DPC trace",
                FileName = Presentation.LatencyTraceFile.DefaultFileName(now),
                Filter = "Text report (*.txt)|*.txt|Spreadsheet (*.csv)|*.csv",
                DefaultExt = ".txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                bool csv = dialog.FilterIndex == 2 || dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                string content = csv
                    ? Presentation.LatencyTraceFile.ToCsv(report)
                    : Presentation.LatencyTraceFile.ToText(report, now, System.Runtime.InteropServices.RuntimeInformation.OSDescription);
                // CSV gets a BOM so Excel reads "µ" and other non-ASCII text correctly.
                File.WriteAllText(dialog.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: csv));
                MessageBox.Show(this, $"Trace saved to:\n{dialog.FileName}", "Save Trace", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't save the trace: {ex.Message}", "Save Trace", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowDriverTrace(LatencyReport report)
        {
            IcLatencyDriverList.ItemsSource = report.Drivers;

            if (!report.DriverTraceAvailable)
                TxtDriverTraceStatus.Text = string.IsNullOrEmpty(report.DriverTraceMessage)
                    ? "The driver trace isn't running."
                    : $"{report.DriverTraceMessage} The core table above still works.";
            else if (report.Drivers.Count == 0)
                TxtDriverTraceStatus.Text = "Timing every driver run… waiting for the first results.";
            else
                TxtDriverTraceStatus.Text = "Every driver run is timed. Hover over a driver for what to do about it."
                    + (report.DriverEventsLost > 0 ? $" ({report.DriverEventsLost:N0} runs were missed because the PC was too busy to record them.)" : "");

            var worst = report.Drivers.FirstOrDefault(d => d.Status != "OK");
            TxtDriverAdvice.Visibility = worst != null ? Visibility.Visible : Visibility.Collapsed;
            if (worst != null)
                TxtDriverAdvice.Text = $"What to do about {worst.Driver} ({worst.Description}): {worst.Advice}";
        }
    }
}
