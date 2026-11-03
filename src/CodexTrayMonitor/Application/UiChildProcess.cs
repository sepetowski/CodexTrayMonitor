using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using CodexTrayMonitor.Core;

namespace CodexTrayMonitor;

internal sealed class UiChildProcess : IDisposable
{
    private readonly Process process;
    private readonly Dispatcher dispatcher;
    private readonly Func<MonitorSettings, Task> saveSettings;
    private readonly Action settingsRequested;
    private readonly Action<UiChildProcess> ready;
    private readonly Action<UiChildProcess> exited;
    private Task lastWrite = Task.CompletedTask;
    private bool disposed;

    public bool IsRunning => !disposed && !process.HasExited;
    public bool WasReady { get; private set; }
    public bool CloseRequested { get; private set; }

    public UiChildProcess(string mode, UiMessage initialState, Dispatcher dispatcher,
        Func<MonitorSettings, Task> saveSettings, Action settingsRequested,
        Action<UiChildProcess> ready, Action<UiChildProcess> exited)
    {
        this.dispatcher = dispatcher;
        this.saveSettings = saveSettings;
        this.settingsRequested = settingsRequested;
        this.ready = ready;
        this.exited = exited;

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the application executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        start.ArgumentList.Add("--ui-child");
        start.ArgumentList.Add(mode);
        process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the UI process.");
        process.StandardInput.AutoFlush = true;
        _ = ReadEventsAsync();
        Send(initialState);
        _ = EnsureReadyAsync();
    }

    private async Task EnsureReadyAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (!dispatcher.HasShutdownStarted)
        {
            _ = dispatcher.BeginInvoke(() =>
            {
                if (disposed || WasReady)
                    return;

                Trace.TraceWarning("UI process did not become ready in time.");
                Dispose();
                exited(this);
            });
        }
    }

    public void Send(UiMessage message)
    {
        if (disposed || process.HasExited)
            return;

        var line = JsonSerializer.Serialize(message);
        lastWrite = WriteAfterAsync(lastWrite, line);
    }

    public void RequestClose()
    {
        CloseRequested = true;
        if (!WasReady)
            Dispose();
        else
            Send(new UiMessage("close"));
    }

    private async Task WriteAfterAsync(Task previous, string line)
    {
        try
        {
            await previous.ConfigureAwait(false);
            if (!disposed && !process.HasExited)
                await process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Trace.TraceWarning("UI process write: {0}", ex);
        }
    }

    private async Task ReadEventsAsync()
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
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

                if (message is not null && !dispatcher.HasShutdownStarted)
                    await dispatcher.InvokeAsync(() => HandleEvent(message));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Trace.TraceWarning("UI process read: {0}", ex);
        }
        finally
        {
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException)
            {
                Trace.TraceWarning("UI process exit: {0}", ex);
            }

            if (!dispatcher.HasShutdownStarted)
                _ = dispatcher.BeginInvoke(() => exited(this));
        }
    }

    private void HandleEvent(UiMessage message)
    {
        if (disposed)
            return;

        if (message.Kind == "ready")
        {
            WasReady = true;
            ready(this);
        }
        else if (message.Kind == "settings")
            settingsRequested();
        else if (message.Kind == "save" && message.Settings is not null)
            _ = SaveAsync(message.Settings);
    }

    private async Task SaveAsync(MonitorSettings next)
    {
        try
        {
            await saveSettings(next);
            Send(new UiMessage("save-ok"));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("UI settings save: {0}", ex);
            Send(new UiMessage("save-error", Error: ex.Message));
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning("UI process stop: {0}", ex);
        }
        process.Dispose();
    }
}
