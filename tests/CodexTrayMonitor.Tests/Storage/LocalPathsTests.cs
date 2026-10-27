using CodexTrayMonitor.Core;

namespace CodexTrayMonitor.Tests;

[TestClass]
public sealed class LocalPathsTests
{

    [TestMethod(DisplayName = "Network paths are rejected")]
    public void NetworkPathsAreRejected()
    {
        foreach (var path in new[]
        {
            @"\\server\share\codex",
            "https://example.com",
            "//server/share"
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => LocalPaths.ValidateHome(path));
        }
    }

    [TestMethod(DisplayName = "Environment-expanded network paths are rejected")]
    public void EnvironmentExpandedNetworkPathsAreRejected()
    {
        const string variable = "CODEX_TRAY_TEST_NETWORK_PATH";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, @"\\server\share\codex");
            Assert.ThrowsExactly<ArgumentException>(
                () => LocalPaths.ValidateHome("%" + variable + "%"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
