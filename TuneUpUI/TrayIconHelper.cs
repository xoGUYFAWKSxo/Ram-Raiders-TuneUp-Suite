using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TuneUpUI
{
    public class TrayIconHelper : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

        private const int NIM_ADD = 0;
        private const int NIM_MODIFY = 1;
        private const int NIM_DELETE = 2;

        private const int NIF_MESSAGE = 1;
        private const int NIF_ICON = 2;
        private const int NIF_TIP = 4;
        private const int NIF_INFO = 16;

        private const int NIIF_NONE = 0;
        private const int NIIF_INFO = 1;
        private const int NIIF_WARNING = 2;

        private const int WM_USER = 0x0400;
        private const int TRAY_MESSAGE = WM_USER + 999;

        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONDOWN = 0x0204;

        private readonly Window _window;
        private IntPtr _hWnd;
        private bool _isRegistered = false;
        private NOTIFYICONDATA _nid;

        public event Action? OnExitClicked;

        public TrayIconHelper(Window window)
        {
            _window = window;
        }

        public void Register()
        {
            if (_isRegistered) return;

            var helper = new WindowInteropHelper(_window);
            _hWnd = helper.Handle;

            if (_hWnd == IntPtr.Zero)
            {
                // If handle is not ready, register when window is initialized
                _window.SourceInitialized += (s, e) => Register();
                return;
            }

            // Extract built-in Shield / security icon for a premium tuneup aesthetic
            IntPtr hIcon = System.Drawing.SystemIcons.Shield.Handle;

            _nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                hWnd = _hWnd,
                uID = 100,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = TRAY_MESSAGE,
                hIcon = hIcon,
                szTip = "Galactic Tune-Up"
            };

            _isRegistered = Shell_NotifyIcon(NIM_ADD, ref _nid);

            if (_isRegistered)
            {
                var source = HwndSource.FromHwnd(_hWnd);
                source?.AddHook(HwndMessageHook);
            }
        }

        public bool EnableNotifications { get; set; } = true;

        public void ShowNotification(string title, string text, bool isWarning = false)
        {
            if (!_isRegistered || !EnableNotifications) return;

            _nid.uFlags |= NIF_INFO;
            _nid.szInfoTitle = title;
            _nid.szInfo = text;
            _nid.dwInfoFlags = isWarning ? NIIF_WARNING : NIIF_INFO;

            Shell_NotifyIcon(NIM_MODIFY, ref _nid);
        }

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == TRAY_MESSAGE)
            {
                int clickEvent = lParam.ToInt32();
                if (clickEvent == WM_LBUTTONDOWN || clickEvent == WM_LBUTTONDBLCLK)
                {
                    RestoreWindow();
                    handled = true;
                }
                else if (clickEvent == WM_RBUTTONDOWN)
                {
                    ShowTrayContextMenu();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void RestoreWindow()
        {
            _window.Show();
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }
            _window.Activate();
        }

        private void ShowTrayContextMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu();
            
            var openItem = new System.Windows.Controls.MenuItem { Header = "Open Dashboard" };
            openItem.Click += (s, e) => RestoreWindow();
            
            var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit Application" };
            exitItem.Click += (s, e) => OnExitClicked?.Invoke();

            menu.Items.Add(openItem);
            menu.Items.Add(exitItem);

            menu.IsOpen = true;
        }

        public void Dispose()
        {
            if (_isRegistered)
            {
                Shell_NotifyIcon(NIM_DELETE, ref _nid);
                _isRegistered = false;

                var source = HwndSource.FromHwnd(_hWnd);
                source?.RemoveHook(HwndMessageHook);
            }
        }
    }
}
