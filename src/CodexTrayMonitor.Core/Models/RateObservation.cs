namespace CodexTrayMonitor.Core;

public sealed record RateObservation(
    DateTimeOffset ObservedAt,
    double? UsedPercent,
    DateTimeOffset? ResetsAt,
    string? Plan,
    string? Problem);
