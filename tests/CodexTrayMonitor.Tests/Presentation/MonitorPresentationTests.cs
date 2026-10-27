using System.Globalization;
using CodexTrayMonitor.Core;

namespace CodexTrayMonitor.Tests;

[TestClass]
public sealed class MonitorPresentationTests
{
    [TestMethod(DisplayName = "Percentages use a decimal point regardless of system culture")]
    public void PercentagesUseADecimalPointRegardlessOfSystemCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            Assert.AreEqual("42.5%", new LimitPresentation(42.5, false, "", "").PercentText);
            Assert.AreEqual("—", new LimitPresentation(null, false, "", "").PercentText);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [TestMethod(DisplayName = "Reset boundary changes only the effective limit")]
    public void ResetBoundaryChangesOnlyTheEffectiveLimit()
    {
        var rate = new RateObservation(Now.AddHours(-1), 55, Now, null, null);
        Assert.AreEqual(55d, MonitorPresentation.ProjectLimit(rate, Now.AddTicks(-1)).UsedPercent);
        var reset = MonitorPresentation.ProjectLimit(rate, Now);
        Assert.AreEqual(0d, reset.UsedPercent);
        Assert.IsTrue(reset.Renewed);
        Assert.AreEqual("Reset", reset.RenewalText);
        Assert.AreEqual("Reset", MonitorPresentation.ProjectLimit(rate, Now.AddDays(10)).RenewalText);
        Assert.AreEqual(55d, rate.UsedPercent);
    }

    [TestMethod(DisplayName = "New usage after reset takes precedence even with an old reset timestamp")]
    public void NewUsageAfterResetTakesPrecedenceEvenWithAnOldResetTimestamp()
    {
        var rate = new RateObservation(Now.AddMinutes(1), 7, Now, null, null);
        var display = MonitorPresentation.ProjectLimit(rate, Now.AddMinutes(2));
        Assert.AreEqual(7d, display.UsedPercent);
        Assert.IsFalse(display.Renewed);
        Assert.AreEqual("Reset: —", display.RenewalText);
        Assert.AreEqual("Reset: —", MonitorPresentation.ProjectLimit(rate with { ResetsAt = null }, Now.AddMinutes(2)).RenewalText);
    }

    [TestMethod(DisplayName = "Independent resets and future timestamp protection")]
    public void IndependentResetsAndFutureTimestampProtection()
    {
        var snapshot = MonitorSnapshot.Empty with
        {
            Rate = new(Now.AddHours(-1), 55, Now, null, null),
            WeeklyRate = new(Now.AddHours(-1), 68, Now.AddDays(2), null, null)
        };
        var view = MonitorPresentation.From(snapshot, Now, TimeZoneInfo.Utc);
        Assert.AreEqual(0d, view.FiveHour.UsedPercent);
        Assert.AreEqual(68d, view.Weekly.UsedPercent);
        Assert.IsNull(MonitorPresentation.ProjectLimit(
            snapshot.Rate with { ObservedAt = Now.AddMinutes(6) }, Now).UsedPercent);
        Assert.AreEqual("No data", MonitorPresentation.ProjectLimit(null, Now).RenewalText);
    }

    [TestMethod(DisplayName = "Midnight keeps the prior counter baseline and changes the displayed day without polling")]
    public void MidnightKeepsThePriorCounterBaselineAndChangesTheDisplayedDayWithoutPolling()
    {
        var midnight = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
        var cursor = Parse(Event(100, time: midnight.AddMinutes(-1)), Event(125, time: midnight.AddMinutes(1)));
        Assert.AreEqual(100L, cursor.DailyTokens[new(2026, 10, 18)]);
        Assert.AreEqual(25L, cursor.DailyTokens[new(2026, 10, 19)]);
        var snapshot = MonitorSnapshot.Empty with
        {
            HasTokenData = true,
            DailyTokens = cursor.DailyTokens
        };
        Assert.AreEqual("100", MonitorPresentation.From(snapshot, midnight.AddMinutes(-1), TimeZoneInfo.Utc).TokensToday);
        Assert.AreEqual("25", MonitorPresentation.From(snapshot, midnight, TimeZoneInfo.Utc).TokensToday);
        Assert.AreEqual("0", MonitorPresentation.From(snapshot, midnight.AddDays(1), TimeZoneInfo.Utc).TokensToday);
    }

    [TestMethod(DisplayName = "Friendly formats cover units, rounding boundaries and renewal intervals")]
    public void FriendlyFormatsCoverUnitsRoundingBoundariesAndRenewalIntervals()
    {
        var cases = new (long Value, string Expected)[]
        {
            (0, "0"),
            (999, "999"),
            (1240, "1.24K"),
            (1240000, "1.24M"),
            (1000000, "1M"),
            (999999, "1M"),
            (long.MaxValue, "9223.37Q")
        };

        foreach (var (value, expected) in cases)
        {
            Assert.AreEqual(expected, MonitorPresentation.FormatTokens(value));
        }

        Assert.AreEqual("Resets in 2h 14m", MonitorPresentation.FormatRenewal(TimeSpan.FromMinutes(134)));
        Assert.AreEqual("Resets in 3d 8h", MonitorPresentation.FormatRenewal(TimeSpan.FromHours(80)));
        Assert.AreEqual("Resets in <1m", MonitorPresentation.FormatRenewal(TimeSpan.FromSeconds(59)));
        Assert.AreEqual("Resets in 1m", MonitorPresentation.FormatRenewal(TimeSpan.FromMinutes(1)));
    }

    [TestMethod(DisplayName = "Exact reset uses the selected time zone and handles missing and expired dates")]
    public void ExactResetUsesTheSelectedTimeZoneAndHandlesMissingAndExpiredDates()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        var rate = new RateObservation(Now, 55, Now.AddHours(2), null, null);
        var snapshot = MonitorSnapshot.Empty with { Rate = rate, WeeklyRate = rate with { ResetsAt = Now.AddDays(10) } };
        var view = MonitorPresentation.From(snapshot, Now, zone);
        Assert.AreEqual("Exact reset: 2026-10-18 16:00", view.FiveHour.ExactResetText);
        Assert.AreEqual("Exact reset: 2026-10-28 13:00", view.Weekly.ExactResetText);
        Assert.AreEqual(view.FiveHour.ExactResetText, MonitorPresentation.ProjectLimit(rate, Now.AddHours(2), zone).ExactResetText);
        Assert.AreEqual("Exact reset: 2026-10-18 14:00", MonitorPresentation.ProjectLimit(rate, Now, TimeZoneInfo.Utc).ExactResetText);
        Assert.AreEqual("Exact reset: —", MonitorPresentation.ProjectLimit(null, Now, zone).ExactResetText);
        Assert.AreEqual("Exact reset: —", MonitorPresentation.ProjectLimit(rate with { ResetsAt = null }, Now, zone).ExactResetText);
        Assert.AreEqual("Exact reset: —", MonitorPresentation.ProjectLimit(rate with { ObservedAt = Now.AddMinutes(6) }, Now, zone).ExactResetText);
        Assert.AreEqual("Exact reset: —", MonitorPresentation.ProjectLimit(rate with { ObservedAt = Now.AddHours(3) }, Now.AddHours(3), zone).ExactResetText);
    }
}
