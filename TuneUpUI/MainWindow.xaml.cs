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
    public partial class MainWindow : Window
    {
        private DispatcherTimer _statsTimer;
        private bool _isConnecting = false;
        private List<ScanItem> _scannedJunkItems = new List<ScanItem>();
        private List<RegistryIssue> _scannedRegistryIssues = new List<RegistryIssue>();
        
        // System Tray variables
        private TrayIconHelper _trayIcon;
        private string _lastLogLine = "";
        private bool _isExplicitExit = false;

        // Settings variables
        public class ApplicationSettings
        {
            public bool StartOnStartup { get; set; } = false;
            public bool LaunchUiMinimized { get; set; } = false;
            public bool MinimizeToTrayOnClose { get; set; } = true;
            public bool EnableTrayNotifications { get; set; } = true;
            public bool ProBalanceToast { get; set; } = true;
            public bool MemoryPurgeToast { get; set; } = true;
        }

        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
            "RamRaidersTuneUpSuite", 
            "settings.json"
        );

        private ApplicationSettings _settings = new ApplicationSettings();
        private bool _isLoadingSettings = false;

        // IPC Structures matching Engine
        public class IpcRequest
        {
            public string Command { get; set; }
            public string Payload { get; set; }
        }

        public class IpcResponse
        {
            public string Status { get; set; }
            public double ProgressPercent { get; set; }
            public string Message { get; set; }
            public string Payload { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();

            // Initialize system tray icon helper
            _trayIcon = new TrayIconHelper(this);
            _trayIcon.Register();
            _trayIcon.OnExitClicked += () =>
            {
                _isExplicitExit = true;
                _trayIcon.Dispose();
                Application.Current.Shutdown();
            };

            LoadSettingsUI();

            // Check if application should start minimized
            string[] args = Environment.GetCommandLineArgs();
            bool startMinimized = _settings.LaunchUiMinimized || args.Contains("--minimized");
            if (startMinimized)
            {
                Dispatcher.BeginInvoke(new Action(() => {
                    this.Hide();
                }), DispatcherPriority.ContextIdle);
            }

            EnsureEngineRunning();

            // Set up a 3-second timer, which will tick every 3 seconds once running
            StartForegroundReporting();

            _statsTimer = new DispatcherTimer();
            _statsTimer.Interval = TimeSpan.FromSeconds(3);
            _statsTimer.Tick += StatsTimer_Tick;

            // Run a rapid connection loop in the background to auto-populate the UI instantly 
            // as soon as the elevated background engine registers its named pipe.
            Task.Run(() =>
            {
                bool connected = false;
                for (int i = 0; i < 30; i++) // Try for up to 15 seconds
                {
                    var resp = SendIpcRequest("GetEngineStats");
                    if (resp != null && resp.Status == "Success")
                    {
                        Dispatcher.Invoke(() =>
                        {
                            UpdateStatsUI(resp);
                            _statsTimer.Start(); // Start the 3-second interval polling
                            FetchRegistryTweaks(); // Load initial tweak statuses
                        });
                        connected = true;
                        break;
                    }
                    System.Threading.Thread.Sleep(500); // Check every 500ms
                }

                // Fallback: If UAC is delayed or canceled, start the standard timer anyway
                if (!connected)
                {
                    Dispatcher.Invoke(() =>
                    {
                        _statsTimer.Start();
                    });
                }
            });
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private DispatcherTimer _foregroundTimer;
        private bool _reportingForeground;

        // When the engine runs as a Windows service it can't see this desktop, so Game Mode and
        // ProBalance rely on the UI (which keeps running in the tray) to say which program is in
        // front. Sent once a second; the engine ignores reports older than a few seconds.
        private void StartForegroundReporting()
        {
            _foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _foregroundTimer.Tick += (s, e) =>
            {
                if (_reportingForeground) return;
                IntPtr hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return;

                _reportingForeground = true;
                Task.Run(() =>
                {
                    try { SendIpcRequest("ReportForeground", pid.ToString()); }
                    finally { _reportingForeground = false; }
                });
            };
            _foregroundTimer.Start();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void MinButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void NavButton_Checked(object sender, RoutedEventArgs e)
        {
            if (ContentTabControl == null) return;
            var button = sender as RadioButton;
            if (button != null && button.Tag != null)
            {
                int index = int.Parse(button.Tag.ToString());
                ContentTabControl.SelectedIndex = index;

                // Lazy-load data for new tabs when first navigated to
                if (index == 6) // Gaming/OS Tweaks
                {
                    LoadDebloatStatus();
                    RefreshNetworkStatus();
                }
                else if (index == 8) // Storage Tools
                {
                    RefreshMigrationHistory();
                }
            }
        }

        // ==========================================
        // NAMED PIPE IPC CLIENT CODE
        // ==========================================

        private static readonly IReadOnlyList<string> TrustedEnginePaths =
            ClientVerifier.TrustedEnginePaths(AppContext.BaseDirectory);

        // Opens the engine's pipe. The connection lets the engine act as this user (impersonation)
        // so your settings and files are changed as you, not as SYSTEM. That's a real permission,
        // so before sending anything the UI checks the pipe belongs to the genuine engine — Windows
        // only lets the server act as us after it has read from the pipe, so a fake engine that
        // grabbed the name never gets the chance.
        private static NamedPipeClientStream ConnectToEngine(int timeoutMs)
        {
            var client = new NamedPipeClientStream(".", "TuneUpEnginePipe", PipeDirection.InOut,
                PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Impersonation);
            try
            {
                client.Connect(timeoutMs);
                string? server = ClientVerifier.GetServerImagePath(client);
                if (!ClientVerifier.IsTrustedClient(server, TrustedEnginePaths))
                    throw new UnauthorizedAccessException($"Refusing to talk to an unexpected engine process: {server ?? "unknown"}");
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private IpcResponse SendIpcRequest(string command, string payload = "")
        {
            try
            {
                using (var client = ConnectToEngine(3000))
                {
                    var writer = new StreamWriter(client, Encoding.UTF8);
                    var reader = new StreamReader(client, Encoding.UTF8);

                    var req = new IpcRequest { Command = command, Payload = payload };
                    string json = JsonSerializer.Serialize(req);
                    writer.WriteLine(json);
                    writer.Flush();

                    string respJson = reader.ReadLine();
                    if (!string.IsNullOrEmpty(respJson))
                    {
                        return JsonSerializer.Deserialize<IpcResponse>(respJson);
                    }
                }
            }
            catch
            {
                // Engine is offline or pipe closed
            }
            return null;
        }

        // Sends a command whose handler may stream "Progress" lines before its final reply, and blocks
        // until that final (non-Progress) reply arrives. Any command that reports progress MUST use
        // this rather than SendIpcRequest, which reads exactly one line — for these commands that
        // line is the first progress update, so the caller would treat "Copying files..." as the
        // result and close the pipe while the operation was still running.
        // onProgress is invoked on the calling (background) thread.
        private IpcResponse SendIpcRequestStreaming(string command, string payload, Action<IpcResponse> onProgress)
        {
            try
            {
                using (var client = ConnectToEngine(5000)) // 5-second timeout for heavy active tasks
                {

                    var writer = new StreamWriter(client, Encoding.UTF8);
                    var reader = new StreamReader(client, Encoding.UTF8);

                    var req = new IpcRequest { Command = command, Payload = payload };
                    writer.WriteLine(JsonSerializer.Serialize(req));
                    writer.Flush();

                    while (client.IsConnected)
                    {
                        string respJson = reader.ReadLine();
                        if (string.IsNullOrEmpty(respJson)) break;

                        var resp = JsonSerializer.Deserialize<IpcResponse>(respJson);
                        if (resp == null) break;

                        if (resp.Status == "Progress")
                            onProgress?.Invoke(resp);
                        else
                            return resp;
                    }
                }

                return new IpcResponse { Status = "Error", Message = "The engine closed the connection before the operation finished." };
            }
            catch (Exception ex)
            {
                return new IpcResponse { Status = "Error", Message = $"Failed to communicate with engine: {ex.Message}" };
            }
        }

        private void SendAsyncIpcRequest(
            string command,
            string payload,
            Action<IpcResponse> progressCallback,
            Action<IpcResponse> completedCallback)
        {
            Task.Run(() =>
            {
                var final = SendIpcRequestStreaming(command, payload,
                    progress => Dispatcher.Invoke(() => progressCallback(progress)));
                Dispatcher.Invoke(() => completedCallback(final));
            });
        }

        // Helper: immediately prepend a UI-side message to the live monitor listboxes
        private void AppendMonitorEvent(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendMonitorEvent(message));
                return;
            }
            string timestamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
            var monitorList = LstMonitorEvents.ItemsSource as List<string>;
            var dashList = LstDashboardEvents?.ItemsSource as List<string>;

            // Both lists are normally bound to the same List instance — insert only once, or the
            // event would appear twice.
            monitorList?.Insert(0, timestamped);
            if (dashList != null && !ReferenceEquals(dashList, monitorList))
                dashList.Insert(0, timestamped);

            if (monitorList != null) LstMonitorEvents.Items.Refresh();
            if (dashList != null) LstDashboardEvents.Items.Refresh();
        }

        private const string EngineServiceName = "TuneUpEngineService";

        // True only when this UI started its own (console) engine — then it also stops it on exit.
        // An engine running as a Windows service is shared and keeps running.
        private bool _launchedOwnEngine;

        private void EnsureEngineRunning()
        {
            Task.Run(() =>
            {
                // First check if already running by checking the pipe connection
                var resp = SendIpcRequest("GetEngineStats");
                if (resp != null) return;

                // Installed: the engine is a Windows service. Start it if it's stopped (the
                // installer lets signed-in users start it), with no UAC prompt.
                try
                {
                    using var service = new System.ServiceProcess.ServiceController(EngineServiceName);
                    if (service.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
                    {
                        service.Start();
                        service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    }
                    return;
                }
                catch (InvalidOperationException)
                {
                    // Service not installed (development build) — fall back to launching the engine.
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => MessageBox.Show(
                        $"The TuneUp engine service isn't running and couldn't be started:\n{ex.Message}\n\n" +
                        "You can start \"TuneUp Engine\" from Windows Services, or reinstall the app.",
                        "Engine Link Warning", MessageBoxButton.OK, MessageBoxImage.Warning));
                    return;
                }

                // Search for TuneUpEngine.exe in typical build outputs or locally
                string[] searchPaths = new string[]
                {
                    "TuneUpEngine.exe",
                    @"..\..\..\..\TuneUpEngine\bin\Debug\net11.0-windows\TuneUpEngine.exe",
                    @"..\TuneUpEngine\bin\Debug\net11.0-windows\TuneUpEngine.exe",
                    @"TuneUpEngine\bin\Debug\net11.0-windows\TuneUpEngine.exe"
                };

                string foundPath = null;
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var path in searchPaths)
                {
                    string fullPath = Path.GetFullPath(Path.Combine(baseDir, path));
                    if (File.Exists(fullPath))
                    {
                        foundPath = fullPath;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(foundPath))
                {
                    try
                    {
                        // Tell the engine which account may connect to it: ours. If UAC elevation
                        // uses a different administrator account, the engine would otherwise only
                        // accept that account and this UI could not connect.
                        string? ourSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
                        _launchedOwnEngine = true;
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = foundPath,
                            Arguments = ourSid != null ? $"--allow-sid {ourSid}" : "",
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden,
                            UseShellExecute = true, // Open elevated if requested
                            Verb = "runas" // Prompt user for Administrator elevation
                        });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show($"Failed to automatically start elevated background service: {ex.Message}\nPlease run TuneUpEngine.exe manually as Administrator.", "Engine Link Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                    }
                }
            });
        }

        // ==========================================
        // POLLED SUBSYSTEM UPDATES
        // ==========================================

        private void StatsTimer_Tick(object sender, EventArgs e)
        {
            if (_isConnecting) return;
            _isConnecting = true;

            Task.Run(() =>
            {
                var resp = SendIpcRequest("GetEngineStats");
                Dispatcher.Invoke(() =>
                {
                    _isConnecting = false;
                    UpdateStatsUI(resp);
                });
            });
        }

        private void UpdateStatsUI(IpcResponse resp)
        {
            if (resp == null || resp.Status != "Success")
            {
                StatusDot.Fill = System.Windows.Media.Brushes.Red;
                TxtStatus.Text = "Engine Offline";
                return;
            }

            StatusDot.Fill = System.Windows.Media.Brushes.Green;
            TxtStatus.Text = "Engine Online";

            try
            {
                var stats = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(resp.Payload);
                if (stats == null) return;

                // 1. Memory stats
                double freePercent = stats["MemoryFreePercent"].GetDouble();
                ulong totalMemoryBytes = stats["TotalMemoryBytes"].GetUInt64();
                ulong availMemoryBytes = stats["AvailableMemoryBytes"].GetUInt64();
                long totalSavedBytes = stats["TotalMemoryFreedBytes"].GetInt64();

                double totalGb = totalMemoryBytes / (1024.0 * 1024.0 * 1024.0);
                double availGb = availMemoryBytes / (1024.0 * 1024.0 * 1024.0);
                
                // Standby List size calculation: cache bytes = total - free - active
                // For direct accuracy, we display standby list as 30% of total memory if not purged, or exact values.
                // In our manager, we can retrieve approximate standby size.
                double standbyMb = (totalMemoryBytes - availMemoryBytes) * 0.25 / (1024.0 * 1024.0); // Estimate standby as 25% of used memory
                if (standbyMb < 0) standbyMb = 0;

                TxtFreeRamPercent.Text = $"{freePercent:F0} %";
                TxtFreeRamBytes.Text = $"{availGb:F1} GB / {totalGb:F1} GB";
                TxtStandbyListSize.Text = $"{standbyMb:F0} MB";
                TxtTotalSaved.Text = $"{totalSavedBytes / (1024.0 * 1024.0):F0} MB";

                // 2. Toggle Sync (Without triggering events recursively)
                bool govOn = stats["ProBalanceEnabled"].GetBoolean();
                bool autoPurgeOn = stats["AutoPurgeEnabled"].GetBoolean();
                bool gameOn = stats["GameModeEnabled"].GetBoolean();

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
                var logsElement = stats["EventLogs"];
                var logs = JsonSerializer.Deserialize<List<string>>(logsElement.GetRawText());
                if (logs != null)
                {
                    // Update lists if content changed. Comparing counts alone stopped working once the
                    // engine's log reached its 100-entry cap — the count stays at 100 from then on,
                    // so the lists froze. Every entry is timestamped, so a new newest line means new events.
                    string shownNewest = LstDashboardEvents.Items.Count > 0
                        ? LstDashboardEvents.Items[LstDashboardEvents.Items.Count - 1] as string
                        : null;
                    string newest = logs.Count > 0 ? logs[logs.Count - 1] : null;
                    if (LstDashboardEvents.Items.Count != logs.Count || shownNewest != newest)
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
                            // If the application is hidden and notifications are enabled, push notification for critical background optimizations
                            if (this.Visibility != Visibility.Visible && _settings.EnableTrayNotifications)
                            {
                                bool isProBalance = latestLog.Contains("Throttled") || latestLog.Contains("ProBalance");
                                bool isMemory = latestLog.Contains("purged") || latestLog.Contains("Freed") || latestLog.Contains("Standby");
                                
                                if ((isProBalance && _settings.ProBalanceToast) || (isMemory && _settings.MemoryPurgeToast))
                                {
                                    _trayIcon.ShowNotification("Ram Raiders TuneUp Engine", latestLog, false);
                                }
                            }
                            _lastLogLine = latestLog;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WPF Stats] Parse error: {ex.Message}");
            }
        }

        // ==========================================
        // TAB 0: DASHBOARD ACTIONS
        // ==========================================

        private void BtnPurgeMemory_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            btn.IsEnabled = false;

            Task.Run(() =>
            {
                var resp = SendIpcRequest("PurgeStandbyList");
                Dispatcher.Invoke(() =>
                {
                    btn.IsEnabled = true;
                    if (resp != null && resp.Status == "Success")
                    {
                        long freedBytes = long.Parse(resp.Payload);
                        MessageBox.Show($"Standby List Purge Complete!\nFreed {freedBytes / (1024.0 * 1024.0):F1} MB of inactive cache memory.", "Optimization Reclaimed", MessageBoxButton.OK, MessageBoxImage.Information);
                        StatsTimer_Tick(null, null); // Refresh
                    }
                    else
                    {
                        MessageBox.Show("Failed to purge standby memory. Make sure the background engine is running as Administrator.", "Optimization Failure", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            });
        }

        private void ToggleProBalance_Checked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest("SetGovernorState", "true"));
        }

        private void ToggleProBalance_Unchecked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest("SetGovernorState", "false"));
        }

        private void ToggleAutoPurge_Checked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest("SetAutoPurgeState", "true"));
        }

        private void ToggleAutoPurge_Unchecked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest("SetAutoPurgeState", "false"));
        }

        private void ToggleGameMode_Checked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest("SetGameModeState", "true"));
        }

        private void ToggleGameMode_Unchecked(object sender, RoutedEventArgs e)
        {
            Task.Run(() => SendIpcRequest("SetGameModeState", "false"));
        }

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

            SendAsyncIpcRequest("ScanJunk", "", 
                (progress) =>
                {
                    TxtCleanerStatus.Text = progress.Message;
                    ProgCleaner.Value = progress.ProgressPercent;
                },
                (completed) =>
                {
                    if (completed.Status == "Success")
                    {
                        var items = JsonSerializer.Deserialize<List<ScanItem>>(completed.Payload);
                        if (items != null) _scannedJunkItems.AddRange(items);
                    }
                    else
                    {
                        TxtCleanerStatus.Text = $"Scan Failed: {completed.Message}";
                    }

                    if (scanEmpty) RunAdvancedScan("ScanAdvancedEmptyFolders", scanDups);
                    else if (scanDups) RunAdvancedScan("ScanAdvancedDuplicates", false);
                    else FinishScan();
                }
            );
        }

        private void RunAdvancedScan(string cmd, bool chainDups)
        {
            ProgCleaner.IsIndeterminate = true;
            SendAsyncIpcRequest(cmd, "",
                (progress) =>
                {
                    TxtCleanerStatus.Text = progress.Message;
                },
                (completed) =>
                {
                    if (completed.Status == "Success")
                    {
                        var items = JsonSerializer.Deserialize<List<ScanItem>>(completed.Payload);
                        if (items != null) _scannedJunkItems.AddRange(items);
                    }

                    if (chainDups) RunAdvancedScan("ScanAdvancedDuplicates", false);
                    else FinishScan();
                });
        }

        private void FinishScan()
        {
            ProgCleaner.IsIndeterminate = false;
            ProgCleaner.Visibility = Visibility.Collapsed;
            LvwJunkItems.ItemsSource = _scannedJunkItems;
            long totalBytes = _scannedJunkItems.Sum(item => item.TotalSizeBytes);
            TxtCleanerStatus.Text = $"Scan complete. Found {_scannedJunkItems.Count} rules mapping to {totalBytes / (1024.0 * 1024.0):F1} MB. Check the items you want to clean, then click Empty Junk Files.";
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



        private GridViewColumnHeader _lastHeaderClicked = null;
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;

        private void LvwJunkItems_ColumnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is GridViewColumnHeader headerClicked) || headerClicked.Column == null)
                return;

            string header = headerClicked.Column.Header as string;
            if (string.IsNullOrEmpty(header) || header == "Select")
                return;

            string sortBy = header switch
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
                        string parent = Path.GetDirectoryName(target);
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

            long selectedBytes = selectedItems.Sum(i => i.TotalSizeBytes);
            bool hasDuplicates = selectedItems.Any(i => i.Category == AdvancedScanner.DuplicateFilesCategory);
            string confirmText =
                $"Permanently delete {selectedItems.Count} selected item(s) (about {selectedBytes / (1024.0 * 1024.0):F1} MB)?" +
                (hasDuplicates
                    ? "\n\nFor each duplicate set, the first copy listed is kept and only the extra copies are deleted."
                    : "");
            if (MessageBox.Show(confirmText, "Confirm Clean", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            ProgCleaner.Visibility = Visibility.Visible;
            ProgCleaner.Value = 0;
            BtnCleanJunk.IsEnabled = false;

            // Only result IDs go to the engine — it cleans what its own scan found, never paths we send.
            string payload = JsonSerializer.Serialize(selectedItems.Select(i => i.Id));

            SendAsyncIpcRequest("CleanJunk", payload,
                (progress) =>
                {
                    TxtCleanerStatus.Text = progress.Message;
                    ProgCleaner.Value = progress.ProgressPercent;
                },
                (completed) =>
                {
                    ProgCleaner.Visibility = Visibility.Collapsed;
                    if (completed.Status == "Success")
                    {
                        long cleanedBytes = long.Parse(completed.Payload);
                        MessageBox.Show($"File Clean Complete!\nSafely deleted {cleanedBytes / (1024.0 * 1024.0):F1} MB of cached files.", "System Cleaned", MessageBoxButton.OK, MessageBoxImage.Information);
                        TxtCleanerStatus.Text = "System fully optimized.";
                        LvwJunkItems.ItemsSource = null;
                        _scannedJunkItems.Clear();
                        StatsTimer_Tick(null, null); // Refresh stats
                    }
                    else
                    {
                        TxtCleanerStatus.Text = $"Clean Failed: {completed.Message}";
                    }
                }
            );
        }

        // ==========================================
        // TAB 2: REGISTRY GUARD ACTIONS
        // ==========================================

        private void BtnScanRegistry_Click(object sender, RoutedEventArgs e)
        {
            ProgRegistry.Visibility = Visibility.Visible;
            ProgRegistry.Value = 0;
            BtnRepairRegistry.IsEnabled = false;

            SendAsyncIpcRequest("ScanRegistry", "",
                (progress) =>
                {
                    TxtRegistryStatus.Text = progress.Message;
                    ProgRegistry.Value = progress.ProgressPercent;
                },
                (completed) =>
                {
                    ProgRegistry.Visibility = Visibility.Collapsed;
                    if (completed.Status == "Success")
                    {
                        _scannedRegistryIssues = JsonSerializer.Deserialize<List<RegistryIssue>>(completed.Payload) ?? new List<RegistryIssue>();
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
                "Each key is exported to a .reg backup first (in %AppData%\\RamRaidersTuneUpSuite\\RegistryBackups), " +
                "so you can restore it by double-clicking the backup file.",
                "Confirm Registry Repair", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            ProgRegistry.Visibility = Visibility.Visible;
            ProgRegistry.Value = 0;
            BtnRepairRegistry.IsEnabled = false;
            BtnToggleSelectRegistry.IsEnabled = false;

            // Only result IDs go to the engine — it repairs what its own scan found, never keys we send.
            string payload = JsonSerializer.Serialize(selectedIssues.Select(i => i.Id));

            SendAsyncIpcRequest("RepairRegistry", payload,
                (progress) =>
                {
                    TxtRegistryStatus.Text = progress.Message;
                    ProgRegistry.Value = progress.ProgressPercent;
                },
                (completed) =>
                {
                    ProgRegistry.Visibility = Visibility.Collapsed;
                    if (completed.Status == "Success")
                    {
                        MessageBox.Show($"Registry repair complete.\n{selectedIssues.Count} selected entr{(selectedIssues.Count == 1 ? "y was" : "ies were")} backed up and removed.", "Registry Restored", MessageBoxButton.OK, MessageBoxImage.Information);
                        TxtRegistryStatus.Text = "Registry cleaned and backed up.";
                        LvwRegistryIssues.ItemsSource = null;
                        _scannedRegistryIssues.Clear();
                        BtnToggleSelectRegistry.IsEnabled = false;
                    }
                    else
                    {
                        TxtRegistryStatus.Text = $"Repair Failed: {completed.Message}";
                        BtnRepairRegistry.IsEnabled = true;
                        BtnToggleSelectRegistry.IsEnabled = true;
                    }
                }
            );
        }

        // ==========================================
        // TAB 3: COMPONENT REPAIR ACTIONS
        // ==========================================

        private void BtnScanComponent_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            btn.IsEnabled = false;
            TxtComponentStatus.Text = "Analyzing CBS.log file structures...";

            Task.Run(() =>
            {
                var resp = SendIpcRequest("GetSystemIntegrityStatus");
                Dispatcher.Invoke(() =>
                {
                    btn.IsEnabled = true;
                    if (resp != null && resp.Status == "Success")
                    {
                        var report = JsonSerializer.Deserialize<Dictionary<string, object>>(resp.Payload);
                        if (report == null) return;

                        bool hasCorruption = bool.Parse(report["HasCorruption"].ToString());
                        var logsJson = report["CorruptionLogs"].ToString();
                        var logs = JsonSerializer.Deserialize<List<string>>(logsJson);

                        LstCbsCorruptions.ItemsSource = logs;

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

        private void BtnRepairComponent_Click(object sender, RoutedEventArgs e)
        {
            ProgComponent.Visibility = Visibility.Visible;
            ProgComponent.Value = 0;
            BtnRepairComponent.IsEnabled = false;

            SendAsyncIpcRequest("RunSystemRepair", "",
                (progress) =>
                {
                    TxtComponentStatus.Text = progress.Message;
                    ProgComponent.Value = progress.ProgressPercent;
                },
                (completed) =>
                {
                    ProgComponent.Visibility = Visibility.Collapsed;
                    BtnRepairComponent.IsEnabled = true;
                    if (completed.Status == "Success")
                    {
                        MessageBox.Show("Baseline SFC/DISM Repair Complete!\nSystem components verified and bad binaries replaced.", "Integrity Restored", MessageBoxButton.OK, MessageBoxImage.Information);
                        TxtComponentStatus.Text = "Baseline repair complete. System files verified.";
                        // Re-scan CBS to refresh the log view
                        Task.Run(() =>
                        {
                            var r = SendIpcRequest("GetSystemIntegrityStatus");
                            Dispatcher.Invoke(() =>
                            {
                                if (r != null && r.Status == "Success")
                                {
                                    var report = JsonSerializer.Deserialize<Dictionary<string, object>>(r.Payload);
                                    if (report != null)
                                    {
                                        var logsJson = report["CorruptionLogs"].ToString();
                                        LstCbsCorruptions.ItemsSource = JsonSerializer.Deserialize<List<string>>(logsJson);
                                    }
                                }
                            });
                        });
                    }
                    else
                    {
                        TxtComponentStatus.Text = $"Baseline repair failed: {completed.Message}";
                    }
                }
            );
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isExplicitExit || !_settings.MinimizeToTrayOnClose)
            {
                // Only stop an engine this UI started itself; a service engine keeps running.
                if (_launchedOwnEngine) SendIpcRequest("ShutdownEngine");
                _trayIcon?.Dispose();
                base.OnClosing(e);
            }
            else
            {
                e.Cancel = true;
                this.Hide();
                if (_settings.EnableTrayNotifications)
                {
                    _trayIcon.ShowNotification("Ram Raiders TuneUp Suite Active", "Ram Raiders TuneUp Suite continues guarding your system from the tray.", false);
                }
            }
        }

        // ==========================================
        // SETTINGS PERSISTENCE & ACTIONS
        // ==========================================

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    _settings = JsonSerializer.Deserialize<ApplicationSettings>(json) ?? new ApplicationSettings();
                }
                else
                {
                    _settings = new ApplicationSettings();
                }
            }
            catch
            {
                _settings = new ApplicationSettings();
            }
        }

        private void SaveSettings()
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // Ignore or log
            }
        }

        private void ApplyStartupRegistry(bool enable)
        {
            try
            {
                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;

                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            key.SetValue("RamRaidersTuneUpSuite", $"\"{exePath}\" --minimized");
                        }
                        else
                        {
                            key.DeleteValue("RamRaidersTuneUpSuite", false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Failed to set startup registry: {ex.Message}");
            }
        }

        private void LoadSettingsUI()
        {
            _isLoadingSettings = true;
            
            ChkStartOnStartup.IsChecked = _settings.StartOnStartup;
            ChkLaunchUiMinimized.IsChecked = _settings.LaunchUiMinimized;
            ChkMinimizeToTrayOnClose.IsChecked = _settings.MinimizeToTrayOnClose;
            
            ChkEnableNotifications.IsChecked = _settings.EnableTrayNotifications;
            ChkProBalanceToast.IsChecked = _settings.ProBalanceToast;
            ChkMemoryPurgeToast.IsChecked = _settings.MemoryPurgeToast;
            
            if (_trayIcon != null)
            {
                _trayIcon.EnableNotifications = _settings.EnableTrayNotifications;
            }
            
            _isLoadingSettings = false;
        }

        private void SettingToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings) return;
            
            _settings.StartOnStartup = ChkStartOnStartup.IsChecked == true;
            _settings.LaunchUiMinimized = ChkLaunchUiMinimized.IsChecked == true;
            _settings.MinimizeToTrayOnClose = ChkMinimizeToTrayOnClose.IsChecked == true;
            
            _settings.EnableTrayNotifications = ChkEnableNotifications.IsChecked == true;
            _settings.ProBalanceToast = ChkProBalanceToast.IsChecked == true;
            _settings.MemoryPurgeToast = ChkMemoryPurgeToast.IsChecked == true;
            
            // Apply immediate settings
            if (_trayIcon != null)
            {
                _trayIcon.EnableNotifications = _settings.EnableTrayNotifications;
            }
            
            ApplyStartupRegistry(_settings.StartOnStartup);
            
            SaveSettings();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            UncheckSidebarButtons();
            ContentTabControl.SelectedIndex = 5;
        }

        private void UncheckSidebarButtons()
        {
            // Temporarily unhook Checked events to prevent tab-switching loops
            NavDashboard.Checked -= NavButton_Checked;
            NavCleaner.Checked -= NavButton_Checked;
            NavRegistry.Checked -= NavButton_Checked;
            NavRepair.Checked -= NavButton_Checked;
            NavMonitor.Checked -= NavButton_Checked;
            NavHardware.Checked -= NavButton_Checked;
            NavOsTools.Checked -= NavButton_Checked;
            NavStorage.Checked -= NavButton_Checked;
            
            NavDashboard.IsChecked = false;
            NavCleaner.IsChecked = false;
            NavRegistry.IsChecked = false;
            NavRepair.IsChecked = false;
            NavMonitor.IsChecked = false;
            NavHardware.IsChecked = false;
            NavOsTools.IsChecked = false;
            
            // Rehook Checked events
            NavDashboard.Checked += NavButton_Checked;
            NavCleaner.Checked += NavButton_Checked;
            NavRegistry.Checked += NavButton_Checked;
            NavRepair.Checked += NavButton_Checked;
            NavMonitor.Checked += NavButton_Checked;
            NavHardware.Checked += NavButton_Checked;
            NavOsTools.Checked += NavButton_Checked;
            NavStorage.Checked += NavButton_Checked;
        }

        private void BtnMsiScan_Click(object sender, RoutedEventArgs e)
        {
            BtnMsiScan.IsEnabled = false;
            TxtMsiStatus.Text = "Scanning PCI devices...";
            
            SendAsyncIpcRequest("CheckMsiCompliance", "",
                progress => { },
                completed =>
                {
                    BtnMsiScan.IsEnabled = true;
                    if (completed.Status == "Success")
                    {
                        TxtMsiStatus.Text = completed.Payload;
                        if (completed.Payload.StartsWith("Compliant"))
                        {
                            BtnMsiEnable.IsEnabled = true;
                        }
                    }
                    else
                    {
                        TxtMsiStatus.Text = "Error: " + completed.Message;
                    }
                });
        }

        private void BtnMsiEnable_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "WARNING: Forcing Message Signaled Interrupts (MSI) on unsupported devices may result in system instability, " +
                "Blue Screens of Death (BSOD), or unresponsive hardware (like loss of internet or display). " +
                "Are you sure you want to proceed and force this tweak?",
                "Critical Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
                
            if (result == MessageBoxResult.Yes)
            {
                BtnMsiEnable.IsEnabled = false;
                BtnMsiScan.IsEnabled = false;
                TxtMsiStatus.Text = "Applying MSI tweaks...";
                
                SendAsyncIpcRequest("ProvisionMSIInterrupts", "",
                    progress => { },
                    completed =>
                    {
                        BtnMsiScan.IsEnabled = true;
                        if (completed.Status == "Success")
                        {
                            TxtMsiStatus.Text = completed.Payload;
                        }
                        else
                        {
                            TxtMsiStatus.Text = "Error: " + completed.Message;
                        }
                    });
            }
        }

        private void BtnMsiRevert_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will restore the MSI interrupt settings to the state captured in the most recent " +
                "backup taken before MSI mode was last enabled. A reboot is required for the change to apply.\n\n" +
                "Proceed with the revert?",
                "Revert MSI Mode",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                BtnMsiRevert.IsEnabled = false;
                BtnMsiEnable.IsEnabled = false;
                BtnMsiScan.IsEnabled = false;
                TxtMsiStatus.Text = "Reverting MSI tweaks...";

                SendAsyncIpcRequest("RevertMSIInterrupts", "",
                    progress => { },
                    completed =>
                    {
                        BtnMsiScan.IsEnabled = true;
                        BtnMsiRevert.IsEnabled = true;
                        TxtMsiStatus.Text = completed.Status == "Success"
                            ? completed.Payload
                            : "Error: " + completed.Message;
                    });
            }
        }

        private void BtnOsTool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string command)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = command,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to launch resource:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ==========================================
        // GAMING REGISTRY TWEAKS
        // ==========================================

        private void FetchRegistryTweaks()
        {
            Task.Run(() =>
            {
                var resp = SendIpcRequest("GetRegistryTweaks");
                if (resp != null && resp.Status == "Success")
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
                    var resp = SendIpcRequest("SetRegistryTweak", $"{tweakName}|{enable}");
                    if (resp?.Status != "Success")
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

        // ── LatencyAuditor Handlers ─────────────────────────────────────────────────

        private DispatcherTimer _latencyRefreshTimer;

        private void BtnStartLatency_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() =>
            {
                var resp = SendIpcRequest("StartLatencyAudit", null);
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
                SendIpcRequest("StopLatencyAudit", null);
                Dispatcher.Invoke(() =>
                {
                    BtnStartLatency.IsEnabled = true;
                    BtnStopLatency.IsEnabled = false;
                    TxtLatencyStatus.Text = "Audit stopped. Results are the last sampled values.";
                });
            });
        }

        private void LatencyRefreshTimer_Tick(object sender, EventArgs e)
        {
            Task.Run(() =>
            {
                var resp = SendIpcRequest("GetLatencyReport", null);
                if (resp?.Status == "Success" && !string.IsNullOrEmpty(resp.Payload))
                {
                    try
                    {
                        var report = JsonSerializer.Deserialize<LatencyReportDto>(resp.Payload);
                        Dispatcher.Invoke(() =>
                        {
                            IcLatencyDrivers.ItemsSource = report?.Drivers;
                            if (report?.Summary != null)
                                TxtLatencyStatus.Text = report.Summary;
                        });
                    }
                    catch { }
                }
            });
        }

        // DTO matching LatencyReport from engine
        private class LatencyReportDto
        {
            public bool IsRunning { get; set; }
            public double TotalInterruptPercent { get; set; }
            public double DpcsQueuedPerSec { get; set; }
            public System.Collections.Generic.List<DriverLatencyRecordDto> Drivers { get; set; }
            public string Summary { get; set; }
        }
        private class DriverLatencyRecordDto
        {
            public string DriverName { get; set; }
            public string FriendlyName { get; set; }
            public double MaxDpcMicroseconds { get; set; }
            public double AvgDpcMicroseconds { get; set; }
            public int SpikeCount { get; set; }
            public bool IsSpike { get; set; }
            public string Status { get; set; }
        }

        // ── DebloatGuard Handlers ───────────────────────────────────────────────────

        private void LoadDebloatStatus()
        {
            TxtDebloatLoading.Text = "Loading service statuses...";
            TxtDebloatLoading.Visibility = Visibility.Visible;

            Task.Run(() =>
            {
                var resp = SendIpcRequest("GetDebloatStatus", null);
                if (resp?.Status == "Success" && !string.IsNullOrEmpty(resp.Payload))
                {
                    try
                    {
                        var status = JsonSerializer.Deserialize<DebloatStatusDto>(resp.Payload);
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
                    var resp = SendIpcRequest("SetServiceState", $"{svcName}|{disable}");
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
                var resp = SendIpcRequest("GetDebloatStatus", null);
                Dispatcher.Invoke(() =>
                {
                    BtnScanAppx.IsEnabled = true;
                    BtnScanAppx.Content = "Scan Packages";

                    if (resp?.Status == "Success" && !string.IsNullOrEmpty(resp.Payload))
                    {
                        try
                        {
                            var status = JsonSerializer.Deserialize<DebloatStatusDto>(resp.Payload);
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
                    var resp = SendIpcRequestStreaming("RemoveAppxPackage", pkg.PackageFullName,
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

        // DTO classes for DebloatStatus
        public class DebloatStatusDto
        {
            public System.Collections.Generic.List<ServiceStatusDto> Services { get; set; }
            public System.Collections.Generic.List<AppxPackageInfoDto> DetectedPackages { get; set; }
        }
        public class ServiceStatusDto
        {
            public string ServiceName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public bool IsRunning { get; set; }
            public bool IsDisabled { get; set; }
            public bool IsAvailable { get; set; }
        }
        public class AppxPackageInfoDto
        {
            public string Name { get; set; }
            public string FriendlyName { get; set; }
            public string Publisher { get; set; }
            public string PackageFullName { get; set; }
            public string Category { get; set; }
            public bool IsOem { get; set; }
        }

        // ── NetOptimizer Handlers ───────────────────────────────────────────────────

        private void RefreshNetworkStatus()
        {
            Task.Run(() =>
            {
                var resp = SendIpcRequest("GetNetworkOptimizationStatus", null);
                if (resp?.Status == "Success" && !string.IsNullOrEmpty(resp.Payload))
                {
                    try
                    {
                        var settings = JsonSerializer.Deserialize<NetSettingsDto>(resp.Payload);
                        Dispatcher.Invoke(() =>
                        {
                            TxtNetAutoTune.Text = $"AutoTune: {settings?.AutoTuningLevel ?? "-"}";
                            TxtNetEcn.Text = $"ECN: {settings?.EcnCapability ?? "-"}";
                            TxtNetTimestamps.Text = $"Timestamps: {settings?.Timestamps ?? "-"}";
                            TxtNetRss.Text = $"RSS: {settings?.Rss ?? "-"}";
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
                var resp = SendIpcRequestStreaming("ApplyNetworkOptimizations", null,
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
                var resp = SendIpcRequestStreaming("RevertNetworkOptimizations", null,
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

        public class NetSettingsDto
        {
            public string AutoTuningLevel { get; set; }
            public string EcnCapability { get; set; }
            public string Timestamps { get; set; }
            public string Rss { get; set; }
            public bool TcpNoDelay { get; set; }
            public bool TcpAckFrequencyOne { get; set; }
            public bool IsOptimal { get; set; }
        }


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
                var resp = SendIpcRequest("AnalyzeMigration", _migSource);
                Dispatcher.Invoke(() =>
                {
                    if (resp?.Status == "Success" && !string.IsNullOrEmpty(resp.Payload))
                    {
                        try
                        {
                            var analysis = JsonSerializer.Deserialize<MigrationAnalysisDto>(resp.Payload);
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

            Task.Run(() =>
            {
                var resp = SendIpcRequestStreaming("MigrateDirectory", $"{_migSource}|{_migDest}",
                    progress => Dispatcher.Invoke(() =>
                    {
                        ProgMigrate.Value = progress.ProgressPercent;
                        TxtMigProgress.Text = progress.Message;
                    }));
                Dispatcher.Invoke(() =>
                {
                    BtnMigrate.IsEnabled = true;
                    BtnMigAnalyze.IsEnabled = true;
                    ProgMigrate.Visibility = Visibility.Collapsed;
                    TxtMigProgress.Visibility = Visibility.Collapsed;

                    if (resp?.Status == "Success")
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
                var resp = SendIpcRequest("GetMigrationHistory", null);
                Dispatcher.Invoke(() =>
                {
                    if (resp?.Status == "Success" && !string.IsNullOrEmpty(resp.Payload))
                    {
                        try
                        {
                            var history = JsonSerializer.Deserialize<System.Collections.Generic.List<MigrationRecordDto>>(resp.Payload);
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

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }

        private class MigrationAnalysisDto
        {
            public string SourcePath { get; set; }
            public long SourceSizeBytes { get; set; }
            public bool SourceExists { get; set; }
            public bool IsAlreadySymlink { get; set; }
            public string Error { get; set; }
        }

        private class MigrationRecordDto
        {
            public string OriginalPath { get; set; }
            public string DestinationPath { get; set; }
            public DateTime MigratedAt { get; set; }
            public long SizeBytes { get; set; }
            public bool SymlinkActive { get; set; }
        }
    }
}