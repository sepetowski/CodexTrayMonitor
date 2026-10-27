using Microsoft.Win32;

namespace CodexTrayMonitor;

internal sealed record AutoStartState(
    object? Run,
    RegistryValueKind RunKind,
    object? Approval,
    RegistryValueKind ApprovalKind);
