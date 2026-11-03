using CodexTrayMonitor.Core;

namespace CodexTrayMonitor;

internal sealed record UiMessage(
    string Kind,
    MonitorPresentation? Presentation = null,
    MonitorSettings? Settings = null,
    string[]? Warnings = null,
    string? Error = null,
    int? CursorX = null,
    int? CursorY = null);
