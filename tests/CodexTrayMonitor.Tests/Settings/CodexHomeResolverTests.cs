using CodexTrayMonitor.Core;

namespace CodexTrayMonitor.Tests;

[TestClass]
public sealed class CodexHomeResolverTests
{
    private readonly TestWorkspace workspace = new();

    private string SettingsRoot => Directory.CreateDirectory(Path.Combine(workspace.Root, "settings")).FullName;

    private string ManualHome => Directory.CreateDirectory(Path.Combine(SettingsRoot, "manual")).FullName;

    private string EnvHome => Directory.CreateDirectory(Path.Combine(SettingsRoot, "environment")).FullName;

    private string Profile => Directory.CreateDirectory(Path.Combine(SettingsRoot, "profile")).FullName;

    private string DefaultHome => Directory.CreateDirectory(Path.Combine(Profile, ".codex")).FullName;

    [TestMethod(DisplayName = "Directory resolution prefers valid manual path")]
    public void DirectoryResolutionPrefersValidManualPath()
    {
        var settings = new MonitorSettings { CodexHome = ManualHome };

        Assert.AreEqual(ManualHome, settings.ResolveHome(EnvHome, Profile));
    }

    [TestMethod(DisplayName = "Invalid manual path falls back to CODEX_HOME")]
    public void InvalidManualPathFallsBackToCODEXHOME()
    {
        var settings = new MonitorSettings { CodexHome = Path.Combine(workspace.Root, "missing") };

        Assert.AreEqual(EnvHome, settings.ResolveHome(EnvHome, Profile));
    }

    [TestMethod(DisplayName = "Missing CODEX_HOME falls back to profile directory")]
    public void MissingCODEXHOMEFallsBackToProfileDirectory()
    {
        var missingHome = Path.Combine(workspace.Root, "missing");

        Assert.AreEqual(DefaultHome, new MonitorSettings().ResolveHome(missingHome, Profile));
    }

    [TestMethod(DisplayName = "Blank and invalid environment paths fall back safely")]
    public void BlankAndInvalidEnvironmentPathsFallBackSafely()
    {
        foreach (var path in new[]
        {
            "",
            " ",
            "https://example.invalid",
            "\\\\server\\share",
            "bad\0path"
        })
        {
            Assert.AreEqual(DefaultHome, new MonitorSettings().ResolveHome(path, Profile));
        }
    }

    [TestMethod(DisplayName = "A file cannot be selected as the Codex directory")]
    public void AFileCannotBeSelectedAsTheCodexDirectory()
    {
        var file = workspace.FileFor("not-directory", "test");
        Assert.AreEqual(DefaultHome, new MonitorSettings { CodexHome = file }.ResolveHome(file, Profile));
    }

    [TestMethod(DisplayName = "No directory found returns null rather than a guessed path")]
    public void NoDirectoryFoundReturnsNullRatherThanAGuessedPath()
    {
        var missingProfile = Path.Combine(workspace.Root, "missing-profile");

        Assert.IsNull(new MonitorSettings().ResolveHome(null, missingProfile));
    }

    [TestMethod(DisplayName = "Directory resolution normalizes full path and trailing separator")]
    public void DirectoryResolutionNormalizesFullPathAndTrailingSeparator()
    {
        var settings = new MonitorSettings { CodexHome = ManualHome + Path.DirectorySeparatorChar };

        Assert.AreEqual(ManualHome, settings.ResolveHome(EnvHome, Profile));
    }
}
