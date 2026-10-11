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
    // Storage Migrator: move folders to another drive, history and undo.
    public partial class MainWindow
    {
        // ── StorageMigrator Handlers ────────────────────────────────────────────────

        private string _migSource = null;
        private string _migDest = null;

        private void BtnMigBrowseSource_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select the game directory to migrate",
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _migSource = dialog.SelectedPath;
                TxtMigSource.Text = _migSource;
                UpdateMigButtons();
            }
        }

        private void BtnMigBrowseDest_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select the destination parent folder (e.g., D:\\Games)",
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                // Destination = chosen folder + source folder name
                string folderName = System.IO.Path.GetFileName(_migSource ?? "GameFiles");
                _migDest = System.IO.Path.Combine(dialog.SelectedPath, folderName);
                TxtMigDest.Text = _migDest;
                UpdateMigButtons();
            }
        }

        private void UpdateMigButtons()
        {
            bool hasSource = !string.IsNullOrEmpty(_migSource);
            bool hasDest   = !string.IsNullOrEmpty(_migDest);
            BtnMigAnalyze.IsEnabled = hasSource;
            BtnMigrate.IsEnabled = hasSource && hasDest;
        }

        private void BtnMigAnalyze_Click(object sender, RoutedEventArgs e)
        {
            TxtMigAnalysis.Visibility = Visibility.Collapsed;
            Task.Run(() =>
            {
                var resp = SendIpcRequest(IpcCommands.AnalyzeMigration, _migSource);
                Dispatcher.Invoke(() =>
                {
                    if (resp?.Status == IpcStatus.Success && !string.IsNullOrEmpty(resp.Payload))
                    {
                        try
                        {
                            var analysis = JsonSerializer.Deserialize<MigrationAnalysis>(resp.Payload);
                            string msg = analysis?.SourceExists == true
                                ? $"Source: {FormatBytes(analysis.SourceSizeBytes)} | " +
                                  (analysis.IsAlreadySymlink ? "⚠️ Already a symbolic link" : "✓ Ready to migrate")
                                : $"⚠️ {analysis?.Error ?? "Source not accessible"}";

                            TxtMigAnalysis.Text = msg;
                            TxtMigAnalysis.Visibility = Visibility.Visible;
                        }
                        catch { }
                    }
                });
            });
        }

        private void BtnMigrate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_migSource) || string.IsNullOrEmpty(_migDest)) return;

            var result = MessageBox.Show(
                $"This will permanently move:\n{_migSource}\n\nTo:\n{_migDest}\n\nA symbolic link will be created at the original path. Continue?",
                "Confirm Migration", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            BtnMigrate.IsEnabled = false;
            BtnMigAnalyze.IsEnabled = false;
            ProgMigrate.Visibility = Visibility.Visible;
            TxtMigProgress.Visibility = Visibility.Visible;
            ProgMigrate.Value = 0;

            _migrationCts?.Dispose();
            _migrationCts = new System.Threading.CancellationTokenSource();
            var migrationCt = _migrationCts.Token;
            BtnCancelMigration.IsEnabled = true;
            BtnCancelMigration.Visibility = Visibility.Visible;

            Task.Run(() =>
            {
                var resp = SendIpcRequestStreaming(IpcCommands.MigrateDirectory, IpcPayload.Serialize(new MigrateRequest(_migSource, _migDest)),
                    progress => Dispatcher.Invoke(() =>
                    {
                        ProgMigrate.Value = progress.ProgressPercent;
                        TxtMigProgress.Text = progress.Message;
                    }), migrationCt);
                Dispatcher.Invoke(() =>
                {
                    BtnCancelMigration.Visibility = Visibility.Collapsed;
                    BtnMigrate.IsEnabled = true;
                    BtnMigAnalyze.IsEnabled = true;
                    ProgMigrate.Visibility = Visibility.Collapsed;
                    TxtMigProgress.Visibility = Visibility.Collapsed;

                    if (resp?.Status == IpcStatus.Success)
                    {
                        AppendMonitorEvent($"[StorageMigrator] {resp.Message}");
                        RefreshMigrationHistory();
                        // Clear paths
                        _migSource = null;
                        _migDest = null;
                        TxtMigSource.Text = "No directory selected...";
                        TxtMigDest.Text = "No destination selected...";
                        UpdateMigButtons();
                    }
                    else
                    {
                        AppendMonitorEvent($"[StorageMigrator] ⚠️ {resp?.Message ?? "Migration failed"}");
                    }
                });
            });
        }

        private void RefreshMigrationHistory()
        {
            Task.Run(() =>
            {
                var resp = SendIpcRequest(IpcCommands.GetMigrationHistory, null);
                Dispatcher.Invoke(() =>
                {
                    if (resp?.Status == IpcStatus.Success && !string.IsNullOrEmpty(resp.Payload))
                    {
                        try
                        {
                            var history = JsonSerializer.Deserialize<System.Collections.Generic.List<MigrationRecord>>(resp.Payload);
                            IcMigrationHistory.ItemsSource = history;
                            TxtNoHistory.Visibility = (history == null || history.Count == 0)
                                ? Visibility.Visible : Visibility.Collapsed;
                        }
                        catch { }
                    }
                });
            });
        }

        private void BtnMigRefreshHistory_Click(object sender, RoutedEventArgs e) => RefreshMigrationHistory();

        private System.Threading.CancellationTokenSource? _migrationCts;

        private void BtnCancelMigration_Click(object sender, RoutedEventArgs e)
        {
            _migrationCts?.Cancel();
            BtnCancelMigration.IsEnabled = false;
            TxtMigProgress.Text = "Cancelling… (only possible while files are still being copied)";
        }

        private void BtnUndoMigration_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string originalPath) return;

            if (MessageBox.Show(
                    $"Move this folder back to its original location?\n\n{originalPath}\n\n" +
                    "The files are copied back and checked before the link is removed, and the migrated copy is deleted afterwards.",
                    "Undo Migration", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            btn.IsEnabled = false;
            ProgMigrate.Visibility = Visibility.Visible;
            TxtMigProgress.Visibility = Visibility.Visible;
            ProgMigrate.Value = 0;

            Task.Run(() =>
            {
                var resp = SendIpcRequestStreaming(IpcCommands.UndoMigration, originalPath,
                    progress => Dispatcher.Invoke(() =>
                    {
                        ProgMigrate.Value = progress.ProgressPercent;
                        TxtMigProgress.Text = progress.Message;
                    }));
                Dispatcher.Invoke(() =>
                {
                    btn.IsEnabled = true;
                    ProgMigrate.Visibility = Visibility.Collapsed;
                    TxtMigProgress.Visibility = Visibility.Collapsed;
                    AppendMonitorEvent($"[StorageMigrator] {(resp?.Status == IpcStatus.Success ? "" : "\u26A0\uFE0F ")}{resp?.Message ?? "Undo failed"}");
                    RefreshMigrationHistory();
                });
            });
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
