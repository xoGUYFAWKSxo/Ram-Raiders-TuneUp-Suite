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
    // Registry Guard: scan and repair of broken registry entries.
    public partial class MainWindow
    {
        private List<RegistryIssue> _scannedRegistryIssues = new List<RegistryIssue>();
        // ==========================================
        // TAB 2: REGISTRY GUARD ACTIONS
        // ==========================================

        private void BtnScanRegistry_Click(object sender, RoutedEventArgs e)
        {
            ProgRegistry.Visibility = Visibility.Visible;
            ProgRegistry.Value = 0;
            BtnRepairRegistry.IsEnabled = false;

            SendAsyncIpcRequest(IpcCommands.ScanRegistry, "",
                (progress) =>
                {
                    TxtRegistryStatus.Text = progress.Message;
                    ProgRegistry.Value = progress.ProgressPercent;
                },
                (completed) =>
                {
                    ProgRegistry.Visibility = Visibility.Collapsed;
                    if (completed.Status == IpcStatus.Success)
                    {
                        _scannedRegistryIssues = JsonSerializer.Deserialize<List<RegistryIssue>>(completed.Payload ?? "null") ?? new List<RegistryIssue>();
                        LvwRegistryIssues.ItemsSource = _scannedRegistryIssues;
                        TxtRegistryStatus.Text = $"Scan complete. Found {_scannedRegistryIssues.Count} broken entries. Check the ones you want to remove, then click Repair.";
                        BtnRepairRegistry.IsEnabled = _scannedRegistryIssues.Count > 0;
                        BtnToggleSelectRegistry.IsEnabled = _scannedRegistryIssues.Count > 0;
                        _registryAllSelected = false;
                        BtnToggleSelectRegistry.Content = "✓ Select All";
                    }
                    else
                    {
                        TxtRegistryStatus.Text = $"Registry Scan Failed: {completed.Message}";
                    }
                }
            );
        }

        private bool _registryAllSelected = false;

        private void BtnToggleSelectRegistry_Click(object sender, RoutedEventArgs e)
        {
            if (_scannedRegistryIssues == null || _scannedRegistryIssues.Count == 0) return;

            _registryAllSelected = !_registryAllSelected;
            foreach (var issue in _scannedRegistryIssues)
            {
                issue.IsSelected = _registryAllSelected;
            }

            BtnToggleSelectRegistry.Content = _registryAllSelected ? "✕ Deselect All" : "✓ Select All";
        }

        private void BtnRepairRegistry_Click(object sender, RoutedEventArgs e)
        {
            // Only the entries the user ticked are repaired. Previously the whole scan result was sent
            // regardless of the checkboxes (which were bound to a property RegistryIssue didn't have).
            var selectedIssues = _scannedRegistryIssues.Where(i => i.IsSelected).ToList();
            if (selectedIssues.Count == 0)
            {
                MessageBox.Show("No entries are selected. Check the boxes next to the entries you want to remove, or use 'Select All'.", "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Remove {selectedIssues.Count} registry entr{(selectedIssues.Count == 1 ? "y" : "ies")}?\n\n" +
                "Each key is exported to a .reg backup first (in %AppData%\\GalacticTuneUp\\RegistryBackups), " +
                "so you can restore it by double-clicking the backup file.",
                "Confirm Registry Repair", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            ProgRegistry.Visibility = Visibility.Visible;
            ProgRegistry.Value = 0;
            BtnRepairRegistry.IsEnabled = false;
            BtnToggleSelectRegistry.IsEnabled = false;

            // Only result IDs go to the engine — it repairs what its own scan found, never keys we send.
            string payload = JsonSerializer.Serialize(selectedIssues.Select(i => i.Id));

            SendAsyncIpcRequest(IpcCommands.RepairRegistry, payload,
                (progress) =>
                {
                    TxtRegistryStatus.Text = progress.Message;
                    ProgRegistry.Value = progress.ProgressPercent;
                },
                (completed) =>
                {
                    ProgRegistry.Visibility = Visibility.Collapsed;
                    if (completed.Status == IpcStatus.Success)
                    {
                        MessageBox.Show($"Registry repair complete.\n{selectedIssues.Count} selected entr{(selectedIssues.Count == 1 ? "y was" : "ies were")} backed up and removed.", "Registry Repair", MessageBoxButton.OK, MessageBoxImage.Information);
                        TxtRegistryStatus.Text = "Registry cleaned and backed up.";
                        LvwRegistryIssues.ItemsSource = null;
                        _scannedRegistryIssues.Clear();
                        BtnToggleSelectRegistry.IsEnabled = false;
                    }
                    else
                    {
                        // Scan results are single-use, so the list is cleared and a rescan shows
                        // what is really left.
                        MessageBox.Show($"Not everything was repaired.\n\n{completed.Message}\n\nScan again to see what is left.", "Registry Repair", MessageBoxButton.OK, MessageBoxImage.Warning);
                        TxtRegistryStatus.Text = "Repair incomplete. Scan again to see what is left.";
                        LvwRegistryIssues.ItemsSource = null;
                        _scannedRegistryIssues.Clear();
                        BtnToggleSelectRegistry.IsEnabled = false;
                    }
                }
            );
        }
    }
}
