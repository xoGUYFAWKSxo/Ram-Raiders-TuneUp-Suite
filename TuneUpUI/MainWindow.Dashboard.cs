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
    // Dashboard: live stats polling, standby purge and the module toggles.
    public partial class MainWindow
    {
        // ==========================================
        // POLLED SUBSYSTEM UPDATES
        // ==========================================

        private void StatsTimer_Tick(object? sender, EventArgs e)
        {
            if (_isConnecting) return;
            _isConnecting = true;

            Task.Run(() =>
            {
                var resp = SendIpcRequest(IpcCommands.GetEngineStats);
                Dispatcher.Invoke(() =>
                {
                    _isConnecting = false;
                    UpdateStatsUI(resp);
                });
            });
        }

        private bool _shownOtherCopyWarning;

        private void UpdateStatsUI(IpcResponse? resp)
        {
            if (resp == null || resp.Status != IpcStatus.Success)
            {
                StatusDot.Fill = System.Windows.Media.Brushes.Red;
                string? problem = _engine.LastConnectionProblem;
                bool otherCopy = _engine.OtherEngineRunning;
                TxtStatus.Text = otherCopy ? "Another copy is running" : "Engine Offline";
                TxtStatus.ToolTip = problem;

                // Say it once, plainly, rather than leaving a silent "offline".
                if (otherCopy && !_shownOtherCopyWarning)
                {
                    _shownOtherCopyWarning = true;
                    UiLog.Warn($"[UI] {problem}");
                    MessageBox.Show(problem, "Galactic Tune-Up", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }
            TxtStatus.ToolTip = null;

            StatusDot.Fill = System.Windows.Media.Brushes.Green;
            TxtStatus.Text = "Engine Online";

            try
            {
                // Typed: the engine's own EngineStatsPayload, not fields read out by name.
                var stats = JsonSerializer.Deserialize<EngineStatsPayload>(resp.Payload ?? "");
                if (stats == null) return;

                // 1. Memory stats
                var memory = Presentation.DashboardFormatter.Format(stats);
                TxtFreeRamPercent.Text = memory.FreePercent;
                TxtFreeRamBytes.Text = memory.AvailableOfTotal;
                TxtStandbyListSize.Text = memory.Standby;
                TxtTotalSaved.Text = memory.Reclaimed;

                // 2. Toggle Sync (Without triggering events recursively)
                bool govOn = stats.ProBalanceEnabled;
                bool autoPurgeOn = stats.AutoPurgeEnabled;
                bool gameOn = stats.GameModeEnabled;

                ToggleProBalance.Checked -= ToggleProBalance_Checked;
                ToggleProBalance.Unchecked -= ToggleProBalance_Unchecked;
                ToggleProBalance.IsChecked = govOn;
                ToggleProBalance.Checked += ToggleProBalance_Checked;
                ToggleProBalance.Unchecked += ToggleProBalance_Unchecked;

                ToggleAutoPurge.Checked -= ToggleAutoPurge_Checked;
                ToggleAutoPurge.Unchecked -= ToggleAutoPurge_Unchecked;
                ToggleAutoPurge.IsChecked = autoPurgeOn;
                ToggleAutoPurge.Checked += ToggleAutoPurge_Checked;
                ToggleAutoPurge.Unchecked += ToggleAutoPurge_Unchecked;

                ToggleGameMode.Checked -= ToggleGameMode_Checked;
                ToggleGameMode.Unchecked -= ToggleGameMode_Unchecked;
                ToggleGameMode.IsChecked = gameOn;
                ToggleGameMode.Checked += ToggleGameMode_Checked;
                ToggleGameMode.Unchecked += ToggleGameMode_Unchecked;

                // 3. Event Logs
                var logs = stats.EventLogs;
                if (logs != null)
                {
                    string? shownNewest = LstDashboardEvents.Items.Count > 0
                        ? LstDashboardEvents.Items[LstDashboardEvents.Items.Count - 1] as string
                        : null;
                    if (Presentation.EventLogView.NeedsRefresh(LstDashboardEvents.Items.Count, shownNewest, logs))
                    {
                        LstDashboardEvents.ItemsSource = logs;
                        LstMonitorEvents.ItemsSource = logs;

                        // Scroll to bottom
                        if (LstDashboardEvents.Items.Count > 0)
                            LstDashboardEvents.ScrollIntoView(LstDashboardEvents.Items[LstDashboardEvents.Items.Count - 1]);
                        if (LstMonitorEvents.Items.Count > 0)
                            LstMonitorEvents.ScrollIntoView(LstMonitorEvents.Items[LstMonitorEvents.Items.Count - 1]);
                    }

                    // Check for background tray toast notifications
                    if (logs.Count > 0)
                    {
                        string latestLog = logs[logs.Count - 1];
                        if (latestLog != _lastLogLine)
                        {
                            if (Presentation.TrayToastPolicy.ShouldNotify(latestLog,
                                    windowVisible: Visibility == Visibility.Visible,
                                    notificationsEnabled: _settings.EnableTrayNotifications,
                                    proBalanceToasts: _settings.ProBalanceToast,
                                    memoryToasts: _settings.MemoryPurgeToast))
                            {
                                _trayIcon?.ShowNotification("Galactic Tune-Up Engine", latestLog, false);
                            }
                            _lastLogLine = latestLog;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UiLog.Warn($"[WPF Stats] Parse error: {ex.Message}");
            }
        }

        // ==========================================
        // TAB 0: DASHBOARD ACTIONS
        // ==========================================

        private void BtnPurgeMemory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            btn.IsEnabled = false;

            Task.Run(() =>
            {
                var resp = SendIpcRequest(IpcCommands.PurgeStandbyList);
                Dispatcher.Invoke(() =>
                {
                    btn.IsEnabled = true;
                    if (resp != null && resp.Status == IpcStatus.Success)
                    {
                        long freedBytes = long.Parse(resp.Payload ?? "0");
                        MessageBox.Show($"Standby list emptied.\nThe standby cache shrank by {freedBytes / (1024.0 * 1024.0):F1} MB, which is now free memory. Windows refills this cache over time as files are used.", "Standby Purge", MessageBoxButton.OK, MessageBoxImage.Information);
                        StatsTimer_Tick(null, EventArgs.Empty); // Refresh
                    }
                    else
                    {
                        MessageBox.Show(resp?.Message ?? "Couldn't reach the engine to purge standby memory.", "Standby Purge Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            });
        }

        private void ToggleProBalance_Checked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest(IpcCommands.SetGovernorState, "true"));
        }

        private void ToggleProBalance_Unchecked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest(IpcCommands.SetGovernorState, "false"));
        }

        private void ToggleAutoPurge_Checked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest(IpcCommands.SetAutoPurgeState, "true"));
        }

        private void ToggleAutoPurge_Unchecked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest(IpcCommands.SetAutoPurgeState, "false"));
        }

        private void ToggleGameMode_Checked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest(IpcCommands.SetGameModeState, "true"));
        }

        private void ToggleGameMode_Unchecked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest(IpcCommands.SetGameModeState, "false"));
        }
    }
}
