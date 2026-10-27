using CodexTrayMonitor.Core;

namespace CodexTrayMonitor;

internal sealed class SettingsPersistence(string path, bool suppressAutoStart)
{
    public void InitializeAutoStart(MonitorSettings settings)
    {
        if (suppressAutoStart)
        {
            return;
        }

        if (!File.Exists(path))
        {
            var existing = AutoStart.Capture();
            if (existing.Run is not null || existing.Approval is not null)
            {
                settings.AutoStart = AutoStart.IsEnabled();
            }

            // First run does not change Windows startup settings. In particular,
            // launching a portable copy must not take over an installed copy.
            SettingsStore.Save(path, settings);
        }
    }

    public void Save(MonitorSettings settings)
    {
        CodexHomeResolver.Validate(settings);
        if (suppressAutoStart)
        {
            SettingsStore.Save(path, settings);
            return;
        }
        var previous = AutoStart.Capture();
        try
        {
            AutoStart.SetEnabled(settings.AutoStart);
            SettingsStore.Save(path, settings);
        }
        catch (Exception saveError)
        {
            try
            {
                AutoStart.Restore(previous);
            }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException("Settings could not be saved and autostart could not be restored. Check startup settings in Windows.",
                    new AggregateException(saveError, rollbackError));
            }
            throw;
        }
    }
}
