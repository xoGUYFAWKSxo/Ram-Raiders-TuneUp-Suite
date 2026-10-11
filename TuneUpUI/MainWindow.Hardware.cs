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
    // Hardware & OS Tools: MSI interrupt mode and shortcuts to Windows tools.
    public partial class MainWindow
    {
        private void BtnMsiScan_Click(object sender, RoutedEventArgs e)
        {
            BtnMsiScan.IsEnabled = false;
            TxtMsiStatus.Text = "Scanning PCI devices...";
            
            SendAsyncIpcRequest(IpcCommands.CheckMsiCompliance, "",
                progress => { },
                completed =>
                {
                    BtnMsiScan.IsEnabled = true;
                    if (completed.Status == IpcStatus.Success)
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
                
                SendAsyncIpcRequest(IpcCommands.ProvisionMSIInterrupts, "",
                    progress => { },
                    completed =>
                    {
                        BtnMsiScan.IsEnabled = true;
                        if (completed.Status == IpcStatus.Success)
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

                SendAsyncIpcRequest(IpcCommands.RevertMSIInterrupts, "",
                    progress => { },
                    completed =>
                    {
                        BtnMsiScan.IsEnabled = true;
                        BtnMsiRevert.IsEnabled = true;
                        TxtMsiStatus.Text = completed.Status == IpcStatus.Success
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
    }
}
