namespace CodexTrayMonitor.Core;

public sealed record MonitorSnapshot(
    DateTimeOffset CheckedAt,
    RateObservation? Rate,
    RateObservation? WeeklyRate,
    IReadOnlyDictionary<DateOnly, long> DailyTokens,
    bool HasTokenData,
    int FileCount,
    long InvalidLines,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyDictionary<DateOnly, long> InputTokens { get; init; } = new Dictionary<DateOnly, long>();
    public IReadOnlyDictionary<DateOnly, long> OutputTokens { get; init; } = new Dictionary<DateOnly, long>();
    public IReadOnlyDictionary<DateOnly, long> CachedTokens { get; init; } = new Dictionary<DateOnly, long>();
    public static MonitorSnapshot Empty { get; } = new(
        DateTimeOffset.MinValue, null, null, new Dictionary<DateOnly, long>(), false, 0, 0, []);
}
