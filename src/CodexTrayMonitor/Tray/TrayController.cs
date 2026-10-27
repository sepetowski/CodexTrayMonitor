using System.Windows;
using System.Windows.Controls;
using CodexTrayMonitor.Core;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace CodexTrayMonitor;

internal sealed class TrayController : IDisposable
{
    private readonly ContextMenu menu = new();
    internal TaskbarIcon Icon { get; }
    private (string Label, int Color, int Size, bool Light)? renderedIcon;

    public TrayController(Action toggle, Action settings, Func<Task> refresh, Func<Task> exit)
    {
        Icon = new TaskbarIcon { ToolTipText = "Codex Tray Monitor", ContextMenu = menu, MenuActivation = PopupActivationMode.RightClick };
        Add("Settings", (_, _) => settings());
        Add("Refresh", async (_, _) => await refresh());
        menu.Items.Add(new Separator());
        Add("Exit", async (_, _) => await exit());
        Icon.TrayLeftMouseUp += (_, _) => toggle();
    }

    private void Add(string title, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = title };
        item.Click += click;
        menu.Items.Add(item);
    }

    // A detached ContextMenu does not receive application resource invalidations.
    public void ApplyTheme()
    {
        menu.Background = (System.Windows.Media.Brush)Application.Current.Resources["Surface"];
        menu.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Text"];
        menu.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["Outline"];
    }

    public void Update(LimitPresentation limit, bool light)
    {
        var key = (TrayIconRenderer.Label(limit), UsagePalette.ForUsage(limit.UsedPercent), NativeMethods.TrayIconSize(), light);
        if (renderedIcon != key)
        {
            // TaskbarIcon disposes the previous owned Icon when replacing it.
            Icon.Icon = TrayIconRenderer.Create(limit, key.Item3, light);
            renderedIcon = key;
        }
        var tooltip = TrayIconRenderer.Tooltip(limit);
        if (Icon.ToolTipText != tooltip)
            Icon.ToolTipText = tooltip;

        if (!Icon.IsCreated)
            Icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Dispose()
    {
        menu.IsOpen = false;
        Icon.Dispose();
    }
}
