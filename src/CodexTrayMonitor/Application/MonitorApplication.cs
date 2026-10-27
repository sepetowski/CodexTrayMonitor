using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CodexTrayMonitor.Core;

namespace CodexTrayMonitor;

internal sealed class MonitorApplication : Application
{
    private readonly string dataDirectory;
    private readonly SettingsPersistence persistence;
    private readonly DispatcherTimer polling = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly DispatcherTimer presentationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();
    private MonitorSettings settings = new();
    private SessionMonitor? monitor;
    private string? monitoredHome;
    private TrayController? tray;
    private FlyoutWindow? flyout;
    private SettingsWindow? settingsWindow;
    private ThemeManager? theme;
    private MonitorSnapshot snapshot = MonitorSnapshot.Empty;
    private string? startupWarning;
    private bool exiting;
    private bool refreshPending;
    private int fileNotificationQueued;
    private Task refreshTask = Task.CompletedTask;
    private Task saveTask = Task.CompletedTask;
    private Task? exitTask;

    private string SettingsPath => Path.Combine(dataDirectory, "settings.json");

    public MonitorApplication(string[] args)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var index = Array.IndexOf(args, "--data-dir");
        dataDirectory = index >= 0 && index + 1 < args.Length ? LocalPaths.ValidateHome(args[index + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTrayMonitor");
        persistence = new(SettingsPath, args.Contains("--no-autostart"));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(dataDirectory);
        settings = SettingsStore.Load(SettingsPath);
        theme = new ThemeManager(this);
        theme.Apply(settings.Appearance);
        theme.Changed += ApplyTheme;
        try
        {
            persistence.InitializeAutoStart(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            Trace.TraceWarning("Autostart: {0}", ex);
            startupWarning = "Autostart is unavailable.";
        }
        tray = new TrayController(ToggleFlyout, ShowSettings, RefreshAsync, ExitApplication);
        ApplyTheme();
        polling.Tick += PollingTick;
        debounce.Tick += DebounceTick;
        presentationTimer.Tick += PresentationTick;
        polling.Start();
        presentationTimer.Start();
        _ = RefreshAsync();
    }

    private async void PollingTick(object? sender, EventArgs e) => await RefreshAsync();

    private async void DebounceTick(object? sender, EventArgs e)
    {
        debounce.Stop();
        await RefreshAsync();
    }

    private void PresentationTick(object? sender, EventArgs e) => ApplySnapshot();

    private void ToggleFlyout()
    {
        if (exiting)
        {
            return;
        }

        EnsureFlyout();
        ApplySnapshot();
        flyout!.Toggle();
    }

    private void CreateMonitor()
    {
        var home = settings.ResolveHome();
        if (string.Equals(home, monitoredHome, StringComparison.OrdinalIgnoreCase) && (monitor is not null || home is null))
        {
            return;
        }

        var next = home is null ? null : new SessionMonitor(home, Path.Combine(dataDirectory, "offset-cache.json"));
        ReplaceMonitor(next, home);
    }

    private void ReplaceMonitor(SessionMonitor? next, string? home)
    {
        if (monitor is not null)
        {
            monitor.Changed -= OnFilesChanged;
            monitor.Dispose();
        }

        monitor = next;
        monitoredHome = home;
        snapshot = MonitorSnapshot.Empty;
        if (monitor is not null)
        {
            monitor.Changed += OnFilesChanged;
        }
    }

    private void OnFilesChanged()
    {
        if (exiting || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (Interlocked.Exchange(ref fileNotificationQueued, 1) != 0)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref fileNotificationQueued, 0);
            if (!exiting)
            {
                debounce.Stop();
                debounce.Start();
            }
        });
    }

    private Task RefreshAsync()
    {
        if (exiting)
        {
            return Task.CompletedTask;
        }

        refreshPending = true;
        if (refreshTask.IsCompleted)
        {
            refreshTask = RefreshCoreAsync();
        }

        return refreshTask;
    }

    private async Task RefreshCoreAsync()
    {
        try
        {
            do
            {
                refreshPending = false;
                await refreshGate.WaitAsync(cancellation.Token);
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    CreateMonitor();
                    snapshot = monitor is null ? MonitorSnapshot.Empty : await monitor.RefreshAsync(cancellation.Token);
                    if (startupWarning is not null)
                    {
                        snapshot = snapshot with
                        {
                            Warnings = snapshot.Warnings.Append(startupWarning).ToArray()
                        };
                    }
                }
                finally
                {
                    refreshGate.Release();
                }
                if (!exiting)
                {
                    ApplySnapshot();
                }
            } while (refreshPending && !exiting);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Event boundary: retain the last usable snapshot and report the failure.
            Trace.TraceError("Refresh: {0}", ex);
            snapshot = snapshot with
            {
                Warnings = ["Cannot refresh session data."]
            };
            if (!exiting)
                settingsWindow?.UpdateMonitorStatus(snapshot.Warnings);
        }
    }

    private void ApplySnapshot()
    {
        if (tray is null || exiting)
        {
            return;
        }

        var presentation = MonitorPresentation.From(snapshot, DateTimeOffset.UtcNow);
        tray.Update(presentation.FiveHour, theme?.IsLight == true);
        flyout?.Update(presentation);
        settingsWindow?.UpdateMonitorStatus(snapshot.Warnings);
    }

    private void EnsureFlyout()
    {
        if (flyout is not null)
        {
            return;
        }

        flyout = new FlyoutWindow();
        flyout.SettingsRequested += ShowSettings;
    }

    private void ShowSettings()
    {
        if (exiting)
        {
            return;
        }

        flyout?.Hide();
        if (settingsWindow is { IsVisible: true })
        {
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new SettingsWindow(settings, SaveSettings);
        settingsWindow.UpdateMonitorStatus(snapshot.Warnings);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    private Task SaveSettings(MonitorSettings next) => saveTask = SaveSettingsCore(next);

    private async Task SaveSettingsCore(MonitorSettings next)
    {
        if (exiting)
        {
            throw new OperationCanceledException();
        }

        CodexHomeResolver.Validate(next);
        var home = next.ResolveHome();
        await refreshGate.WaitAsync(cancellation.Token);
        SessionMonitor? replacement = null;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            bool homeChanged = !string.Equals(home, monitoredHome, StringComparison.OrdinalIgnoreCase);
            if (homeChanged && home is not null)
            {
                replacement = new SessionMonitor(home, Path.Combine(dataDirectory, "offset-cache.json"));
            }

            persistence.Save(next);
            if (homeChanged)
            {
                debounce.Stop();
                ReplaceMonitor(replacement, home);
                replacement = null;
            }

            settings = next;
            theme!.Apply(settings.Appearance);
            startupWarning = null;
        }
        finally
        {
            replacement?.Dispose();
            refreshGate.Release();
        }
        ApplySnapshot();
        _ = RefreshAsync();
    }

    private void ApplyTheme()
    {
        tray?.ApplyTheme();
        ApplySnapshot();
    }

    private Task ExitApplication() => exitTask ??= StopAndShutdownAsync();

    private void BeginStopping()
    {
        exiting = true;
        polling.Stop();
        debounce.Stop();
        presentationTimer.Stop();
        cancellation.Cancel();
    }

    private async Task StopAndShutdownAsync()
    {
        BeginStopping();
        await refreshTask;
        await refreshGate.WaitAsync();
        try
        {
            DetachMonitor();
        }
        finally
        {
            refreshGate.Release();
        }
        Shutdown();
    }

    private void DetachMonitor()
    {
        if (monitor is null)
        {
            return;
        }

        monitor.Changed -= OnFilesChanged;
        monitor.StopAsync().GetAwaiter().GetResult();
        monitor = null;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        BeginStopping();
        // SessionMonitor awaits only background work, never the WPF dispatcher.
        DetachMonitor();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        BeginStopping();
        DetachMonitor();
        polling.Tick -= PollingTick;
        debounce.Tick -= DebounceTick;
        presentationTimer.Tick -= PresentationTick;
        if (flyout is not null)
        {
            flyout.SettingsRequested -= ShowSettings;
        }

        tray?.Dispose();
        if (theme is not null)
        {
            theme.Changed -= ApplyTheme;
            theme.Dispose();
        }
        // Release synchronization only after all owners have left their finally blocks.
        _ = Task.WhenAll(refreshTask, saveTask).ContinueWith(completed =>
        {
            if (completed.Exception is { } error)
            {
                Trace.TraceWarning("Shutdown: {0}", error);
            }

            refreshGate.Dispose();
            cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        base.OnExit(e);
    }
}
