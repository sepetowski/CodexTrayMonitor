using CodexTrayMonitor.Core;

namespace CodexTrayMonitor.Tests;

[TestClass]
public sealed class JsonStorageTests
{
    private readonly TestWorkspace workspace = new();

    [TestMethod(DisplayName = "Malformed cache falls back cleanly")]
    public void MalformedCacheFallsBackCleanly()
    {
        var path = Path.Combine(workspace.Root, "bad-cache.json");
        File.WriteAllText(path, "not json");
        Assert.IsNull(JsonStorage.Read<CursorCache>(path));
        File.WriteAllText(path, "{}");
        Assert.IsNull(JsonStorage.Read<CursorCache>(path));
    }

    [TestMethod(DisplayName = "Oversized JSON is rejected before deserialization")]
    public void OversizedJSONIsRejectedBeforeDeserialization()
    {
        var path = Path.Combine(workspace.Root, "large-cache.json");
        File.WriteAllText(path, "{\"Version\":4}");
        Assert.IsNull(JsonStorage.Read<CursorCache>(path, 4));
        Assert.AreEqual(4, JsonStorage.Read<CursorCache>(path)!.Version);
    }
}
