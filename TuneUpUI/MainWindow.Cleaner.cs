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
    // System Cleaner: junk / empty-folder / duplicate scans, selection and cleaning.
    public partial class MainWindow
    {
        private List<ScanItem> _scannedJunkItems = new List<ScanItem>();
        // ==========================================
        // TAB 1: SYSTEM CLEANER ACTIONS
        // ==========================================

        private void BtnScanJunk_Click(object sender, RoutedEventArgs e)
        {
            ProgCleaner.Visibility = Visibility.Visible;
            ProgCleaner.IsIndeterminate = false;
            ProgCleaner.Value = 0;
            BtnCleanJunk.IsEnabled = false;

            _scannedJunkItems = new List<ScanItem>();
            LvwJunkItems.ItemsSource = null;

            bool scanEmpty = ChkEmptyFolders.IsChecked == true;
            bool scanDups = ChkDuplicateFiles.IsChecked == true;
            var ct = BeginCleanerOperation();

            var groups = new ScanJunkRequest(ChkTempFiles.IsChecked == true, ChkWinUpdate.IsChecked == true,
                ChkBrowserCache.IsChecked == true);
            SendAsyncIpcRequest(IpcCommands.ScanJunk, IpcPayload.Serialize(groups),
                (progress) =>
                {
                    TxtCleanerStatus.Text = progress.Message;
                    ProgCleaner.Value = progress.ProgressPercent;
                },
                (completed) =>
                {
                    if (completed.Status == IpcStatus.Success)
                    {
                        var items = JsonSerializer.Deserialize<List<ScanItem>>(completed.Payload ?? "null");
                        if (items != null) _scannedJunkItems.AddRange(items);
                    }
                    else
                    {
                        TxtCleanerStatus.Text = $"Scan Failed: {completed.Message}";
                    }

                    if (ct.IsCancellationRequested) FinishScan(cancelled: true);
                    else if (scanEmpty) RunAdvancedScan(IpcCommands.ScanAdvancedEmptyFolders, scanDups, ct);
                    else if (scanDups) RunAdvancedScan(IpcCommands.ScanAdvancedDuplicates, false, ct);
                    else FinishScan();
                },
                ct
            );
        }

        // ── Cancelling cleaner work ─────────────────────────────────────────────
        private System.Threading.CancellationTokenSource? _cleanerCts;

        private System.Threading.CancellationToken BeginCleanerOperation()
        {
            _cleanerCts?.Dispose();
            _cleanerCts = new System.Threading.CancellationTokenSource();
            BtnCancelCleaner.IsEnabled = true;
            BtnCancelCleaner.Visibility = Visibility.Visible;
            return _cleanerCts.Token;
        }

        private void EndCleanerOperation() => BtnCancelCleaner.Visibility = Visibility.Collapsed;

        private void BtnCancelCleaner_Click(object sender, RoutedEventArgs e)
        {
            _cleanerCts?.Cancel();
            BtnCancelCleaner.IsEnabled = false;
            TxtCleanerStatus.Text = "Cancelling…";
        }

        private void RunAdvancedScan(string cmd, bool chainDups, System.Threading.CancellationToken ct)
        {
            ProgCleaner.IsIndeterminate = true;
            SendAsyncIpcRequest(cmd, "",
                (progress) =>
                {
                    TxtCleanerStatus.Text = progress.Message;
                },
                (completed) =>
                {
                    if (completed.Status == IpcStatus.Success)
                    {
                        var items = JsonSerializer.Deserialize<List<ScanItem>>(completed.Payload ?? "null");
                        if (items != null) _scannedJunkItems.AddRange(items);
                    }

                    if (ct.IsCancellationRequested) FinishScan(cancelled: true);
                    else if (chainDups) RunAdvancedScan(IpcCommands.ScanAdvancedDuplicates, false, ct);
                    else FinishScan();
                },
                ct);
        }

        private void FinishScan(bool cancelled = false)
        {
            EndCleanerOperation();
            ProgCleaner.IsIndeterminate = false;
            ProgCleaner.Visibility = Visibility.Collapsed;
            LvwJunkItems.ItemsSource = _scannedJunkItems;
            long totalBytes = _scannedJunkItems.Sum(item => item.TotalSizeBytes);
            TxtCleanerStatus.Text = cancelled
                ? $"Scan cancelled. Showing what was found before stopping: {_scannedJunkItems.Count} items, {totalBytes / (1024.0 * 1024.0):F1} MB."
                : $"Scan complete. Found {_scannedJunkItems.Count} rules mapping to {totalBytes / (1024.0 * 1024.0):F1} MB. Check the items you want to clean, then click Empty Junk Files.";
            BtnCleanJunk.IsEnabled = _scannedJunkItems.Count > 0;
            BtnToggleSelectJunk.IsEnabled = _scannedJunkItems.Count > 0;
            // Scan results arrive unchecked, so the button's first action must be "Select All".
            _junkAllSelected = false;
            BtnToggleSelectJunk.Content = "✓ Select All";
        }

        private bool _junkAllSelected = false;

        private void BtnToggleSelectJunk_Click(object sender, RoutedEventArgs e)
        {
            if (_scannedJunkItems == null || _scannedJunkItems.Count == 0) return;

            _junkAllSelected = !_junkAllSelected;
            foreach (var item in _scannedJunkItems)
            {
                item.IsSelected = _junkAllSelected;
            }

            BtnToggleSelectJunk.Content = _junkAllSelected ? "✕ Deselect All" : "✓ Select All";
            LvwJunkItems.Items.Refresh();
        }



        private GridViewColumnHeader? _lastHeaderClicked = null;
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;

        private void LvwJunkItems_ColumnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is GridViewColumnHeader headerClicked) || headerClicked.Column == null)
                return;

            string? header = headerClicked.Column.Header as string;
            if (string.IsNullOrEmpty(header) || header == "Select")
                return;

            string? sortBy = header switch
            {
                "Category" => "Category",
                "Cleaner Target" => "RuleName",
                "Size" => "TotalSizeBytes",
                "Files Found" => "FilePaths.Count",
                _ => null
            };

            if (sortBy == null) return;

            ListSortDirection direction;

            if (headerClicked != _lastHeaderClicked)
            {
                direction = ListSortDirection.Descending;
            }
            else
            {
                direction = _lastDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            }

            var view = CollectionViewSource.GetDefaultView(LvwJunkItems.ItemsSource);
            if (view == null) return;

            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(sortBy, direction));
            view.Refresh();

            _lastHeaderClicked = headerClicked;
            _lastDirection = direction;
        }

        private void LvwJunkItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LvwJunkItems.SelectedItem is ScanItem selected && selected.FilePaths?.Count > 0)
            {
                // Only show the detail panel for rows that have real inspectable paths
                // (Winapp2 rules target many files — cap display to 200 for performance)
                bool isAdvanced = selected.Category == "Empty Folders" || selected.Category == AdvancedScanner.DuplicateFilesCategory;
                if (isAdvanced || selected.FilePaths.Count <= 200)
                {
                    TxtDetailHeader.Text = $"{selected.RuleName}  —  {selected.FilePaths.Count} path(s)";
                    IcFilePaths.ItemsSource = selected.FilePaths;
                    PnlFileDetail.Visibility = Visibility.Visible;
                }
                else
                {
                    // For large Winapp2 rule sets, show a condensed summary instead
                    TxtDetailHeader.Text = $"{selected.RuleName}  —  {selected.FilePaths.Count} files (too many to list individually)";
                    IcFilePaths.ItemsSource = selected.FilePaths.Take(50).Concat(new[] { $"... and {selected.FilePaths.Count - 50} more" }).ToList();
                    PnlFileDetail.Visibility = Visibility.Visible;
                }
            }
            else
            {
                PnlFileDetail.Visibility = Visibility.Collapsed;
            }
        }

        private void FilePathItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is string path)
            {
                try
                {
                    string target = path;
                    // If it's a file, select it in Explorer; if it's a folder, just open the folder
                    if (File.Exists(target))
                    {
                        // Use explorer /select to highlight the file
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{target}\"",
                            UseShellExecute = true
                        });
                    }
                    else if (Directory.Exists(target))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"\"{target}\"",
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        // Path doesn't exist yet (already deleted or pattern-based) — open parent
                        string? parent = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"\"{parent}\"",
                                UseShellExecute = true
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open location:\n{ex.Message}", "Explorer Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void BtnCleanJunk_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = _scannedJunkItems.Where(i => i.IsSelected).ToList();
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("No items are selected. Check the boxes next to the items you want to clean, or use 'Select All'.", "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string confirmText = Presentation.CleanConfirmation.Build(selectedItems);
            if (MessageBox.Show(confirmText, "Confirm Clean", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            ProgCleaner.Visibility = Visibility.Visible;
            ProgCleaner.Value = 0;
            BtnCleanJunk.IsEnabled = false;

            // Only result IDs go to the engine — it cleans what its own scan found, never paths we send.
            string payload = JsonSerializer.Serialize(selectedItems.Select(i => i.Id));

            var cleanCt = BeginCleanerOperation();
            SendAsyncIpcRequest(IpcCommands.CleanJunk, payload,
                (progress) =>
                {
                    TxtCleanerStatus.Text = progress.Message;
                    ProgCleaner.Value = progress.ProgressPercent;
                },
                (completed) =>
                {
                    EndCleanerOperation();
                    ProgCleaner.Visibility = Visibility.Collapsed;
                    if (completed.Status == IpcStatus.Success)
                    {
                        long cleanedBytes = long.Parse(completed.Payload ?? "0");
                        MessageBox.Show($"Clean complete.\nDeleted {cleanedBytes / (1024.0 * 1024.0):F1} MB of files.", "Clean Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                        TxtCleanerStatus.Text = "Clean finished.";
                        LvwJunkItems.ItemsSource = null;
                        _scannedJunkItems.Clear();
                        StatsTimer_Tick(null, EventArgs.Empty); // Refresh stats
                    }
                    else
                    {
                        // A cancelled clean stops between files and reports what it freed.
                        TxtCleanerStatus.Text = cleanCt.IsCancellationRequested
                            ? (completed.Message ?? "Clean cancelled.") + " Scan again to see what's left."
                            : $"Clean Failed: {completed.Message}";
                        if (cleanCt.IsCancellationRequested)
                        {
                            LvwJunkItems.ItemsSource = null;
                            _scannedJunkItems.Clear();
                        }
                    }
                },
                cleanCt
            );
        }
    }
}
