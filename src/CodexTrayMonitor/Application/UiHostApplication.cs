using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CodexTrayMonitor.Core;

namespace CodexTrayMonitor;

internal sealed class UiHostApplication : Application
{
    private readonly string mode;
    private readonly StreamReader input = new(Console.OpenStandardInput(), Encoding.UTF8);
    private readonly StreamWriter output = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    private ThemeManager? theme;
    private FlyoutWindow? flyout;
    private SettingsWindow? settingsWindow;
    private TaskCompletionSource? pendingSave;
    private bool flyoutShown;

    public UiHostApplication(string mode)
    {
        this.mode = mode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        theme = new ThemeManager(this);
        _ = ReadCommandsAsync();
    }

    private async Task ReadCommandsAsync()
    {
        try
        {
            string? line;
            while ((line = await input.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                UiMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<UiMessage>(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message is not null && !Dispatcher.HasShutdownStarted)
                    await Dispatcher.InvokeAsync(() => HandleCommand(message));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Trace.TraceWarning("UI host input: {0}", ex);
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(Shutdown);
        }
    }

    private void HandleCommand(UiMessage message)
    {
        switch (message.Kind)
        {
            case "state" when message.Settings is not null && message.Presentation is not null:
                if (theme!.Mode != message.Settings.Appearance)
                    theme.Apply(message.Settings.Appearance);
                if (mode == "flyout")
                {
                    if (flyout is null)
                    {
                        flyout = new FlyoutWindow();
                        flyout.IsVisibleChanged += FlyoutVisibilityChanged;
                        flyout.SettingsRequested += () =>
                        {
                            Send(new UiMessage("settings"));
                            Shutdown();
                        };
                        flyout.Update(message.Presentation);
                        flyoutShown = true;
                        var cursor = NativeMethods.CursorPosition();
                        flyout.ToggleAt(new NativePoint
                        {
                            X = message.CursorX ?? cursor.X,
                            Y = message.CursorY ?? cursor.Y
                        });
                        Send(new UiMessage("ready"));
                    }
                    else
                    {
                        flyout.Update(message.Presentation);
                    }
                }
                else if (settingsWindow is null)
                {
                    settingsWindow = new SettingsWindow(message.Settings, SaveSettingsAsync);
                    settingsWindow.UpdateMonitorStatus(message.Warnings ?? []);
                    settingsWindow.Closed += (_, _) => Shutdown();
                    settingsWindow.Show();
                    settingsWindow.Activate();
                    Send(new UiMessage("ready"));
                }
                else
                {
                    settingsWindow.UpdateMonitorStatus(message.Warnings ?? []);
                }
                break;
            case "close":
                Shutdown();
                break;
            case "activate":
                settingsWindow?.Activate();
                break;
            case "save-ok":
                pendingSave?.TrySetResult();
                pendingSave = null;
                break;
            case "save-error":
                pendingSave?.TrySetException(new IOException(message.Error ?? "Cannot save settings."));
                pendingSave = null;
                break;
        }
    }

    private void FlyoutVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (flyoutShown && flyout is { IsVisible: false } && !Dispatcher.HasShutdownStarted)
            _ = Dispatcher.BeginInvoke(Shutdown, DispatcherPriority.Background);
    }

    private Task SaveSettingsAsync(MonitorSettings next)
    {
        if (pendingSave is not null)
            throw new InvalidOperationException("A settings save is already running.");

        pendingSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(new UiMessage("save", Settings: next));
        return pendingSave.Task;
    }

    private void Send(UiMessage message)
    {
        try
        {
            output.WriteLine(JsonSerializer.Serialize(message));
        }
        catch (IOException ex)
        {
            Trace.TraceWarning("UI host output: {0}", ex);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        pendingSave?.TrySetCanceled();
        theme?.Dispose();
        input.Dispose();
        output.Dispose();
        base.OnExit(e);
    }
}
