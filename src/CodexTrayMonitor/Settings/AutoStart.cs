using Microsoft.Win32;

namespace CodexTrayMonitor;

internal static class AutoStart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexTrayMonitor";
    private const string ApprovalPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    internal static AutoStartState Capture()
    {
        using var run = Registry.CurrentUser.OpenSubKey(KeyPath);
        using var approval = Registry.CurrentUser.OpenSubKey(ApprovalPath);
        var command = run?.GetValue(ValueName);
        var status = approval?.GetValue(ValueName);
        return new(
            command,
            command is null ? RegistryValueKind.String : run!.GetValueKind(ValueName),
            status,
            status is null ? RegistryValueKind.Binary : approval!.GetValueKind(ValueName));
    }

    public static bool IsEnabled()
    {
        var state = Capture();
        return state.Run is string { Length: > 0 }
            && (state.Approval is not byte[] { Length: > 0 } bytes || bytes[0] is 2 or 6);
    }

    internal static void Restore(AutoStartState state)
    {
        using var run = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (state.Run is null)
        {
            run.DeleteValue(ValueName, false);
        }
        else
        {
            run.SetValue(ValueName, state.Run, state.RunKind);
        }

        using var approval = Registry.CurrentUser.CreateSubKey(ApprovalPath, true);
        if (state.Approval is null)
        {
            approval.DeleteValue(ValueName, false);
        }
        else
        {
            approval.SetValue(ValueName, state.Approval, state.ApprovalKind);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot locate the application executable.");
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Autostart requires running CodexTrayMonitor.exe directly.");
            }

            key.SetValue(ValueName, "\"" + executable + "\" --startup", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
        // Remove only our stale Task Manager override when explicitly enabling/disabling.
        using var approval = Registry.CurrentUser.OpenSubKey(ApprovalPath, true);
        approval?.DeleteValue(ValueName, false);
    }
}
