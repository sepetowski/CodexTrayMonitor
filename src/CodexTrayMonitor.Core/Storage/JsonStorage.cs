using System.Text.Json;

namespace CodexTrayMonitor.Core;

public static class JsonStorage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T? Read<T>(string path, long maxBytes = 64 * 1024 * 1024)
    {
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            using var stream = File.OpenRead(path);
            if (stream.Length > maxBytes)
            {
                System.Diagnostics.Trace.TraceWarning("JSON file exceeds the size limit.");
                return default;
            }

            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.TraceWarning("Cannot read {0}: {1}", path, e);
            return default;
        }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(true);
            }

            File.Move(temp, path, true);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Cannot remove temporary file {0}: {1}", temp, e);
            }
        }
    }
}
