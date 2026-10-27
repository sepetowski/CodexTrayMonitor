using CodexTrayMonitor.Core;
using static CodexTrayMonitor.Tests.TestData;

namespace CodexTrayMonitor.Tests;

internal sealed class TestWorkspace
{
    public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "test-runs", Guid.NewGuid().ToString("N")));

    public TestWorkspace()
    {
        Directory.CreateDirectory(Root);
    }

    public string FileFor(string name, string content)
    {
        var path = Path.Combine(Root, name + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    public async Task WithMonitor(string name, Func<string, string, SessionMonitor, Task> body)
    {
        var home = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        Directory.CreateDirectory(Path.Combine(home, "archived_sessions"));
        var cache = Path.Combine(Root, name + "-cache.json");
        using var monitor = new SessionMonitor(home, cache, Clock(Now, TimeZoneInfo.Utc));
        await body(home, cache, monitor);
    }
}
