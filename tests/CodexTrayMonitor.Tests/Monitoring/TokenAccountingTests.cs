using System.Text;
using System.Text.Json;
using CodexTrayMonitor.Core;

namespace CodexTrayMonitor.Tests;

[TestClass]
public sealed class TokenAccountingTests
{
    [TestMethod(DisplayName = "Daily deltas ignore repetitions and use a new baseline after a counter drop")]
    public void DailyDeltasIgnoreRepetitionsAndUseANewBaselineAfterACounterDrop()
    {
        var cursor = Parse(
            Event(100),
            Event(100),
            Event(140, time: Now.AddMinutes(1)),
            Event(30, time: Now.AddMinutes(2)),
            Event(50, time: Now.AddMinutes(3)),
            Event(900, time: Now.AddMinutes(-1)));

        Assert.AreEqual(160L, cursor.DailyTokens.Values.Sum());
        Assert.AreEqual(50L, cursor.TokenBaseline);
    }

    [TestMethod(DisplayName = "Missing total never adds last usage or loses the previous counter baseline")]
    public void MissingTotalNeverAddsLastUsageOrLosesThePreviousCounterBaseline()
    {
        var missing = JsonSerializer.Serialize(new
        {
            timestamp = Now.AddMinutes(1).ToString("O"),
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new { last_token_usage = new { total_tokens = 500 } }
            }
        });

        var cursor = Parse(Event(100), missing, Event(150, time: Now.AddMinutes(2)));
        Assert.AreEqual(150L, cursor.DailyTokens.Values.Sum());
        Assert.AreEqual(0, Parse(missing).DailyTokens.Count);
    }

    [TestMethod(DisplayName = "Nonconsecutive repeats at an equal timestamp cannot count an increment twice")]
    public void NonconsecutiveRepeatsAtAnEqualTimestampCannotCountAnIncrementTwice()
    {
        var cursor = Parse(Event(100), Event(140), Event(100), Event(140));
        Assert.AreEqual(140L, cursor.DailyTokens.Values.Sum());
        Assert.AreEqual(140L, cursor.TokenBaseline);
        Assert.AreEqual(2L, cursor.TokenObservationCount);
        var cached = JsonSerializer.Deserialize<FileCursor>(JsonSerializer.Serialize(cursor))!;
        TokenEventParser.Apply(Encoding.UTF8.GetBytes(Event(100)), cached, TimeZoneInfo.Utc);
        TokenEventParser.Apply(Encoding.UTF8.GetBytes(Event(140)), cached, TimeZoneInfo.Utc);
        Assert.AreEqual(140L, cached.DailyTokens.Values.Sum());
        Assert.AreEqual(140L, cached.TokenBaseline);
    }

    [TestMethod(DisplayName = "Local date and DST transitions do not duplicate or move daily token increments")]
    public void LocalDateAndDSTTransitionsDoNotDuplicateOrMoveDailyTokenIncrements()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var cursor = new FileCursor();
        var times = new[]
        {
            "2026-10-24T21:59:00Z",
            "2026-10-24T22:01:00Z",
            "2026-10-25T00:30:00Z",
            "2026-10-25T01:30:00Z"
        };
        for (var i = 0; i < times.Length; i++)
        {
            var record = Event(100 + i * 25, time: DateTimeOffset.Parse(times[i]));
            Assert.IsTrue(TokenEventParser.Apply(Encoding.UTF8.GetBytes(record), cursor, zone));
        }

        Assert.AreEqual(100L, cursor.DailyTokens[new(2026, 10, 24)]);
        Assert.AreEqual(75L, cursor.DailyTokens[new(2026, 10, 25)]);
        var spring = new FileCursor();
        var springRecords = new[]
        {
            ("2026-03-29T00:30:00Z", 100),
            ("2026-03-29T01:30:00Z", 125)
        };

        foreach (var (time, total) in springRecords)
        {
            var record = Event(total, time: DateTimeOffset.Parse(time));
            Assert.IsTrue(TokenEventParser.Apply(Encoding.UTF8.GetBytes(record), spring, zone));
        }

        Assert.AreEqual(125L, spring.DailyTokens[new(2026, 3, 29)]);
    }

    [TestMethod(DisplayName = "Token details survive midnight, missing fields, counter drops and cached repeats")]
    public void TokenDetailsSurviveMidnightMissingFieldsCounterDropsAndCachedRepeats()
    {
        var cursor = Parse(Event(100), Event(140), Event(100), Event(140));
        Assert.AreEqual(130L, cursor.InputTokens.Daily.Values.Sum());
        Assert.AreEqual(10L, cursor.OutputTokens.Daily.Values.Sum());
        Assert.AreEqual(20L, cursor.CachedTokens.Daily.Values.Sum());
        cursor = JsonSerializer.Deserialize<FileCursor>(JsonSerializer.Serialize(cursor))!;
        TokenEventParser.Apply(Encoding.UTF8.GetBytes(Event(100)), cursor, TimeZoneInfo.Utc);
        Assert.AreEqual(130L, cursor.InputTokens.Daily.Values.Sum());
        TokenEventParser.Apply(Encoding.UTF8.GetBytes(Event(160, time: Now.AddDays(1))), cursor, TimeZoneInfo.Utc);
        Assert.AreEqual(20L, cursor.InputTokens.Daily[new(2026, 10, 19)]);
        Assert.AreEqual(0L, cursor.OutputTokens.Daily[new(2026, 10, 19)]);
        TokenEventParser.Apply(Encoding.UTF8.GetBytes(Event(30, time: Now.AddDays(1).AddMinutes(1))), cursor, TimeZoneInfo.Utc);
        TokenEventParser.Apply(Encoding.UTF8.GetBytes(Event(50, time: Now.AddDays(1).AddMinutes(2))), cursor, TimeZoneInfo.Utc);
        Assert.AreEqual(40L, cursor.InputTokens.Daily[new(2026, 10, 19)]);
        var missing = Event(180, time: Now.AddDays(2)).Replace("\"input_tokens\":170,", "");
        TokenEventParser.Apply(Encoding.UTF8.GetBytes(missing), cursor, TimeZoneInfo.Utc);
        Assert.AreEqual(40L, cursor.InputTokens.Baseline);
    }
}
