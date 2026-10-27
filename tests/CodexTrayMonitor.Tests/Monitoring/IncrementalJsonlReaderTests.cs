using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CodexTrayMonitor.Core;

namespace CodexTrayMonitor.Tests;

[TestClass]
public sealed class IncrementalJsonlReaderTests
{
    private readonly TestWorkspace workspace = new();
    private readonly IncrementalJsonlReader reader = new(TimeZoneInfo.Utc, Clock(Now, TimeZoneInfo.Utc));

    [TestMethod(DisplayName = "Incomplete JSONL tail is retried without duplicate counts")]
    public void IncompleteJSONLTailIsRetriedWithoutDuplicateCounts()
    {
        var completeEvent = Event(150);
        var path = workspace.FileFor("partial", Event() + "\n" + completeEvent[..80]);

        var cursor = reader.Read(path, null);
        Assert.AreEqual(100L, cursor.Total!.Total);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(Event() + "\n"), cursor.Offset);

        File.AppendAllText(path, completeEvent[80..] + "\n");
        cursor = reader.Read(path, cursor);
        Assert.AreEqual(150L, cursor.Total!.Total);
        Assert.AreEqual(new FileInfo(path).Length, cursor.Offset);

        var bytes = reader.BytesRead;
        reader.Read(path, cursor);
        Assert.AreEqual(bytes, reader.BytesRead);
    }

    [TestMethod(DisplayName = "Truncation resets offset and previous session")]
    public void TruncationResetsOffsetAndPreviousSession()
    {
        var path = workspace.FileFor("truncate", Meta("old") + "\n" + Event(900) + "\n" + Event(900) + "\n");
        var cursor = reader.Read(path, null);

        File.WriteAllText(path, Meta("new") + "\n" + Event(20) + "\n");
        cursor = reader.Read(path, cursor);

        Assert.AreEqual("new", cursor.SessionId);
        Assert.AreEqual(20L, cursor.Total!.Total);
    }

    [TestMethod(DisplayName = "Equal-length replacement is detected by fingerprints")]
    public void EqualLengthReplacementIsDetectedByFingerprints()
    {
        var path = workspace.FileFor("rewrite", Event(100) + "\n");
        var cursor = reader.Read(path, null);

        File.WriteAllText(path, Event(200) + "\n");
        cursor = reader.Read(path, cursor);

        Assert.AreEqual(200L, cursor.Total!.Total);
    }

    [TestMethod(DisplayName = "Bad lines do not prevent subsequent valid lines")]
    public void BadLinesDoNotPreventSubsequentValidLines()
    {
        var path = workspace.FileFor("bad", "{bad}\n" + Event() + "\r\n");
        var cursor = reader.Read(path, null);

        Assert.AreEqual(1L, cursor.InvalidLines);
        Assert.AreEqual(100L, cursor.Total!.Total);
    }

    [TestMethod(DisplayName = "BOM and split UTF8 tail are supported")]
    public void BOMAndSplitUTF8TailAreSupported()
    {
        var metadata = JsonSerializer.Serialize(
            new { timestamp = Now.ToString("O"), type = "session_meta", payload = new { id = "zażółć" } },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var text = "\uFEFF" + metadata + "\r\n" + Event() + "\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        var split = Array.IndexOf(bytes, (byte)0xc5) + 1;
        Assert.IsTrue(split > 1, "Expected literal UTF-8 character in fixture.");

        var path = Path.Combine(workspace.Root, "utf8.jsonl");
        File.WriteAllBytes(path, bytes[..split]);
        var cursor = reader.Read(path, null);
        Assert.AreEqual(0L, cursor.Offset);
        Assert.IsNull(cursor.SessionId);

        using (var stream = new FileStream(path, FileMode.Append))
            stream.Write(bytes[split..]);

        cursor = reader.Read(path, cursor);
        Assert.AreEqual("zażółć", cursor.SessionId);
        Assert.AreEqual(0L, cursor.InvalidLines);
        Assert.AreEqual(100L, cursor.Total!.Total);
    }

    [TestMethod(DisplayName = "Oversized line is skipped with bounded memory")]
    public void OversizedLineIsSkippedWithBoundedMemory()
    {
        var path = workspace.FileFor("oversize", new string('x', IncrementalJsonlReader.MaxLineBytes + 100));
        var cursor = reader.Read(path, null);
        Assert.IsTrue(cursor.DiscardingLine);

        File.AppendAllText(path, "end\n" + Event() + "\n");
        cursor = reader.Read(path, cursor);

        Assert.AreEqual(1L, cursor.InvalidLines);
        Assert.AreEqual(100L, cursor.Total!.Total);
        Assert.IsFalse(cursor.DiscardingLine);
    }
}
