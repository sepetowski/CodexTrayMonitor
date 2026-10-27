using System.Text.Json;
using CodexTrayMonitor.Core;

namespace CodexTrayMonitor.Tests;

[TestClass]
public sealed class SessionMonitorTests
{
    private readonly TestWorkspace workspace = new();

    [TestMethod(DisplayName = "Empty session IDs cannot merge unrelated files")]
    public Task EmptySessionIdsStaySeparate() => workspace.WithMonitor("empty-id", async (home, cache, monitor) =>
    {
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Meta("") + "\n" + Event(100) + "\n");
        File.WriteAllText(Path.Combine(home, "sessions", "b.jsonl"), Meta("  ") + "\n" + Event(200) + "\n");
        Assert.AreEqual(300L, Today(await monitor.RefreshAsync()));
        var saved = JsonStorage.Read<CursorCache>(cache)!;
        saved.Files[0].SessionId = ""; // A malformed cache must recover as well.
        JsonStorage.Write(cache, saved);
        using var restarted = new SessionMonitor(home, cache, Clock(Now, TimeZoneInfo.Utc));
        Assert.AreEqual(300L, Today(await restarted.RefreshAsync()));
    });

    [TestMethod(DisplayName = "Cancellation during a large append recovers without double counting")]
    public Task CancelActiveScanRecovers() => workspace.WithMonitor("cancel-active", async (home, _, monitor) =>
    {
        var path = Path.Combine(home, "sessions", "a.jsonl");
        File.WriteAllText(path, Event(100) + "\n");
        await monitor.RefreshAsync();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.SetLength(stream.Length + 64L * 1024 * 1024);
            stream.Position = stream.Length;
            stream.Write(System.Text.Encoding.UTF8.GetBytes("\n" + Event(150, time: Now.AddMinutes(1)) + "\n"));
        }
        var bytesBefore = monitor.BytesRead;
        using var cancellation = new CancellationTokenSource();
        var reading = monitor.RefreshAsync(cancellation.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (monitor.BytesRead == bytesBefore && !reading.IsCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(1);

        Assert.IsTrue(monitor.BytesRead > bytesBefore);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading);

        var recovered = await monitor.RefreshAsync();
        Assert.AreEqual(150L, Today(recovered));
        Assert.AreEqual(1L, recovered.InvalidLines);
        Assert.AreEqual(150L, Today(await monitor.RefreshAsync()));
    });

    [TestMethod(DisplayName = "Future cache timestamps are discarded and rebuilt")]
    public Task FutureCacheIsRebuilt() => workspace.WithMonitor("future-cache-v4", async (home, cache, monitor) =>
    {
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Event(100) + "\n");
        await monitor.RefreshAsync();
        var state = JsonStorage.Read<CursorCache>(cache)!;
        state.Files[0].UsageAt = Now.AddYears(1);
        state.Files[0].TokenBaseline = 9999;
        JsonStorage.Write(cache, state);
        using var restarted = new SessionMonitor(home, cache, Clock(Now, TimeZoneInfo.Utc));
        var snapshot = await restarted.RefreshAsync();
        Assert.AreEqual(100L, Today(snapshot));
        Assert.IsTrue(snapshot.Warnings.Any(w => w.Contains("cache")));
        Assert.IsTrue(restarted.BytesRead > 0);
    });

    [TestMethod(DisplayName = "Future events cannot poison counters or limits across restart")]
    public Task FutureEventsCannotPoisonState() => workspace.WithMonitor("future-recovery", async (home, cache, monitor) =>
    {
        var path = Path.Combine(home, "sessions", "a.jsonl");
        File.WriteAllText(path, Meta("real") + "\n" + Event(100, used: 10) + "\n"
            + Meta("wrong", Now.AddYears(1)) + "\n" + Event(99999, used: 99, time: Now.AddYears(1)) + "\n");
        var snapshot = await monitor.RefreshAsync();
        Assert.AreEqual(100L, Today(snapshot));
        Assert.AreEqual(10d, snapshot.Rate!.UsedPercent);
        Assert.AreEqual(2L, snapshot.InvalidLines);
        File.AppendAllText(path, Event(150, used: 15, time: Now.AddMinutes(1)) + "\n");
        using var restarted = new SessionMonitor(home, cache, Clock(Now, TimeZoneInfo.Utc));
        snapshot = await restarted.RefreshAsync();
        Assert.AreEqual(150L, Today(snapshot));
        Assert.AreEqual(15d, snapshot.Rate!.UsedPercent);
        Assert.AreEqual("real", JsonStorage.Read<CursorCache>(cache)!.Files.Single().SessionId);
    });

    [TestMethod(DisplayName = "Cache is loaded lazily and corrupt cache is reported")]
    public async Task CacheIsLoadedLazily()
    {
        var home = Directory.CreateDirectory(Path.Combine(workspace.Root, "lazy-home")).FullName;
        var cache = Path.Combine(workspace.Root, "lazy-cache.json");
        using var monitor = new SessionMonitor(home, cache, Clock(Now, TimeZoneInfo.Utc));
        // Writing after construction proves the read happens at refresh time.
        File.WriteAllText(cache, "invalid");
        var snapshot = await monitor.RefreshAsync();
        Assert.IsTrue(snapshot.Warnings.Any(w => w.Contains("cache")));
        Assert.AreEqual(4, JsonStorage.Read<CursorCache>(cache)!.Version);
    }

    [TestMethod(DisplayName = "Cache counters saturate rather than overflow")]
    public Task CacheCountersSaturate() => workspace.WithMonitor("overflow", async (home, cache, monitor) =>
    {
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Event() + "\n");
        File.WriteAllText(Path.Combine(home, "sessions", "b.jsonl"), Event() + "\n");
        await monitor.RefreshAsync();
        var state = JsonStorage.Read<CursorCache>(cache)!;
        foreach (var cursor in state.Files)
            cursor.InvalidLines = long.MaxValue;
        JsonStorage.Write(cache, state);
        using var restarted = new SessionMonitor(home, cache, Clock(Now, TimeZoneInfo.Utc));
        Assert.AreEqual(long.MaxValue, (await restarted.RefreshAsync()).InvalidLines);
        File.AppendAllText(Path.Combine(home, "sessions", "a.jsonl"), "broken\n");
        Assert.AreEqual(long.MaxValue, (await restarted.RefreshAsync()).InvalidLines);
    });

    [TestMethod(DisplayName = "Concurrent refresh and cancellation recover safely")]
    public Task ConcurrentRefreshRecovers() => workspace.WithMonitor("concurrent", async (home, _, monitor) =>
    {
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Event(123) + "\n");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => monitor.RefreshAsync(cancellation.Token));

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => monitor.RefreshAsync()));
        foreach (var snapshot in snapshots)
            Assert.AreEqual(123L, Today(snapshot));
    });

    [TestMethod(DisplayName = "Archive copies and restart cache do not duplicate tokens")]
    public Task ArchiveCopiesAndRestartCacheDoNotDuplicateTokens() => workspace.WithMonitor("archive", async (home, cache, monitor) =>
    {
        var content = Meta("same-session") + "\n" + Event(120) + "\n";
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), content);
        File.WriteAllText(Path.Combine(home, "archived_sessions", "b.jsonl"), content);
        var snapshot = await monitor.RefreshAsync();
        var display = MonitorPresentation.From(snapshot, Now, TimeZoneInfo.Utc);

        Assert.AreEqual(120L, Today(snapshot));
        Assert.AreEqual("Input: 110", display.InputText);
        Assert.AreEqual("Output: 10", display.OutputText);
        Assert.AreEqual("Cached: 20", display.CachedText);
        Assert.AreEqual(2, snapshot.FileCount);

        using var restarted = new SessionMonitor(home, cache, Clock(Now, TimeZoneInfo.Utc));
        snapshot = await restarted.RefreshAsync();
        Assert.AreEqual(120L, Today(snapshot));
        Assert.AreEqual(0L, restarted.BytesRead);
        Assert.AreEqual("Input: 110", MonitorPresentation.From(snapshot, Now, TimeZoneInfo.Utc).InputText);
        Assert.AreEqual("Input: 0", MonitorPresentation.From(snapshot, Now.AddDays(1), TimeZoneInfo.Utc).InputText);
        Assert.AreEqual("Input: —", MonitorPresentation.From(MonitorSnapshot.Empty, Now, TimeZoneInfo.Utc).InputText);
    });

    [TestMethod(DisplayName = "Expired observations project zero without modifying raw data")]
    public Task ExpiredObservationsProjectZeroWithoutModifyingRawData() => workspace.WithMonitor("stale", async (home, _, monitor) =>
    {
        var path = Path.Combine(home, "sessions", "a.jsonl");
        File.WriteAllText(path, Event(used: 87, time: Now.AddHours(-3)) + "\n");
        var s = await monitor.RefreshAsync();
        Assert.AreEqual(0d, Display(s).UsedPercent);
        Assert.IsTrue(Display(s).Renewed);
        Assert.AreEqual(87d, s.Rate!.UsedPercent);
    });

    [TestMethod(DisplayName = "Age alone keeps the last observation available")]
    public Task AgeAloneKeepsTheLastObservationAvailable() => workspace.WithMonitor("old", async (home, _, monitor) =>
    {
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Event(time: Now.AddMinutes(-11)) + "\n");
        Assert.AreEqual(42d, Display(await monitor.RefreshAsync()).UsedPercent);
    });

    [TestMethod(DisplayName = "Future samples are unavailable")]
    public Task FutureSamplesAreUnavailable() => workspace.WithMonitor("future", async (home, _, monitor) =>
    {
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Event(time: Now.AddHours(1)) + "\n");
        Assert.IsNull(Display(await monitor.RefreshAsync()).UsedPercent);
    });

    [TestMethod(DisplayName = "New session without counters does not erase daily totals")]
    public Task NewSessionWithoutCountersDoesNotEraseDailyTotals() => workspace.WithMonitor("session-switch", async (home, _, monitor) =>
    {
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Meta("old") + "\n" + Event() + "\n");
        File.WriteAllText(Path.Combine(home, "sessions", "b.jsonl"), Meta("new", Now.AddSeconds(1)) + "\n");
        var s = await monitor.RefreshAsync();
        Assert.AreEqual(100L, Today(s));
        Assert.AreEqual(42d, s.Rate!.UsedPercent);
    });

    [TestMethod(DisplayName = "Deleted files cannot survive solely through cache")]
    public Task DeletedFilesCannotSurviveSolelyThroughCache() => workspace.WithMonitor("delete", async (home, _, monitor) =>
    {
        var path = Path.Combine(home, "sessions", "a.jsonl");
        File.WriteAllText(path, Event() + "\n");
        await monitor.RefreshAsync();
        File.Delete(path);
        var s = await monitor.RefreshAsync();
        Assert.IsNull(Display(s).UsedPercent);
        Assert.AreEqual(0, s.FileCount);
        Assert.IsNull(Today(s));
    });

    [TestMethod(DisplayName = "Empty and missing folders are unavailable")]
    public Task EmptyAndMissingFoldersAreUnavailable() => workspace.WithMonitor("empty", async (_, _, monitor) =>
    {
        Assert.IsNull(Display(await monitor.RefreshAsync()).UsedPercent);
    });

    [TestMethod(DisplayName = "FileSystemWatcher signals appended data")]
    public Task FileSystemWatcherSignalsAppendedData() => workspace.WithMonitor("watcher", async (home, _, monitor) =>
    {
        await monitor.RefreshAsync();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += () => signal.TrySetResult();
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Event() + "\n");
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(42d, Display(await monitor.RefreshAsync()).UsedPercent);
    });

    [TestMethod(DisplayName = "Cache from another Codex home is ignored")]
    public Task CacheFromAnotherCodexHomeIsIgnored() => workspace.WithMonitor("cache-home", async (home, cache, monitor) =>
    {
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Event() + "\n");
        await monitor.RefreshAsync();
        var other = Path.Combine(workspace.Root, "other-home");
        Directory.CreateDirectory(other);
        using var otherMonitor = new SessionMonitor(other, cache, Clock(Now, TimeZoneInfo.Utc));
        Assert.IsNull(Display(await otherMonitor.RefreshAsync()).UsedPercent);
    });

    [TestMethod(DisplayName = "Locked file produces a warning and recovers after unlock")]
    public Task LockedFileProducesAWarningAndRecoversAfterUnlock() => workspace.WithMonitor("locked", async (home, _, monitor) =>
    {
        var path = Path.Combine(home, "sessions", "a.jsonl");
        File.WriteAllText(path, Event() + "\n");
        await monitor.RefreshAsync();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var s = await monitor.RefreshAsync();
            Assert.AreEqual(42d, Display(s).UsedPercent);
            Assert.AreEqual(100L, Today(s));
            Assert.IsTrue(s.Warnings.Count > 0);
        }

        Assert.AreEqual(42d, Display(await monitor.RefreshAsync()).UsedPercent);
    });

    [TestMethod(DisplayName = "Events without rate_limits do not refresh rate sample age")]
    public Task EventsWithoutRateLimitsDoNotRefreshRateSampleAge() => workspace.WithMonitor("no-new-rate", async (home, _, monitor) =>
    {
        var withoutRate = JsonSerializer.Serialize(new
        {
            timestamp = Now.ToString("O"),
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new { last_token_usage = new { total_tokens = 10 } }
            }
        });
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Event(time: Now.AddMinutes(-20)) + "\n" + withoutRate + "\n");
        var s = await monitor.RefreshAsync();
        Assert.AreEqual(42d, Display(s).UsedPercent);
        Assert.AreEqual(Now.AddMinutes(-20), s.Rate!.ObservedAt);
    });

    [TestMethod(DisplayName = "Daily totals sum sessions but deduplicate archive copies by newest counter")]
    public Task DailyTotalsSumSessionsButDeduplicateArchiveCopiesByNewestCounter() => workspace.WithMonitor("daily-archive", async (home, cache, monitor) =>
    {
        var content = Meta("same") + "\n" + Event(100) + "\n";
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), content);
        File.WriteAllText(Path.Combine(home, "archived_sessions", "a.jsonl"), content + Event(140, time: Now.AddMinutes(1)) + "\n");
        File.WriteAllText(Path.Combine(home, "sessions", "b.jsonl"), Meta("different") + "\n" + Event(60) + "\n");
        var snapshot = await monitor.RefreshAsync();
        Assert.AreEqual(200L, Today(snapshot));
        using var restarted = new SessionMonitor(home, cache, Clock(Now, TimeZoneInfo.Utc));
        Assert.AreEqual(200L, Today(await restarted.RefreshAsync()));
        Assert.AreEqual(0L, restarted.BytesRead);
    });

    [TestMethod(DisplayName = "Archive ties choose the more complete counter history")]
    public Task ArchiveTiesChooseTheMoreCompleteCounterHistory() => workspace.WithMonitor("archive-tie", async (home, _, monitor) =>
    {
        var yesterday = Now.AddDays(-1);
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Meta("same") + "\n" + Event(150) + "\n");
        File.WriteAllText(Path.Combine(home, "archived_sessions", "b.jsonl"), Meta("same") + "\n" + Event(100, time: yesterday) + "\n" + Event(150) + "\n");
        Assert.AreEqual(50L, Today(await monitor.RefreshAsync()));
    });

    [TestMethod(DisplayName = "Controlled clock resets cached limits without reading new bytes and accepts new usage")]
    public async Task ControlledClockResetsCachedLimitsWithoutReadingNewBytesAndAcceptsNewUsage()
    {
        var home = Path.Combine(workspace.Root, "clock");
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        var path = Path.Combine(home, "sessions", "a.jsonl");
        File.WriteAllText(path, Event(used: 55) + "\n" + Event(minutes: 10080, used: 68, time: Now.AddHours(1)) + "\n");
        var clock = Clock(Now.AddHours(1), TimeZoneInfo.Utc);
        using var monitor = new SessionMonitor(home, Path.Combine(workspace.Root, "clock-cache.json"), clock);
        var snapshot = await monitor.RefreshAsync();
        var bytes = monitor.BytesRead;
        clock.SetUtcNow(Now.AddHours(2));
        var projected = MonitorPresentation.From(snapshot, clock.GetUtcNow(), clock.LocalTimeZone);
        Assert.AreEqual(0d, projected.FiveHour.UsedPercent);
        Assert.AreEqual(68d, projected.Weekly.UsedPercent);
        Assert.AreEqual(bytes, monitor.BytesRead);
        Assert.AreEqual(0d, Display(await monitor.RefreshAsync()).UsedPercent);
        Assert.AreEqual(bytes, monitor.BytesRead);
        File.AppendAllText(path, Event(used: 3, time: clock.GetUtcNow().AddSeconds(1)) + "\n");
        Assert.AreEqual(3d, Display(await monitor.RefreshAsync()).UsedPercent);
    }

    [TestMethod(DisplayName = "Old cache and changed timezone rebuild histories")]
    public Task OldCacheAndChangedTimezoneRebuildHistories() => workspace.WithMonitor("cache-version", async (home, cache, monitor) =>
    {
        File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), Event() + "\n" + Event(minutes: 10080, used: 68) + "\n");
        await monitor.RefreshAsync();
        var saved = JsonStorage.Read<CursorCache>(cache)!;
        saved.Version = 3;
        foreach (var cursor in saved.Files)
        {
            cursor.InputTokens = new();
            cursor.OutputTokens = new();
            cursor.CachedTokens = new();
        }
        JsonStorage.Write(cache, saved);
        using (var rebuilt = new SessionMonitor(home, cache, Clock(Now, TimeZoneInfo.Utc)))
        {
            var snapshot = await rebuilt.RefreshAsync();
            Assert.AreEqual(68d, snapshot.WeeklyRate!.UsedPercent);
            Assert.AreEqual(100L, Today(snapshot));
            Assert.IsTrue(rebuilt.BytesRead > 0);
            Assert.AreEqual("Input: 90", MonitorPresentation.From(snapshot, Now, TimeZoneInfo.Utc).InputText);
        }

        using var changedZone = new SessionMonitor(home, cache, Clock(Now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")));
        await changedZone.RefreshAsync();
        Assert.IsTrue(changedZone.BytesRead > 0);
    });
}
