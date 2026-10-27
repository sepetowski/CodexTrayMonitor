using System.Globalization;
using System.Text.Json;

namespace CodexTrayMonitor.Core;

public static class TokenEventParser
{
    private static readonly string[] RateWindows = ["primary", "secondary"];

    // Mutate only the compact state. Prompt text, tool output and credentials are never retained.
    public static bool Apply(ReadOnlyMemory<byte> line, FileCursor cursor, TimeZoneInfo? timeZone = null, DateTimeOffset? now = null)
    {
        try
        {
            if (line.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                line = line[3..];
            }

            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;
            var type = Text(root, "type");
            if (type is not ("session_meta" or "event_msg"))
            {
                return true;
            }

            if (!Object(root, "payload", out var payload))
            {
                return true;
            }

            if (type == "event_msg" && Text(payload, "type") != "token_count")
            {
                return true;
            }

            if (!DateTimeOffset.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                return false;
            }

            // Reject before mutating any baseline. A future event must not poison
            // subsequent valid observations, including after a cache restart.
            if (now is { } reference && timestamp - reference > TimeSpan.FromMinutes(5))
            {
                return false;
            }

            if (timestamp > cursor.ActivityAt)
            {
                cursor.ActivityAt = timestamp;
            }

            if (type == "session_meta")
            {
                var id = Text(payload, "id");
                if (!string.IsNullOrWhiteSpace(id))
                    cursor.SessionId = id;

                return true;
            }

            if (Object(payload, "info", out var info) && (cursor.UsageAt is null || timestamp >= cursor.UsageAt))
            {
                // Both fields are snapshots. Never add last_token_usage to total_token_usage,
                // and never add successive cumulative total_token_usage snapshots together.
                cursor.Total = Usage(info, "total_token_usage");
                var zone = timeZone ?? TimeZoneInfo.Local;
                cursor.InputTokens.Observe(cursor.Total?.Input, timestamp, zone);
                cursor.OutputTokens.Observe(cursor.Total?.Output, timestamp, zone);
                cursor.CachedTokens.Observe(cursor.Total?.CachedInput, timestamp, zone);
                cursor.Last = Usage(info, "last_token_usage");
                cursor.UsageAt = timestamp;
                if (cursor.Total?.Total is { } total)
                {
                    TokenAccounting.Observe(cursor, total, timestamp, timeZone ?? TimeZoneInfo.Local);
                }
            }

            if (payload.TryGetProperty("rate_limits", out var rates))
            {
                if (rates.ValueKind != JsonValueKind.Object)
                {
                    var unavailable = new RateObservation(timestamp, null, null, null, "No limit data");
                    cursor.Rate = Newer(cursor.Rate, unavailable);
                    cursor.WeeklyRate = Newer(cursor.WeeklyRate, unavailable);
                }
                else
                {
                    var id = Text(rates, "limit_id");
                    // Separate model/feature quotas must not replace the general Codex quota.
                    if (id is not (null or "codex"))
                    {
                        return true;
                    }

                    cursor.Rate = Newer(cursor.Rate, ParseRate(rates, timestamp, 300));
                    cursor.WeeklyRate = Newer(cursor.WeeklyRate, ParseRate(rates, timestamp, 10080));
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static RateObservation? Newer(RateObservation? previous, RateObservation? next) =>
        next is not null && (previous is null || next.ObservedAt >= previous.ObservedAt) ? next : previous;

    private static RateObservation? ParseRate(JsonElement rates, DateTimeOffset timestamp, int minutes)
    {
        var plan = Text(rates, "plan_type");
        foreach (var key in RateWindows)
        {
            if (!Object(rates, key, out var window) || Number(window, "window_minutes") != minutes)
            {
                continue;
            }

            var used = Number(window, "used_percent");
            if (used is null || !double.IsFinite(used.Value) || used < 0 || used > 100)
            {
                return new(timestamp, null, null, plan, "Invalid limit percentage");
            }

            DateTimeOffset? reset = null;
            if (window.TryGetProperty("resets_at", out var resetValue) && resetValue.ValueKind != JsonValueKind.Null)
            {
                if (Integer(window, "resets_at") is not { } epoch)
                {
                    return new(timestamp, null, null, plan, "Invalid reset time");
                }

                try
                {
                    reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return new(timestamp, null, null, plan, "Invalid reset time");
                }
            }

            return new(timestamp, used, reset, plan, null);
        }

        // A partial payload does not erase the other window's last observation.
        return null;
    }

    private static TokenUsage? Usage(JsonElement info, string name)
    {
        if (!Object(info, name, out var value))
            return null;

        return new(
            Integer(value, "input_tokens"),
            Integer(value, "cached_input_tokens"),
            Integer(value, "output_tokens"),
            Integer(value, "reasoning_output_tokens"),
            Integer(value, "total_tokens"));
    }

    private static string? Text(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null;
    }

    private static double? Number(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.Number
            && item.TryGetDouble(out var number)
                ? number
                : null;
    }

    private static long? Integer(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.Number
            && item.TryGetInt64(out var number)
            && number >= 0
                ? number
                : null;
    }

    private static bool Object(JsonElement value, string name, out JsonElement item)
    {
        item = default;
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out item)
            && item.ValueKind == JsonValueKind.Object;
    }
}
