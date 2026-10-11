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
    // DebloatGuard: telemetry services and removable packages.
    public partial class MainWindow
    {
        // ── DebloatGuard Handlers ───────────────────────────────────────────────────

        private void LoadDebloatStatus()
        {
            TxtDebloatLoading.Text = "Loading service statuses...";
            TxtDebloatLoading.Visibility = Visibility.Visible;

            Task.Run(() =>
            {
                var resp = SendIpcRequest(IpcCommands.GetDebloatStatus, null);
                if (resp?.Status == IpcStatus.Success && !string.IsNullOrEmpty(resp.Payload))
                {
                    try
                    {
                        var status = JsonSerializer.Deserialize<DebloatStatus>(resp.Payload);
                        Dispatcher.Invoke(() =>
                        {
                            TxtDebloatLoading.Visibility = Visibility.Collapsed;
                            PnlServiceToggles.Children.Clear();

                            foreach (var svc in status?.Services ?? new())
                            {
                                if (!svc.IsAvailable) continue;

                                var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                                var sp = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
                                sp.Children.Add(new TextBlock
                                {
                                    Text = svc.DisplayName,
                                    Foreground = System.Windows.Media.Brushes.White,
                                    FontSize = 14,
                                    FontWeight = FontWeights.SemiBold
                                });
                                sp.Children.Add(new TextBlock
                                {
                                    Text = svc.Description,
                                    FontSize = 12,
                                    TextWrapping = TextWrapping.Wrap,
                                    Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted")
                                });

                                var toggle = new CheckBox
                                {
                                    Style = (Style)FindResource("PremiumToggle"),
                                    Content = "",
                                    IsChecked = !svc.IsDisabled,
                                    Tag = svc.ServiceName,
                                    VerticalAlignment = VerticalAlignment.Center
                                };
                                toggle.Click += ServiceToggle_Click;

                                Grid.SetColumn(sp, 0);
                                Grid.SetColumn(toggle, 1);
                                grid.Children.Add(sp);
                                grid.Children.Add(toggle);
                                PnlServiceToggles.Children.Add(grid);
                            }
                        });
                    }
                    catch { }
                }
            });
        }

        private void ServiceToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox chk && chk.Tag is string svcName)
            {
                bool disable = !(chk.IsChecked ?? false);
                Task.Run(() =>
                {
                    var resp = _engine.SendJson(IpcCommands.SetServiceState, new SetServiceRequest(svcName, disable));
                    Dispatcher.Invoke(() =>
                    {
                        if (resp?.Message != null)
                            AppendMonitorEvent(resp.Message);
                    });
                });
            }
        }

        private void BtnScanAppx_Click(object sender, RoutedEventArgs e)
        {
            BtnScanAppx.IsEnabled = false;
            BtnScanAppx.Content = "Scanning...";

            Task.Run(() =>
            {
                var resp = SendIpcRequest(IpcCommands.GetDebloatStatus, null);
                Dispatcher.Invoke(() =>
                {
                    BtnScanAppx.IsEnabled = true;
                    BtnScanAppx.Content = "Scan Packages";

                    if (resp?.Status == IpcStatus.Success && !string.IsNullOrEmpty(resp.Payload))
                    {
                        try
                        {
                            var status = JsonSerializer.Deserialize<DebloatStatus>(resp.Payload);
                            var pkgs = (status?.DetectedPackages ?? new())
                                .Select(p => new SelectableAppxPackage
                                {
                                    Name = p.Name,
                                    FriendlyName = p.FriendlyName,
                                    Publisher = p.Publisher,
                                    PackageFullName = p.PackageFullName,
                                    Category = p.Category,
                                    IsOem = p.IsOem
                                }).ToList();

                            IcAppxPackages.ItemsSource = pkgs;
                            BtnRemoveAppx.IsEnabled = pkgs.Count > 0;
                            TxtNoAppx.Visibility = pkgs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                            if (pkgs.Count == 0)
                                AppendMonitorEvent("[DebloatGuard] No known bloatware packages detected on this system.");
                            else
                                AppendMonitorEvent($"[DebloatGuard] Detected {pkgs.Count} bloatware/OEM package(s). Review and select which to remove.");
                        }
                        catch { }
                    }
                });
            });
        }

        private void BtnRemoveAppx_Click(object sender, RoutedEventArgs e)
        {
            var selected = (IcAppxPackages.ItemsSource as System.Collections.Generic.List<SelectableAppxPackage>)?
                .Where(p => p.IsSelected).ToList();

            if (selected == null || selected.Count == 0)
            {
                AppendMonitorEvent("[DebloatGuard] No packages selected for removal.");
                return;
            }

            BtnRemoveAppx.IsEnabled = false;
            BtnRemoveAppx.Content = $"Removing {selected.Count} package(s)...";

            Task.Run(() =>
            {
                // Strictly one at a time: each removal is awaited to completion (the streaming call
                // returns only on the final reply), instead of firing the next after a fixed delay.
                foreach (var pkg in selected)
                {
                    var resp = SendIpcRequestStreaming(IpcCommands.RemoveAppxPackage, pkg.PackageFullName,
                        progress => Dispatcher.Invoke(() => BtnRemoveAppx.Content = progress.Message));
                    Dispatcher.Invoke(() => AppendMonitorEvent(resp?.Message ?? $"Processed {pkg.FriendlyName}"));
                }
                Dispatcher.Invoke(() =>
                {
                    BtnRemoveAppx.Content = "Remove Selected Packages";
                    BtnRemoveAppx.IsEnabled = true;
                    AppendMonitorEvent("[DebloatGuard] Removal pass complete. Restart may be required for some packages.");
                });
            });
        }

    }
}
