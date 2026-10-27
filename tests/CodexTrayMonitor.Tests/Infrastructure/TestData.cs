using System.Text;
using System.Text.Json;
using CodexTrayMonitor.Core;
using Microsoft.Extensions.Time.Testing;

namespace CodexTrayMonitor.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");

    public static FakeTimeProvider Clock(DateTimeOffset now, TimeZoneInfo zone)
    {
        var clock = new FakeTimeProvider(now);
        clock.SetLocalTimeZone(zone);
        return clock;
    }

    public static long? Today(MonitorSnapshot snapshot)
    {
        if (!snapshot.HasTokenData) return null;

        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        return snapshot.DailyTokens.GetValueOrDefault(today);
    }

    public static LimitPresentation Display(MonitorSnapshot snapshot) =>
        MonitorPresentation.From(snapshot, snapshot.CheckedAt, TimeZoneInfo.Utc).FiveHour;

    public static string Event(
        long total = 100,
        long last = 25,
        double used = 42,
        DateTimeOffset? time = null,
        int minutes = 300,
        string id = "codex")
    {
        var timestamp = time ?? Now;

        return JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    total_token_usage = new
                    {
                        input_tokens = total - 10,
                        output_tokens = 10,
                        total_tokens = total,
                        cached_input_tokens = 20,
                        reasoning_output_tokens = 2
                    },
                    last_token_usage = new { total_tokens = last }
                },
                rate_limits = new
                {
                    limit_id = id,
                    primary = new
                    {
                        used_percent = used,
                        window_minutes = minutes,
                        resets_at = timestamp.AddHours(2).ToUnixTimeSeconds()
                    }
                }
            }
        });
    }

    public static string Meta(string id, DateTimeOffset? time = null) =>
        JsonSerializer.Serialize(new
        {
            timestamp = (time ?? Now).ToString("O"),
            type = "session_meta",
            payload = new { id }
        });

    public static FileCursor Parse(params string[] lines)
    {
        var cursor = new FileCursor();
        foreach (var line in lines)
        {
            Assert.IsTrue(TokenEventParser.Apply(Encoding.UTF8.GetBytes(line), cursor, TimeZoneInfo.Utc));
        }

        return cursor;
    }

    public static string Rates(DateTimeOffset timestamp, object? primary, object? secondary, bool unavailable = false) =>
        JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                rate_limits = unavailable ? null : new { limit_id = "codex", primary, secondary }
            }
        });
}
