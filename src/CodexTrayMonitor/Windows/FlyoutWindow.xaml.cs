using System.Windows;
using System.Windows.Input;
using CodexTrayMonitor.Core;

namespace CodexTrayMonitor;

public partial class FlyoutWindow : Window
{
    public event Action? SettingsRequested;
    private DateTimeOffset hiddenAt;

    public FlyoutWindow()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibilityChanged;
        // The transparent flyout draws one rounded surface itself; native window
        // decoration would add a second outline around that surface.
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            // WPF remembers logical focus when a window is hidden. Restore focus
            // to the panel itself, not to the last clicked toolbar button.
            FocusManager.SetFocusedElement(this, this);
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Focus();
    }

    public void Toggle()
    {
        // Clicking the tray first deactivates the flyout, then dispatches MouseClick.
        if (IsVisible || DateTimeOffset.UtcNow - hiddenAt < TimeSpan.FromMilliseconds(300))
        {
            Hide();
            return;
        }
        Show();
        NativeMethods.PositionFlyout(this);
        Activate();
        Focus();
    }

    public void Update(MonitorPresentation presentation)
    {
        if (!Equals(DataContext, presentation))
        {
            DataContext = presentation;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        hiddenAt = DateTimeOffset.UtcNow;
        Hide();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Hide();

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        SettingsRequested?.Invoke();
    }
}
