namespace CodexTrayMonitor.Core;

public sealed class SessionMonitor : IDisposable
{
    private static readonly string[] SessionDirectories = ["sessions", "archived_sessions"];

    private readonly string home;
    private readonly string cachePath;
    private readonly Dictionary<string, FileCursor> cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IncrementalJsonlReader reader;
    private readonly TimeProvider clock;
    private bool disposed;
    private bool cacheDirty = true;
    private bool cacheLoaded;
    private int watcherError;

    public event Action? Changed;
    public long BytesRead => reader.BytesRead;

    public SessionMonitor(string home, string cachePath, TimeProvider? clock = null)
    {
        this.home = LocalPaths.ValidateHome(home);
        this.cachePath = cachePath;
        this.clock = clock ?? TimeProvider.System;
        reader = new(this.clock.LocalTimeZone, this.clock);
    }

    // Called only on the serialized background scan, never the UI constructor.
    private void LoadCache(List<string> warnings, CancellationToken cancellationToken)
    {
        if (cacheLoaded)
            return;

        var cachedState = JsonStorage.Read<CursorCache>(cachePath);
        if (cachedState is null && File.Exists(cachePath))
            AddWarning(warnings, "The cache could not be read; rebuilding from session files.");

        if (cachedState is { Version: 4, Files: not null } && cachedState.TimeZoneId == this.clock.LocalTimeZone.Id
            && string.Equals(cachedState.Home, this.home, StringComparison.OrdinalIgnoreCase))
        {
            cacheDirty = false;
            foreach (var cursor in cachedState.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ValidCursor(cursor))
                {
                    cursors[cursor.Path] = cursor;
                }
                else
                {
                    cacheDirty = true;
                    AddWarning(warnings, "Invalid cache entries were ignored; rebuilding from session files.");
                }
            }
        }
        cacheLoaded = true;
    }

    private bool ValidCursor(FileCursor? cursor)
    {
        if (cursor is null || string.IsNullOrWhiteSpace(cursor.Path) || !Path.IsPathFullyQualified(cursor.Path)
            || !ValidCachedPath(cursor.Path))
            return false;

        var validTimestamps = NotFuture(cursor.ActivityAt)
            && NotFuture(cursor.UsageAt)
            && NotFuture(cursor.TokenObservedAt)
            && NotFuture(cursor.Rate?.ObservedAt)
            && NotFuture(cursor.WeeklyRate?.ObservedAt);

        var validCounters = cursor.DailyTokens is not null
            && cursor.DailyTokens.Values.All(value => value >= 0)
            && cursor.InputTokens is { IsValid: true }
            && cursor.OutputTokens is { IsValid: true }
            && cursor.CachedTokens is { IsValid: true }
            && NotFuture(cursor.InputTokens.ObservedAt)
            && NotFuture(cursor.OutputTokens.ObservedAt)
            && NotFuture(cursor.CachedTokens.ObservedAt)
            && cursor.TotalsAtTokenTimestamp is not null
            && cursor.TotalsAtTokenTimestamp.All(value => value >= 0);

        return validTimestamps && validCounters
            && (cursor.SessionId is null || !string.IsNullOrWhiteSpace(cursor.SessionId))
            && cursor.Offset >= 0
            && cursor.CreationTicks >= 0
            && cursor.CreationTicks <= DateTime.MaxValue.Ticks
            && cursor.InvalidLines >= 0
            && cursor.TokenObservationCount >= 0
            && cursor.TokenBaseline is not < 0
            && cursor.PrefixHash is not null
            && cursor.TailHash is not null;
    }

    private bool NotFuture(DateTimeOffset? value) => value is null || value.Value - clock.GetUtcNow() <= TimeSpan.FromMinutes(5);

    private bool ValidCachedPath(string path)
    {
        try
        {
            return string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
                && SessionDirectories.Any(name =>
                    path.StartsWith(Path.Combine(home, name) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    public async Task<MonitorSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() => Scan(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private MonitorSnapshot Scan(CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        LoadCache(warnings, cancellationToken);
        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Recheck the path every scan: a folder may have been replaced with a link.
        try
        {
            LocalPaths.ValidateHome(home);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Validate session folder: {0}", e);
            return CreateSnapshot(["Cannot access the local Codex folder."]);
        }
        foreach (var name in SessionDirectories)
        {
            var root = Path.Combine(home, name);
            foreach (var path in Enumerate(root, warnings, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                discoveredPaths.Add(path);
                try
                {
                    cursors.TryGetValue(path, out var previous);
                    var previousOffset = previous?.Offset;
                    var cursor = reader.Read(path, previous, cancellationToken);
                    cursors[path] = cursor;
                    cacheDirty |= !ReferenceEquals(previous, cursor) || previousOffset != cursor.Offset;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    AddWarning(warnings, "Cannot read some session files: " + e.GetType().Name);
                }
            }
        }
        // Failed enumeration is not evidence that cached files were deleted.
        if (warnings.Count == 0)
        {
            foreach (var deleted in cursors.Keys.Where(p => !discoveredPaths.Contains(p)).ToArray())
            {
                cursors.Remove(deleted);
                cacheDirty = true;
            }
        }

        if (Interlocked.Exchange(ref watcherError, 0) != 0)
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }

            watchers.Clear();
            AddWarning(warnings, "The file watcher missed events; a full directory scan was performed.");
        }
        EnsureWatchers(warnings);
        try
        {
            if (cacheDirty)
            {
                JsonStorage.Write(cachePath, new CursorCache
                {
                    Home = home,
                    TimeZoneId = clock.LocalTimeZone.Id,
                    Files = cursors.Values.ToList()
                });
                cacheDirty = false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AddWarning(warnings, "Cannot save the cache; monitoring continues in memory.");
        }

        return CreateSnapshot(warnings);
    }

    private MonitorSnapshot CreateSnapshot(IReadOnlyList<string> warnings)
    {
        var files = cursors.Values;
        var rate = files.Select(cursor => cursor.Rate)
            .Where(observation => observation is not null)
            .OrderByDescending(observation => observation!.ObservedAt)
            .FirstOrDefault();
        var weekly = files.Select(cursor => cursor.WeeklyRate)
            .Where(observation => observation is not null)
            .OrderByDescending(observation => observation!.ObservedAt)
            .FirstOrDefault();

        // Select one complete counter history per logical session, not one copy per file.
        var sessions = files
            .Where(cursor => cursor.TokenObservedAt is not null
                || cursor.InputTokens.ObservedAt is not null
                || cursor.OutputTokens.ObservedAt is not null
                || cursor.CachedTokens.ObservedAt is not null)
            .GroupBy(cursor => cursor.SessionId ?? cursor.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(cursor => cursor.TokenObservedAt ?? cursor.UsageAt)
                .ThenByDescending(cursor => cursor.TokenObservationCount)
                .ThenBy(cursor => cursor.Path, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToArray();
        var daily = new Dictionary<DateOnly, long>();
        foreach (var session in sessions)
        {
            foreach (var (day, total) in session.DailyTokens)
            {
                daily[day] = TokenAccounting.Add(daily.GetValueOrDefault(day), total);
            }
        }

        return new(
            clock.GetUtcNow(), rate, weekly, daily,
            sessions.Any(cursor => cursor.TokenObservedAt is not null),
            files.Count,
            files.Aggregate(0L, (sum, cursor) => TokenAccounting.Add(sum, cursor.InvalidLines)),
            warnings)
        {
            InputTokens = SumCounters(sessions.Select(c => c.InputTokens)),
            OutputTokens = SumCounters(sessions.Select(c => c.OutputTokens)),
            CachedTokens = SumCounters(sessions.Select(c => c.CachedTokens))
        };
    }

    private static Dictionary<DateOnly, long> SumCounters(IEnumerable<TokenCounter> counters)
    {
        var daily = new Dictionary<DateOnly, long>();
        foreach (var counter in counters)
        {
            foreach (var (day, value) in counter.Daily)
            {
                daily[day] = TokenAccounting.Add(daily.GetValueOrDefault(day), value);
            }
        }

        return daily;
    }

    private static IEnumerable<string> Enumerate(string root, List<string> warnings, CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] entries;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    AddWarning(warnings, "Skipped a symbolic link in the session folder.");
                    continue;
                }
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AddWarning(warnings, "Some session folders are inaccessible.");
                continue;
            }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    AddWarning(warnings, "Cannot inspect some session files.");
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    AddWarning(warnings, "Skipped a symbolic link in the session folder.");
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    stack.Push(entry);
                }
                else if (string.Equals(Path.GetExtension(entry), ".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry;
                }
            }
        }
    }

    private void EnsureWatchers(List<string> warnings)
    {
        foreach (var watcher in watchers.Where(w => !Directory.Exists(w.Path)).ToArray())
        {
            watchers.Remove(watcher);
            watcher.Dispose();
        }
        foreach (var name in SessionDirectories)
        {
            var root = Path.Combine(home, name);
            if (!Directory.Exists(root) || watchers.Any(w => w.Path == root))
            {
                continue;
            }

            FileSystemWatcher? watcher = null;
            try
            {
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                watcher = new FileSystemWatcher(root)
                {
                    Filter = "*",
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
                watcher = null; // Ownership transferred to the monitor.
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AddWarning(warnings, "The file watcher is unavailable; periodic refresh remains active.");
            }
            finally
            {
                watcher?.Dispose();
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!disposed)
        {
            Changed?.Invoke();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        System.Diagnostics.Trace.TraceWarning("Session watcher: {0}", e.GetException());
        Interlocked.Exchange(ref watcherError, 1);
        if (!disposed)
        {
            Changed?.Invoke();
        }
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Contains(warning))
        {
            return;
        }

        warnings.Add(warning);
        System.Diagnostics.Trace.TraceWarning(warning);
    }

    // The caller stops new refresh requests and cancels the current read before waiting.
    public async Task StopAsync()
    {
        if (disposed)
        {
            return;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
        Dispose();
    }

    // Direct disposal requires the caller to have awaited any refresh.
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        watchers.Clear();
        Changed = null;
        gate.Dispose();
    }
}
