using System.Globalization;

namespace CodexTrayMonitor.Core;

public sealed record MonitorPresentation(
    LimitPresentation FiveHour,
    LimitPresentation Weekly,
    string TokensToday,
    string InputText,
    string OutputText,
    string CachedText)
{
    private static readonly string[] TokenUnits = ["", "K", "M", "B", "T", "Q"];

    private static string Detail(string label, IReadOnlyDictionary<DateOnly, long> daily, DateOnly today)
    {
        var value = daily.Count == 0
            ? "—"
            : daily.GetValueOrDefault(today).ToString("N0", CultureInfo.InvariantCulture);
        return label + ": " + value;
    }

    public static MonitorPresentation From(MonitorSnapshot snapshot, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone ?? TimeZoneInfo.Local).DateTime);
        var tokens = snapshot.HasTokenData ? FormatTokens(snapshot.DailyTokens.GetValueOrDefault(today)) : "—";
        return new(
            ProjectLimit(snapshot.Rate, now, timeZone),
            ProjectLimit(snapshot.WeeklyRate, now, timeZone),
            tokens,
            Detail("Input", snapshot.InputTokens, today),
            Detail("Output", snapshot.OutputTokens, today),
            Detail("Cached", snapshot.CachedTokens, today));
    }

    // Reset is a presentation rule, never a fabricated observation written back into the cache.
    public static LimitPresentation ProjectLimit(RateObservation? observation, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var limit = EffectiveLimit.From(observation, now);
        string text;
        if (limit.UsedPercent is null)
        {
            text = "No data";
        }
        else if (limit.Renewed)
        {
            text = "Reset";
        }
        else if (limit.ResetsAt is { } reset)
        {
            text = FormatRenewal(reset - now);
        }
        else
        {
            text = "Reset: —";
        }
        var exactReset = limit.ResetsAt is { } resetAt
            ? "Exact reset: " + TimeZoneInfo.ConvertTime(resetAt, timeZone ?? TimeZoneInfo.Local)
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "Exact reset: —";

        return new(limit.UsedPercent, limit.Renewed, text, exactReset);
    }

    public static string FormatRenewal(TimeSpan remaining)
    {
        if (remaining.TotalMinutes < 1)
        {
            return "Resets in <1m";
        }

        var minutes = (long)remaining.TotalMinutes;
        if (minutes >= 1440)
        {
            return $"Resets in {minutes / 1440}d {minutes % 1440 / 60}h";
        }

        if (minutes >= 60)
        {
            return $"Resets in {minutes / 60}h {minutes % 60}m";
        }

        return $"Resets in {minutes}m";
    }

    public static string FormatTokens(long value)
    {
        decimal scaled = value;
        int unit = 0;
        while (unit < TokenUnits.Length - 1 && Math.Round(scaled, 2, MidpointRounding.AwayFromZero) >= 1000)
        {
            scaled /= 1000;
            unit++;
        }
        var rounded = Math.Round(scaled, 2, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.##", CultureInfo.InvariantCulture) + TokenUnits[unit];
    }
}
