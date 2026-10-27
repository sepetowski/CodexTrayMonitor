namespace CodexTrayMonitor.Core;

public static class SettingsStore
{
    public static MonitorSettings Load(string path)
    {
        var settings = JsonStorage.Read<MonitorSettings>(path, 1024 * 1024) ?? new();
        if (!Enum.IsDefined(settings.Appearance))
        {
            settings.Appearance = AppearanceMode.System;
        }

        return settings;
    }

    public static void Save(string path, MonitorSettings settings)
    {
        CodexHomeResolver.Validate(settings);
        JsonStorage.Write(path, settings);
    }
}
