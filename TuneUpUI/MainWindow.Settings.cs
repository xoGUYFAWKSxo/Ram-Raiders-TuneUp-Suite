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
    // Settings: persisted UI preferences, start-with-Windows and sidebar state.
    public partial class MainWindow
    {
        // Settings variables. Everything starts off for a new user: each one is the user's choice to
        // turn on, not something the app opts them into. (Saved settings are kept as they are.)
        public class ApplicationSettings
        {
            public bool StartOnStartup { get; set; } = false;
            public bool LaunchUiMinimized { get; set; } = false;
            public bool MinimizeToTrayOnClose { get; set; } = false;
            public bool EnableTrayNotifications { get; set; } = false;
            public bool ProBalanceToast { get; set; } = false;
            public bool MemoryPurgeToast { get; set; } = false;
        }

        private static readonly string SettingsFilePath = Path.Combine(
            AppFolders.Root(Environment.SpecialFolder.LocalApplicationData), 
            "settings.json"
        );

        private ApplicationSettings _settings = new ApplicationSettings();
        private bool _isLoadingSettings = false;
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
                        // The entry's name before the rename (2026-10-16); it also points at the old folder.
                        key.DeleteValue(AppFolders.LegacyName, false);
                        if (enable)
                        {
                            key.SetValue(AppFolders.Name, $"\"{exePath}\" --minimized");
                        }
                        else
                        {
                            key.DeleteValue(AppFolders.Name, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UiLog.Warn($"[Settings] Failed to set startup registry: {ex.Message}");
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
    }
}
