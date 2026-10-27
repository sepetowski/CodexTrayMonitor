namespace CodexTrayMonitor.Core;

public sealed class FileCursor
{
    public string Path { get; set; } = "";
    public long Offset { get; set; }
    public long CreationTicks { get; set; }
    public string PrefixHash { get; set; } = "";
    public string TailHash { get; set; } = "";
    public bool DiscardingLine { get; set; }
    public string? SessionId { get; set; }
    public DateTimeOffset ActivityAt { get; set; }
    public DateTimeOffset? UsageAt { get; set; }
    public TokenUsage? Total { get; set; }
    public TokenUsage? Last { get; set; }
    public RateObservation? Rate { get; set; }
    public RateObservation? WeeklyRate { get; set; }
    public long? TokenBaseline { get; set; }
    public DateTimeOffset? TokenObservedAt { get; set; }
    public HashSet<long> TotalsAtTokenTimestamp { get; set; } = [];
    public long TokenObservationCount { get; set; }
    public Dictionary<DateOnly, long> DailyTokens { get; set; } = [];
    public long InvalidLines { get; set; }

    public TokenCounter InputTokens { get; set; } = new();
    public TokenCounter OutputTokens { get; set; } = new();
    public TokenCounter CachedTokens { get; set; } = new();
}
