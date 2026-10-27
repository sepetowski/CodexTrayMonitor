namespace CodexTrayMonitor;

internal static class UsagePalette
{
    public const int Low = 0x89ACFF;
    public const int Gold = 0xE7C468;
    public const int Orange = 0xF2A15F;
    public const int High = 0xF17E7E;
    public const int Unavailable = 0x363B45;

    public static int ForUsage(double? value) => value switch
    {
        >= 0 and <= 50 => Low,
        > 50 and <= 70 => Gold,
        > 70 and <= 90 => Orange,
        > 90 and <= 100 => High,
        _ => Unavailable
    };
}
