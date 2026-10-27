using System.Text;
using System.Text.Json;
using CodexTrayMonitor.Core;

namespace CodexTrayMonitor.Tests;

[TestClass]
public sealed class TokenEventParserTests
{

    [TestMethod(DisplayName = "Cumulative and repeated snapshots are not added")]
    public void CumulativeAndRepeatedSnapshotsAreNotAdded()
    {
        var cursor = Parse(Event(100, 25), Event(100, 25), Event(140, 40));

        Assert.AreEqual(140L, cursor.Total!.Total);
        Assert.AreEqual(40L, cursor.Last!.Total);
        Assert.AreEqual(42d, cursor.Rate!.UsedPercent);
    }

    [TestMethod(DisplayName = "A decreasing total replaces the previous snapshot")]
    public void ADecreasingTotalReplacesThePreviousSnapshot()
    {
        var cursor = Parse(Event(100), Event(30));

        Assert.AreEqual(30L, cursor.Total!.Total);
    }

    [TestMethod(DisplayName = "No percentage inferred from token usage")]
    public void NoPercentageInferredFromTokenUsage()
    {
        const string record = """
            {"timestamp":"2026-01-01T00:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"total_tokens":25}}}}
            """;
        var cursor = Parse(record);

        Assert.IsNull(cursor.Rate);
        Assert.IsNull(cursor.Total);
        Assert.AreEqual(25L, cursor.Last!.Total);
    }

    [TestMethod(DisplayName = "Unknown window is unavailable")]
    public void UnknownWindowIsUnavailable()
    {
        Assert.IsNull(Parse(Event(minutes: 240)).Rate);
    }

    [TestMethod(DisplayName = "Model-specific quota cannot replace Codex quota")]
    public void ModelSpecificQuotaCannotReplaceCodexQuota()
    {
        var cursor = Parse(Event(), Event(used: 99, id: "codex_other"));

        Assert.AreEqual(42d, cursor.Rate!.UsedPercent);
    }

    [TestMethod(DisplayName = "Out-of-order events cannot replace newer observations")]
    public void OutOfOrderEventsCannotReplaceNewerObservations()
    {
        var cursor = Parse(Event(), Event(500, time: Now.AddMinutes(-1)));

        Assert.AreEqual(100L, cursor.Total!.Total);
    }

    [TestMethod(DisplayName = "Invalid percentages are unavailable")]
    public void InvalidPercentagesAreUnavailable()
    {
        Assert.IsNull(Parse(Event(used: 101)).Rate!.UsedPercent);
        Assert.IsNull(Parse(Event(used: -1)).Rate!.UsedPercent);
    }

    [TestMethod(DisplayName = "Null rate limits invalidate previous observation")]
    public void NullRateLimitsInvalidatePreviousObservation()
    {
        var record = JsonSerializer.Serialize(new
        {
            timestamp = Now.AddSeconds(1).ToString("O"),
            type = "event_msg",
            payload = new { type = "token_count", rate_limits = (object?)null }
        });

        Assert.IsNull(Parse(Event(), record).Rate!.UsedPercent);
    }

    [TestMethod(DisplayName = "300-minute secondary window is accepted")]
    public void ThreeHundredMinuteSecondaryWindowIsAccepted()
    {
        var record = Event().Replace("\"primary\"", "\"secondary\"");

        Assert.AreEqual(42d, Parse(record).Rate!.UsedPercent);
    }

    [TestMethod(DisplayName = "Non-token event content is not retained")]
    public void NonTokenEventContentIsNotRetained()
    {
        const string record = """{"type":"response_item","payload":{"secret":"not-a-token-event"}}""";
        var cursor = Parse(record);

        Assert.IsNull(cursor.Total);
        Assert.IsNull(cursor.Rate);
        Assert.DoesNotContain("not-a-token-event", JsonSerializer.Serialize(cursor));
    }

    [TestMethod(DisplayName = "Both windows are independent and can exchange primary/secondary positions")]
    public void BothWindowsAreIndependentAndCanExchangePrimarySecondaryPositions()
    {
        var weekly = new { used_percent = 68, window_minutes = 10080, resets_at = Now.AddDays(3).ToUnixTimeSeconds() };
        var fiveHour = new { used_percent = 32, window_minutes = 300, resets_at = Now.AddHours(2).ToUnixTimeSeconds() };
        var cursor = Parse(Rates(Now, weekly, fiveHour), Event(used: 40, time: Now.AddMinutes(1)));

        Assert.AreEqual(40d, cursor.Rate!.UsedPercent);
        Assert.AreEqual(68d, cursor.WeeklyRate!.UsedPercent);
        Assert.AreEqual(Now, cursor.WeeklyRate.ObservedAt);
    }

    [TestMethod(DisplayName = "Unknown windows and model quotas preserve both existing limits")]
    public void UnknownWindowsAndModelQuotasPreserveBothExistingLimits()
    {
        var cursor = Parse(
            Event(),
            Event(minutes: 10080, used: 68),
            Event(minutes: 240, time: Now.AddMinutes(1)),
            Event(minutes: 10080, used: 99, time: Now.AddMinutes(2), id: "model"));

        Assert.AreEqual(42d, cursor.Rate!.UsedPercent);
        Assert.AreEqual(68d, cursor.WeeklyRate!.UsedPercent);
    }

    [TestMethod(DisplayName = "Null limits invalidate both windows without touching tokens")]
    public void NullLimitsInvalidateBothWindowsWithoutTouchingTokens()
    {
        var cursor = Parse(Event(), Event(minutes: 10080), Rates(Now.AddMinutes(1), null, null, unavailable: true));
        Assert.IsNull(cursor.Rate!.UsedPercent);
        Assert.IsNull(cursor.WeeklyRate!.UsedPercent);
        Assert.AreEqual(100L, cursor.DailyTokens.Values.Sum());
    }

    [TestMethod(DisplayName = "Bad window data does not erase the valid other window")]
    public void BadWindowDataDoesNotEraseTheValidOtherWindow()
    {
        var cursor = Parse(Rates(Now,
            new { used_percent = 101, window_minutes = 300 },
            new { used_percent = 68, window_minutes = 10080 }));
        Assert.IsNull(cursor.Rate!.UsedPercent);
        Assert.AreEqual(68d, cursor.WeeklyRate!.UsedPercent);

        cursor = Parse(Rates(Now,
            new { used_percent = 32, window_minutes = 300, resets_at = "invalid" },
            new { used_percent = 68, window_minutes = 10080 }));
        Assert.IsNull(cursor.Rate!.UsedPercent);
        Assert.AreEqual(68d, cursor.WeeklyRate!.UsedPercent);
    }

    [TestMethod(DisplayName = "Older events are compared independently for each window")]
    public void OlderEventsAreComparedIndependentlyForEachWindow()
    {
        var cursor = Parse(
            Event(time: Now.AddMinutes(2)),
            Event(minutes: 10080, time: Now.AddMinutes(1)),
            Event(used: 90, time: Now),
            Event(used: 90, minutes: 10080, time: Now));

        Assert.AreEqual(42d, cursor.Rate!.UsedPercent);
        Assert.AreEqual(42d, cursor.WeeklyRate!.UsedPercent);
    }

    [TestMethod(DisplayName = "Malformed shapes and deeply nested records cannot stop parsing")]
    public void MalformedShapesAndDeeplyNestedRecordsCannotStopParsing()
    {
        var cursor = new FileCursor();
        var records = new[]
        {
            "null", "[]", "42", "{}", "{broken", "{\"type\":\"event_msg\",\"payload\":[]}",
            new string('[', 100) + "0" + new string(']', 100),
            Event().Replace("\"total_tokens\":100", "\"total_tokens\":999999999999999999999999999999"),
            Rates(Now, new { used_percent = 20, window_minutes = 300, resets_at = long.MaxValue }, null)
        };

        foreach (var record in records)
            TokenEventParser.Apply(Encoding.UTF8.GetBytes(record), cursor, TimeZoneInfo.Utc, Now);

        Assert.IsTrue(TokenEventParser.Apply(
            Encoding.UTF8.GetBytes(Event(150, time: Now.AddMinutes(1))), cursor, TimeZoneInfo.Utc, Now));
        Assert.AreEqual(150L, cursor.TokenBaseline);
        Assert.AreEqual(42d, cursor.Rate!.UsedPercent);
    }
}
