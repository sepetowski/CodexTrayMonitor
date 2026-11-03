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
    private UiChildProcess? flyoutChild;
    private UiChildProcess? settingsChild;
    private FlyoutLoadingWindow? flyoutLoadingWindow;
    private FlyoutWindow? flyout;
    private SettingsWindow? settingsWindow;
    private ThemeManager? theme;
    private MonitorSnapshot snapshot = MonitorSnapshot.Empty;
    private MonitorPresentation? latestPresentation;
    private string? startupWarning;
    private DateTimeOffset lastPresentationAt;
    private DateTimeOffset nextPresentationUpdate;
    private DateTimeOffset lastFlyoutChildClosedAt;
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

    private void PresentationTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < lastPresentationAt || now >= nextPresentationUpdate)
            ApplySnapshot(now);
    }

    private void ToggleFlyout()
    {
        if (exiting)
        {
            return;
        }

        if (flyout is { IsVisible: true })
        {
            flyout.Toggle();
            return;
        }

        if (flyoutChild is { IsRunning: true } running)
        {
            CloseFlyoutLoading();
            running.RequestClose();
            return;
        }

        if (flyoutChild is not null)
            FlyoutChildExited(flyoutChild);

        if (DateTimeOffset.UtcNow - lastFlyoutChildClosedAt < TimeSpan.FromMilliseconds(300))
            return;

        ApplySnapshot();
        var cursor = NativeMethods.CursorPosition();
        try
        {
            flyoutLoadingWindow = new FlyoutLoadingWindow(cursor);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning("Show flyout loading window: {0}", ex);
        }
        try
        {
            var initial = CurrentUiState() with { CursorX = cursor.X, CursorY = cursor.Y };
            flyoutChild = new UiChildProcess("flyout", initial, Dispatcher,
                SaveSettings, ShowSettings, UiChildReady, FlyoutChildExited);
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Keep the panel usable if starting the optional UI process fails.
            Trace.TraceWarning("Start flyout process: {0}", ex);
            CloseFlyoutLoading();
        }

        EnsureFlyout();
        ApplySnapshot();
        flyout!.Toggle();
    }

    private void FlyoutChildExited(UiChildProcess child)
    {
        if (!ReferenceEquals(flyoutChild, child))
            return;

        flyoutChild = null;
        lastFlyoutChildClosedAt = DateTimeOffset.UtcNow;
        CloseFlyoutLoading();
        var failedToOpen = !child.WasReady && !child.CloseRequested && !exiting;
        child.Dispose();
        if (failedToOpen)
        {
            EnsureFlyout();
            ApplySnapshot();
            flyout!.Toggle();
        }
    }

    private void SettingsChildExited(UiChildProcess child)
    {
        if (!ReferenceEquals(settingsChild, child))
            return;

        settingsChild = null;
        var failedToOpen = !child.WasReady && !exiting;
        child.Dispose();
        if (failedToOpen)
            ShowSettingsFallback();
    }

    private void UiChildReady(UiChildProcess child)
    {
        if (ReferenceEquals(child, flyoutChild))
            CloseFlyoutLoading();
    }

    private void CloseFlyoutLoading()
    {
        flyoutLoadingWindow?.Dispose();
        flyoutLoadingWindow = null;
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
        latestPresentation = null;
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
                    ApplySnapshot();
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
            {
                settingsWindow?.UpdateMonitorStatus(snapshot.Warnings);
                settingsChild?.Send(CurrentUiState());
            }
        }
    }

    private void ApplySnapshot(DateTimeOffset? current = null)
    {
        if (tray is null || exiting)
        {
            return;
        }

        var now = current ?? DateTimeOffset.UtcNow;
        var presentation = MonitorPresentation.From(snapshot, now);
        latestPresentation = presentation;
        tray.Update(presentation.FiveHour, theme?.IsLight == true);
        flyout?.Update(presentation);
        settingsWindow?.UpdateMonitorStatus(snapshot.Warnings);
        if (flyoutChild is not null || settingsChild is not null)
        {
            var uiState = CurrentUiState();
            flyoutChild?.Send(uiState);
            settingsChild?.Send(uiState);
        }
        lastPresentationAt = now;
        nextPresentationUpdate = NextPresentationUpdate(snapshot, now);
    }

    private UiMessage CurrentUiState() => new("state",
        latestPresentation ?? MonitorPresentation.From(snapshot, DateTimeOffset.UtcNow),
        settings, snapshot.Warnings.ToArray());

    private static DateTimeOffset NextPresentationUpdate(MonitorSnapshot snapshot, DateTimeOffset now)
    {
        // Today's totals change at local midnight even if no log event arrives.
        var tomorrow = now.ToLocalTime().Date.AddDays(1);
        var next = new DateTimeOffset(tomorrow, TimeZoneInfo.Local.GetUtcOffset(tomorrow)).ToUniversalTime();
        next = EarlierRateChange(snapshot.Rate, now, next);
        return EarlierRateChange(snapshot.WeeklyRate, now, next);
    }

    private static DateTimeOffset EarlierRateChange(RateObservation? observation, DateTimeOffset now, DateTimeOffset next)
    {
        if (observation is null)
            return next;

        // A future observation becomes eligible five minutes before its timestamp.
        if (observation.ObservedAt > now.AddMinutes(5))
        {
            var eligibleAt = observation.ObservedAt.AddMinutes(-5);
            if (eligibleAt < next)
                next = eligibleAt;
        }

        if (observation.ResetsAt is not { } reset || reset <= now)
            return next;

        var remaining = reset - now;
        // Renewal text uses whole minutes; the next change occurs just after
        // the remaining time crosses the next whole-minute boundary.
        var change = remaining >= TimeSpan.FromMinutes(1)
            ? reset - TimeSpan.FromMinutes(Math.Floor(remaining.TotalMinutes)) + TimeSpan.FromMilliseconds(1)
            : reset;
        return change < next ? change : next;
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

        CloseFlyoutLoading();
        flyoutChild?.RequestClose();
        flyout?.Hide();
        if (settingsWindow is { IsVisible: true })
        {
            settingsWindow.Activate();
            return;
        }

        if (settingsChild is { IsRunning: true } running)
        {
            running.Send(new UiMessage("activate"));
            return;
        }

        if (settingsChild is not null)
        {
            SettingsChildExited(settingsChild);
            if (settingsWindow is { IsVisible: true })
                return;
        }

        try
        {
            settingsChild = new UiChildProcess("settings", CurrentUiState(), Dispatcher,
                SaveSettings, ShowSettings, UiChildReady, SettingsChildExited);
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning("Start settings process: {0}", ex);
        }

        ShowSettingsFallback();
    }

    private void ShowSettingsFallback()
    {
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
        flyoutLoadingWindow?.UpdateTheme();
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
        flyoutChild?.Dispose();
        settingsChild?.Dispose();
        CloseFlyoutLoading();
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
