namespace CodexTrayMonitor.Core;

public static class LocalPaths
{
    public static string ValidateHome(string path)
    {
        var isNetworkPath = path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal);
        var isNonFileUri = Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile;
        if (isNetworkPath || isNonFileUri)
        {
            throw new ArgumentException("Choose a local folder. Network paths and URIs are not supported.");
        }

        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("Choose a local folder. Network paths are not supported.");
        }

        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(fullPath)!).DriveType == DriveType.Network)
        {
            throw new ArgumentException("The Codex folder must be on a local drive.");
        }

        for (var directory = new DirectoryInfo(fullPath); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("The Codex folder path must not pass through a symbolic link or reparse point.");
            }
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }
}
