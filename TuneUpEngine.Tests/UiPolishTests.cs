using System.Collections.Generic;
using TuneUpEngine;
using TuneUpUI;
using Xunit;

namespace TuneUpEngine.Tests
{
    public class MsiScanTests
    {
        [Theory]
        [InlineData("{4d36e968-e325-11ce-bfc1-08002be10318}", null, true)]  // Display, as Windows 11 stores it
        [InlineData("{4D36E972-E325-11CE-BFC1-08002BE10318}", null, true)]  // Net, upper case
        [InlineData(null, "Display", true)]                                  // older systems' Class name
        [InlineData("{4d36e97d-e325-11ce-bfc1-08002be10318}", null, false)] // System devices
        [InlineData(null, null, false)]
        public void DisplayAndNetworkDevices_AreRecognisedByClassGuid(string? guid, string? name, bool expected) =>
            Assert.Equal(expected, HardwareController.IsDisplayOrNet(guid, name));

        private static HardwareController.MsiDevice Dev(string name, int? msi) => new("VEN_1", "1", name, msi);

        [Fact]
        public void Scan_WithEveryDeviceOn_SaysNothingToEnable()
        {
            string text = HardwareController.DescribeScan(new List<HardwareController.MsiDevice>
            {
                Dev("GPU", 1), Dev("NIC", 1)
            });
            Assert.StartsWith("All 2 supported device(s) already use MSI", text);
            Assert.Contains("• GPU — MSI on", text);
        }

        [Fact]
        public void Scan_WithADeviceOff_OffersEnable()
        {
            string text = HardwareController.DescribeScan(new List<HardwareController.MsiDevice>
            {
                Dev("GPU", 1), Dev("NIC", 0), Dev("WiFi", null)
            });
            Assert.StartsWith("Compliant: 2 of 3", text);
            Assert.Contains("• WiFi — MSI off", text);
        }

        [Fact]
        public void Scan_WithNoDevices_SaysSo() =>
            Assert.StartsWith("No plugged-in", HardwareController.DescribeScan(new List<HardwareController.MsiDevice>()));
    }

    public class ThemeStylesTests
    {
        // XAML style mistakes only show up when the app starts, so load the dictionary for real.
        [Fact]
        public void ThemeStyles_Load_WithDarkRowAndHeaderStyles()
        {
            System.Exception? failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var dict = (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(
                        new System.Uri("/TuneUpUI;component/ThemeStyles.xaml", System.UriKind.Relative));
                    Assert.NotNull(dict[typeof(System.Windows.Controls.ListViewItem)]);
                    Assert.NotNull(dict[typeof(System.Windows.Controls.ListBoxItem)]);
                    Assert.NotNull(dict[typeof(System.Windows.Controls.GridViewColumnHeader)]);
                }
                catch (System.Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            Assert.True(failure == null, failure?.ToString());
        }
    }

    public class FirstRunDefaultsTests
    {
        [Fact]
        public void UiSettings_AreAllOffForANewUser()
        {
            var s = new MainWindow.ApplicationSettings();
            Assert.False(s.StartOnStartup);
            Assert.False(s.LaunchUiMinimized);
            Assert.False(s.MinimizeToTrayOnClose);
            Assert.False(s.EnableTrayNotifications);
            Assert.False(s.ProBalanceToast);
            Assert.False(s.MemoryPurgeToast);
        }

        [Fact]
        public void EngineFeatures_AreAllOffOnFirstRun()
        {
            var s = new EngineSettings();
            Assert.False(s.ProBalanceEnabled);
            Assert.False(s.AutoPurgeEnabled);
            Assert.False(s.GameModeEnabled);
        }
    }
}
