using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CodexTrayMonitor.Core;
using H.NotifyIcon;

namespace CodexTrayMonitor;

// Appearance configuration; all bitmap drawing is supplied by H.NotifyIcon.
internal static class TrayIconRenderer
{
    public static string Label(LimitPresentation limit) => limit.UsedPercent is { } used
        ? Math.Round(used, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) : "?";

    private static GeneratedIconSource Source(LimitPresentation limit, int size, bool light)
    {
        var label = Label(limit);
        var fontSize = size * (label.Length > 2 ? .45 : .65);
        // GDI+ creates bitmaps at the desktop DPI, which need not be 96.
        using var dpi = new System.Drawing.Bitmap(1, 1);
        var text = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            fontSize,
            Brushes.Black,
            1);
        var foregroundRgb = limit.UsedPercent is null ? 0x94A3B8 : UsagePalette.ForUsage(limit.UsedPercent);
        var foregroundColor = Color.FromRgb(
            (byte)(foregroundRgb >> 16),
            (byte)(foregroundRgb >> 8),
            (byte)foregroundRgb);

        return new GeneratedIconSource
        {
            Text = label,
            Size = size,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.Bold,
            FontSize = fontSize * 72 / dpi.HorizontalResolution,
            TextMargin = new Thickness((size - text.Width) / 2, (size - text.Height) / 2, 0, 0),
            BackgroundType = BackgroundType.RoundedRectangle,
            CornerRadius = new CornerRadius(size * .115),
            Background = new SolidColorBrush(light ? Color.FromRgb(238, 240, 244) : Color.FromRgb(38, 43, 52)),
            Foreground = new SolidColorBrush(foregroundColor)
        };
    }

    public static System.Drawing.Icon Create(LimitPresentation limit, int size, bool light = false)
    {
        // 2.4.1 returns Icon.FromHandle(GetHicon()), which does not own its HICON.
        // Clone for TaskbarIcon ownership and release the original to avoid a USER handle leak.
        using var borrowed = Source(limit, size, light).ToIcon();
        try
        {
            return (System.Drawing.Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(borrowed.Handle);
        }
    }

    public static string Tooltip(LimitPresentation limit)
    {
        var value = limit.UsedPercent is not null ? limit.PercentText + " used" : "No data";
        var text = $"5-hour limit: {value}\n{limit.RenewalText}";
        return text.Length > 127 ? text[..127] : text;
    }
}
