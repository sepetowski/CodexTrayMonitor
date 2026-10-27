using System.Security.Cryptography;

namespace CodexTrayMonitor.Core;

public sealed class IncrementalJsonlReader
{
    public const int MaxLineBytes = 4 * 1024 * 1024;

    private readonly TimeZoneInfo timeZone;
    private readonly TimeProvider clock;

    public IncrementalJsonlReader(TimeZoneInfo? timeZone = null, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.timeZone = timeZone ?? this.clock.LocalTimeZone;
    }

    public long BytesRead { get; private set; }

    public FileCursor Read(string path, FileCursor? previous, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var creation = File.GetCreationTimeUtc(path).Ticks;
        var cursor = previous;
        if (cursor is null
            || cursor.Offset < 0
            || stream.Length < cursor.Offset
            || cursor.CreationTicks != creation
            || !Matches(stream, cursor))
        {
            cursor = new FileCursor { Path = path, CreationTicks = creation };
        }

        stream.Position = cursor.Offset;
        var limit = stream.Length; // A writer may append; defer those bytes to the next scan.
        var position = cursor.Offset;
        var now = clock.GetUtcNow();
        if (position == limit)
        {
            return cursor;
        }

        using var pending = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (position < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - position));
            if (count == 0)
            {
                break;
            }

            BytesRead += count;
            for (var i = 0; i < count; i++)
            {
                position++;
                var currentByte = buffer[i];
                if (currentByte == (byte)'\n')
                {
                    if (!cursor.DiscardingLine && pending.Length > 0)
                    {
                        var line = pending.GetBuffer().AsMemory(0, (int)pending.Length);
                        if (!TokenEventParser.Apply(line, cursor, timeZone, now))
                        {
                            cursor.InvalidLines = TokenAccounting.Add(cursor.InvalidLines, 1);
                        }
                    }
                    cursor.DiscardingLine = false;
                    pending.SetLength(0);
                    cursor.Offset = position;
                }
                else if (!cursor.DiscardingLine)
                {
                    if (pending.Length >= MaxLineBytes)
                    {
                        cursor.InvalidLines = TokenAccounting.Add(cursor.InvalidLines, 1);
                        cursor.DiscardingLine = true;
                        pending.SetLength(0);
                        cursor.Offset = position;
                    }
                    else
                    {
                        pending.WriteByte(currentByte);
                    }
                }
                else
                {
                    cursor.Offset = position;
                }
            }
        }

        // Offset intentionally excludes an unfinished last line (including split UTF-8).
        // This allows the writer to finish it later, without counting it twice.
        cursor.PrefixHash = Hash(stream, 0, (int)Math.Min(4096, cursor.Offset));
        cursor.TailHash = Hash(stream, Math.Max(0, cursor.Offset - 256), (int)Math.Min(256, cursor.Offset));
        return cursor;
    }

    private static bool Matches(FileStream stream, FileCursor cursor)
    {
        if (cursor.Offset == 0)
            return true;

        return cursor.PrefixHash == Hash(stream, 0, (int)Math.Min(4096, cursor.Offset))
            && cursor.TailHash == Hash(stream, Math.Max(0, cursor.Offset - 256), (int)Math.Min(256, cursor.Offset));
    }

    private static string Hash(FileStream stream, long offset, int length)
    {
        if (length == 0)
        {
            return "";
        }

        stream.Position = offset;
        var bytes = new byte[length];
        var count = 0;
        while (count < length)
        {
            var read = stream.Read(bytes, count, length - count);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, count)));
    }
}
