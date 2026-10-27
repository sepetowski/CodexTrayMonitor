namespace CodexTrayMonitor.Core;

public static class CodexHomeResolver
{
    public static string? ResolveHome(this MonitorSettings settings)
    {
        var environmentHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return settings.ResolveHome(environmentHome, userProfile);
    }

    public static string? ResolveHome(this MonitorSettings settings, string? environmentHome, string userProfile)
    {
        return ExistingHome(settings.CodexHome)
            ?? ExistingHome(environmentHome)
            ?? ExistingHome(Path.Combine(userProfile, ".codex"));
    }

    public static string? ExistingHome(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = LocalPaths.ValidateHome(path.Trim());
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static void Validate(MonitorSettings settings)
    {
        settings.Validate();
        if (!string.IsNullOrWhiteSpace(settings.CodexHome) && ExistingHome(settings.CodexHome) is null)
        {
            throw new ArgumentException("Choose an existing local Codex folder.");
        }
    }
}
