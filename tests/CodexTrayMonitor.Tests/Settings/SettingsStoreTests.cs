using System.Text.Json;
using CodexTrayMonitor.Core;

namespace CodexTrayMonitor.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    private readonly TestWorkspace workspace = new();

    private string SettingsRoot => Directory.CreateDirectory(Path.Combine(workspace.Root, "settings")).FullName;

    private string ManualHome => Directory.CreateDirectory(Path.Combine(SettingsRoot, "manual")).FullName;

    [TestMethod(DisplayName = "Old stale setting is ignored when loading settings")]
    public void OldStaleSettingIsIgnoredWhenLoadingSettings()
    {
        var settings = JsonSerializer.Deserialize<MonitorSettings>("{\"AutoStart\":false,\"StaleAfterMinutes\":1}")!;
        Assert.IsFalse(settings.AutoStart);
        Assert.DoesNotContain("StaleAfterMinutes", JsonSerializer.Serialize(settings));
    }

    [TestMethod(DisplayName = "Legacy refresh setting is ignored and removed on save")]
    public void LegacyRefreshSettingIsIgnoredAndRemovedOnSave()
    {
        var path = Path.Combine(SettingsRoot, "legacy.json");
        File.WriteAllText(path, "{\"AutoStart\":false,\"RefreshSeconds\":15}");
        var loaded = SettingsStore.Load(path);
        Assert.AreEqual(AppearanceMode.System, loaded.Appearance);

        Assert.IsFalse(loaded.AutoStart);
        SettingsStore.Save(path, loaded);
        Assert.DoesNotContain("RefreshSeconds", File.ReadAllText(path));
    }

    [TestMethod(DisplayName = "Settings round trip persists all fields and automatic detection")]
    public void SettingsRoundTripPersistsAllFieldsAndAutomaticDetection()
    {
        var path = Path.Combine(SettingsRoot, "roundtrip.json");
        SettingsStore.Save(path, new()
        {
            CodexHome = ManualHome,
            AutoStart = false,
            Appearance = AppearanceMode.Light
        });

        var loaded = SettingsStore.Load(path);
        Assert.AreEqual(ManualHome, loaded.CodexHome);
        Assert.IsFalse(loaded.AutoStart);
        Assert.AreEqual(AppearanceMode.Light, loaded.Appearance);

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.AreEqual(3, json.RootElement.EnumerateObject().Count());

        SettingsStore.Save(path, new());
        Assert.IsNull(SettingsStore.Load(path).CodexHome);
        Assert.IsFalse(File.Exists(path + ".tmp"));
    }

    [TestMethod(DisplayName = "Invalid settings cannot overwrite saved settings")]
    public void InvalidSettingsCannotOverwriteSavedSettings()
    {
        var path = Path.Combine(SettingsRoot, "validate.json");
        SettingsStore.Save(path, new());
        var before = File.ReadAllText(path);
        var invalidSettings = new[]
        {
            new MonitorSettings { Appearance = (AppearanceMode)99 },
            new MonitorSettings { CodexHome = Path.Combine(workspace.Root, "missing") }
        };

        foreach (var invalid in invalidSettings)
        {
            Assert.ThrowsExactly<ArgumentException>(() => SettingsStore.Save(path, invalid));
            Assert.AreEqual(before, File.ReadAllText(path));
        }
    }

    [TestMethod(DisplayName = "Failed atomic settings write preserves previous file")]
    public void FailedAtomicSettingsWritePreservesPreviousFile()
    {
        var path = Path.Combine(SettingsRoot, "locked.json");
        SettingsStore.Save(path, new());
        var before = File.ReadAllText(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.Throws<Exception>(
                () => SettingsStore.Save(path, new() { Appearance = AppearanceMode.Light }));
            Assert.IsTrue(error is IOException or UnauthorizedAccessException);
        }

        Assert.AreEqual(before, File.ReadAllText(path));
        Assert.IsFalse(File.Exists(path + ".tmp"));
    }

    [TestMethod(DisplayName = "Invalid persisted options recover to defaults")]
    public void InvalidPersistedOptionsRecoverToDefaults()
    {
        var path = Path.Combine(SettingsRoot, "invalid.json");
        File.WriteAllText(path, "{\"Appearance\":999}");
        var loaded = SettingsStore.Load(path);
        Assert.AreEqual(AppearanceMode.System, loaded.Appearance);

        File.WriteAllText(path, "broken json");
        Assert.AreEqual(AppearanceMode.System, SettingsStore.Load(path).Appearance);
    }

    [TestMethod(DisplayName = "New settings opt out of startup and existing choice survives")]
    public void NewSettingsOptOutOfStartupAndExistingChoiceSurvives()
    {
        Assert.IsFalse(new MonitorSettings().AutoStart);
        var path = Path.Combine(SettingsRoot, "startup.json");
        Assert.IsFalse(SettingsStore.Load(path).AutoStart);
        File.WriteAllText(path, "{\"AutoStart\":true}");
        Assert.IsTrue(SettingsStore.Load(path).AutoStart);
        File.WriteAllText(path, "{\"AutoStart\":false}");
        Assert.IsFalse(SettingsStore.Load(path).AutoStart);
    }
}
