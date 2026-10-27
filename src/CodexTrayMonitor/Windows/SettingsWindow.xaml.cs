using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodexTrayMonitor.Core;
using Microsoft.Win32;

namespace CodexTrayMonitor;

public partial class SettingsWindow : Window
{
    private readonly Func<MonitorSettings, Task> save;
    private bool automatic;
    private bool updatingHome;
    private bool initialized;
    private bool saving;
    private ThemeManager? previewTheme;

    public SettingsWindow(MonitorSettings settings, Func<MonitorSettings, Task> save, Func<bool>? readAutoStart = null)
    {
        InitializeComponent();
        this.save = save;
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 24);
        AppearanceBox.SelectedValue = settings.Appearance.ToString();
        automatic = CodexHomeResolver.ExistingHome(settings.CodexHome) is null;
        SetHome(settings.ResolveHome());
        initialized = true;
        UpdateHomeStatus();
        try
        {
            AutoStartBox.IsChecked = (readAutoStart ?? AutoStart.IsEnabled)();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Read startup settings: {0}", ex);
            ShowError("Cannot read startup settings.");
            AutoStartBox.IsEnabled = SaveButton.IsEnabled = false;
        }
        Closing += (_, e) => e.Cancel = saving;
        Closed += (_, _) => previewTheme?.Dispose();
    }

    private void AppearanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || saving || AppearanceBox.SelectedValue is not string value
            || !Enum.TryParse<AppearanceMode>(value, out var mode))
        {
            return;
        }

        previewTheme ??= new ThemeManager(Application.Current, Resources);
        previewTheme.Apply(mode);
    }

    private void SetHome(string? path)
    {
        updatingHome = true;
        HomeBox.Text = path ?? "";
        HomeBox.CaretIndex = 0;
        updatingHome = false;
    }

    private void HomeChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || updatingHome)
        {
            return;
        }

        automatic = false;
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateHomeStatus();
    }

    private void UpdateHomeStatus()
    {
        var exists = CodexHomeResolver.ExistingHome(HomeBox.Text) is not null;
        HomePlaceholder.Visibility = string.IsNullOrEmpty(HomeBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (!exists)
            HomeStatus.Text = "Folder not found";
        else if (automatic)
            HomeStatus.Text = "✓ Detected automatically";
        else
            HomeStatus.Text = "✓ Folder selected manually";

        HomeStatus.SetResourceReference(TextBlock.ForegroundProperty, exists ? "Success" : "Secondary");
        HomeHelp.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        AutomaticButton.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AutomaticClick(object sender, RoutedEventArgs e)
    {
        automatic = true;
        ErrorText.Visibility = Visibility.Collapsed;
        SetHome(new MonitorSettings().ResolveHome());
        UpdateHomeStatus();
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose the Codex folder", Multiselect = false };
        if (CodexHomeResolver.ExistingHome(HomeBox.Text) is { } home)
        {
            picker.InitialDirectory = home;
        }

        if (picker.ShowDialog(this) == true)
        {
            automatic = false;
            SetHome(picker.FolderName);
            UpdateHomeStatus();
        }
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (saving)
        {
            return;
        }

        var next = new MonitorSettings
        {
            CodexHome = automatic || string.IsNullOrWhiteSpace(HomeBox.Text) ? null : HomeBox.Text.Trim(),
            AutoStart = AutoStartBox.IsChecked == true,
            Appearance = Enum.Parse<AppearanceMode>((string)AppearanceBox.SelectedValue)
        };
        saving = true;
        Form.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            CodexHomeResolver.Validate(next);
            if (next.CodexHome is not null)
            {
                next.CodexHome = CodexHomeResolver.ExistingHome(next.CodexHome);
            }

            await save(next);
            saving = false;
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Save settings: {0}", ex);
            ShowError("Could not save settings. Check the selected folder and your access permissions.");
        }
        finally
        {
            saving = false;
            Form.IsEnabled = true;
        }
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    internal void UpdateMonitorStatus(IReadOnlyList<string> warnings)
    {
        MonitorStatus.Text = string.Join(Environment.NewLine, warnings);
        MonitorStatus.Visibility = warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (!saving)
        {
            Close();
        }
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !AppearanceBox.IsDropDownOpen && !saving)
        {
            Close();
            e.Handled = true;
        }
    }

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBlock or Grid && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
