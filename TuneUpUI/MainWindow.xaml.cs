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
        // Window shell: startup, engine connection, foreground reporting, navigation and closing.
        // Each tab's code lives in its own MainWindow.<Tab>.cs partial file.
        private DispatcherTimer _statsTimer;
        private bool _isConnecting = false;
        
        // System Tray variables
        private TrayIconHelper _trayIcon;
        private string _lastLogLine = "";
        private bool _isExplicitExit = false;


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
                    var resp = SendIpcRequest(IpcCommands.GetEngineStats);
                    if (resp != null && resp.Status == IpcStatus.Success)
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
                    try { SendIpcRequest(IpcCommands.ReportForeground, pid.ToString()); }
                    finally { _reportingForeground = false; }
                });
            };
            _foregroundTimer.Start();
        }

        // Open as large as the screen comfortably allows (up to 1280x860), so the tabs show their
        // content without scrolling on bigger monitors; the window can be resized or maximized from there.
        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            Rect work = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, Math.Min(1280, work.Width * 0.9));
            Height = Math.Max(MinHeight, Math.Min(860, work.Height * 0.9));
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
        }

        // A borderless window that is maximized extends past the screen edges by the resize border;
        // pad the content by that much so nothing is cut off.
        private void Window_StateChanged(object? sender, EventArgs e)
        {
            bool maximized = WindowState == WindowState.Maximized;
            Thickness frame = SystemParameters.WindowResizeBorderThickness;
            const double pad = 4; // Windows' padded border, which WindowResizeBorderThickness leaves out
            RootGrid.Margin = maximized
                ? new Thickness(frame.Left + pad, frame.Top + pad, frame.Right + pad, frame.Bottom + pad)
                : new Thickness(0);
            BorderThickness = new Thickness(maximized ? 0 : 1);
            BtnMaximize.Content = maximized ? "" : "";
            BtnMaximize.ToolTip = maximized ? "Restore" : "Maximize";
        }

        private void MinButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaxButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
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

        // All engine communication goes through EngineClient (TuneUpEngine assembly): it checks
        // the engine is genuine, sends typed requests, streams progress and supports cancelling.
        private readonly EngineClient _engine = EngineClient.ForThisApplication();

        // One request, one reply; null if the engine can't be reached. Not for commands that report
        // progress — those use SendIpcRequestStreaming.
        private IpcResponse? SendIpcRequest(string command, string? payload = "") => _engine.Send(command, payload);

        // Streams progress (onProgress runs on the calling, background, thread) and returns the
        // final reply. Cancelling ct asks the engine to stop; the final reply then says so.
        private IpcResponse SendIpcRequestStreaming(string command, string? payload, Action<IpcResponse> onProgress,
            System.Threading.CancellationToken ct = default) =>
            _engine.SendStreaming(command, payload, onProgress, ct);

        private void SendAsyncIpcRequest(
            string command,
            string? payload,
            Action<IpcResponse> progressCallback,
            Action<IpcResponse> completedCallback,
            System.Threading.CancellationToken ct = default)
        {
            Task.Run(() =>
            {
                var final = SendIpcRequestStreaming(command, payload,
                    progress => Dispatcher.Invoke(() => progressCallback(progress)), ct);
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
                var resp = SendIpcRequest(IpcCommands.GetEngineStats);
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

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isExplicitExit || !_settings.MinimizeToTrayOnClose)
            {
                // Only stop an engine this UI started itself; a service engine keeps running.
                if (_launchedOwnEngine) SendIpcRequest(IpcCommands.ShutdownEngine);
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

    }
}
