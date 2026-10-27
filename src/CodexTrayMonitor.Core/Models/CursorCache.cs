using System.Text.Json.Serialization;

namespace CodexTrayMonitor.Core;

public sealed class CursorCache
{
    [JsonRequired]
    public int Version { get; set; } = 4;
    public string Home { get; set; } = "";
    public string TimeZoneId { get; set; } = "";
    public List<FileCursor> Files { get; set; } = [];
}
