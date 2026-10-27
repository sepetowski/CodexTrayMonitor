namespace CodexTrayMonitor.Core;

internal static class TokenAccounting
{
    public static void Observe(FileCursor cursor, long total, DateTimeOffset timestamp, TimeZoneInfo timeZone)
    {
        if (cursor.TokenObservedAt is { } previousTimestamp && timestamp < previousTimestamp)
        {
            return;
        }

        if (cursor.TokenObservedAt != timestamp)
        {
            cursor.TotalsAtTokenTimestamp.Clear();
        }

        // Equal-timestamp repeats must not reset the baseline and count an increment twice.
        if (!cursor.TotalsAtTokenTimestamp.Add(total))
        {
            return;
        }

        var localDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, timeZone).DateTime);
        var tokenIncrease = Math.Max(0, total - (cursor.TokenBaseline ?? 0));
        cursor.DailyTokens[localDay] = Add(cursor.DailyTokens.GetValueOrDefault(localDay), tokenIncrease);
        cursor.TokenObservationCount = Add(cursor.TokenObservationCount, 1);
        cursor.TokenBaseline = total;
        cursor.TokenObservedAt = timestamp;
    }

    public static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}
