using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CodexTrayMonitor;

// Shared palette for the flyout bars and tray number.
public sealed class UsageBrushConverter : IValueConverter
{
    private static readonly Dictionary<int, SolidColorBrush> Brushes = new[]
        {
            UsagePalette.Low,
            UsagePalette.Gold,
            UsagePalette.Orange,
            UsagePalette.High,
            UsagePalette.Unavailable
        }
        .ToDictionary(color => color, FrozenBrush);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Brushes[UsagePalette.ForUsage(value as double?)];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;

    private static SolidColorBrush FrozenBrush(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
