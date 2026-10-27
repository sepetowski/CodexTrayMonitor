namespace CodexTrayMonitor.Core;

public sealed class TokenCounter
{
    public long? Baseline { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public HashSet<long> ValuesAtTimestamp { get; set; } = [];
    public Dictionary<DateOnly, long> Daily { get; set; } = [];

    public void Observe(long? value, DateTimeOffset timestamp, TimeZoneInfo zone)
    {
        if (value is not { } total || total < 0 || timestamp < ObservedAt)
            return;

        if (timestamp != ObservedAt)
            ValuesAtTimestamp.Clear();

        if (!ValuesAtTimestamp.Add(total))
            return;

        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone).DateTime);
        Daily[day] = TokenAccounting.Add(Daily.GetValueOrDefault(day), Math.Max(0, total - (Baseline ?? 0)));
        Baseline = total;
        ObservedAt = timestamp;
    }

    internal bool IsValid => Baseline is not < 0
        && Daily is not null
        && Daily.Values.All(value => value >= 0)
        && ValuesAtTimestamp is not null
        && ValuesAtTimestamp.All(value => value >= 0);
}
