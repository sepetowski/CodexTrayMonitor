using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using CodexTrayMonitor.Core;

namespace CodexTrayMonitor;

internal sealed class ThemeManager : IDisposable
{
    private static readonly (string Name, string Light, string Dark)[] Colors =
    [
        ("Surface", "#F7F8FA", "#17191D"),
        ("Text", "#20242B", "#E9EDF4"),
        ("Secondary", "#596273", "#BCC4D0"),
        ("Outline", "#CCD1DA", "#383E48"),
        ("Hover", "#E8ECF2", "#30353F"),
        ("Selection", "#D7E3F9", "#354664"),
        ("Focus", "#4777C2", "#89ACFF"),
        ("Primary", "#20242B", "#E9EDF4"),
        ("PrimaryText", "#FFFFFF", "#17191D"),
        ("Success", "#25754B", "#71CFA1"),
        ("Error", "#A43B30", "#F17E7E"),
        ("Track", "#E1E5EC", "#363B45"),
        ("TooltipSurface", "#FFFFFF", "#22262E")
    ];

    private readonly Application app;
    private readonly ResourceDictionary resources;
    private bool disposed;

    public AppearanceMode Mode { get; private set; }
    public bool IsLight { get; private set; }
    public event Action? Changed;

    public ThemeManager(Application app)
        : this(app, app.Resources)
    {
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CodexTrayMonitor;component/Theming/UiStyles.xaml", UriKind.Relative)
        });
    }

    // Window-scoped resources allow a draft theme without changing the application.
    internal ThemeManager(Application app, ResourceDictionary resources)
    {
        this.app = app;
        this.resources = resources;
        Apply(AppearanceMode.System);
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
    }

    private static bool ReadSystemIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (disposed || app.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(() =>
        {
            if (!disposed)
            {
                RefreshSystemTheme();
            }
        });
    }

    internal void RefreshSystemTheme()
    {
        if (Mode == AppearanceMode.System)
        {
            Apply(Mode);
        }
    }

    public void Apply(AppearanceMode mode)
    {
        Mode = mode;
        IsLight = mode == AppearanceMode.Light || mode == AppearanceMode.System && ReadSystemIsLight();

        foreach (var (name, light, dark) in Colors)
        {
            var color = IsLight ? light : dark;
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            resources[name] = brush;
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        disposed = true;
        SystemEvents.UserPreferenceChanged -= PreferencesChanged;
    }
}
