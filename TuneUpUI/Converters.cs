using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TuneUpEngine;

// Disambiguate System.Drawing.Color vs System.Windows.Media.Color
using WpfColor = System.Windows.Media.Color;

namespace TuneUpUI
{
    // ─── SelectableAppxPackage ────────────────────────────────────────────────
    // Thin wrapper around AppxPackageInfo that adds an IsSelected property for
    // use with CheckBox bindings in the DebloatGuard AppX list.
    public class SelectableAppxPackage : AppxPackageInfo
    {
        public bool IsSelected { get; set; } = false;
    }

    // ─── BoolToColorConverter ─────────────────────────────────────────────────
    // Used by DPC Latency table: true (spike) → red, false → white
    public class BoolToColorConverter : IValueConverter
    {
        public static readonly BoolToColorConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isSpike = value is bool b && b;
            return isSpike
                ? new SolidColorBrush(WpfColor.FromRgb(0xFF, 0x4D, 0x4D))
                : new SolidColorBrush(Colors.White);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // ─── BoolToBadgeConverter ─────────────────────────────────────────────────
    // Used for status badges: true → red/spike, false → green/OK
    // Also used for SymlinkActive: true → indigo/Active, false → gray/Broken
    public class BoolToBadgeConverter : IValueConverter
    {
        public static readonly BoolToBadgeConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isTrue = value is bool b && b;
            return isTrue
                ? new SolidColorBrush(WpfColor.FromRgb(0x10, 0x61, 0x40)) // green (Active/OK)
                : new SolidColorBrush(WpfColor.FromRgb(0x9B, 0x1C, 0x1C)); // red (Broken/SPIKE)
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // ─── BoolToSymlinkLabelConverter ──────────────────────────────────────────
    // Shows "Active" when symlink is healthy, "Broken" when stale
    public class BoolToSymlinkLabelConverter : IValueConverter
    {
        public static readonly BoolToSymlinkLabelConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? "✓ Active" : "✗ Broken";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // ─── BytesConverter ───────────────────────────────────────────────────────
    // Formats a long byte count as "4.2 GB", "512 MB", etc.
    public class BytesConverter : IValueConverter
    {
        public static readonly BytesConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long bytes) return StorageMigrator.FormatBytes(bytes);
            return "—";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
