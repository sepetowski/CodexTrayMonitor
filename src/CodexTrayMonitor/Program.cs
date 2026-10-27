using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace CodexTrayMonitor;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var identitySource = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dataIndex = Array.IndexOf(args, "--data-dir");
            if (dataIndex >= 0)
            {
                if (dataIndex + 1 >= args.Length || !args.Contains("--no-autostart"))
                {
                    throw new ArgumentException("--data-dir requires a path and --no-autostart.");
                }

                identitySource += "|" + Path.GetFullPath(args[dataIndex + 1]).ToUpperInvariant();
            }

            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identitySource)))[..24];
            using var mutex = new Mutex(true, @"Local\CodexTrayMonitor-" + identity, out bool first);
            if (!first)
                return 0;

            // A second marker lets Setup/Uninstall detect the running application.
            using var installationMutex = dataIndex < 0 ? new Mutex(false, @"Local\CodexTrayMonitor.Installation") : null;
            var app = new MonitorApplication(args);
            app.Run();
            mutex.ReleaseMutex();
            return 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Startup: {0}", ex);
            MessageBox.Show("Cannot start Codex Tray Monitor.",
                "Codex Tray Monitor", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
