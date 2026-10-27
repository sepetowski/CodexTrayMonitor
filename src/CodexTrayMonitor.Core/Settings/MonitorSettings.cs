namespace CodexTrayMonitor.Core;

public sealed class MonitorSettings
{
    public string? CodexHome { get; set; }
    public bool AutoStart { get; set; }
    public AppearanceMode Appearance { get; set; } = AppearanceMode.System;

    public void Validate()
    {
        if (!Enum.IsDefined(Appearance))
        {
            throw new ArgumentException("Choose an application appearance.");
        }
    }
}
