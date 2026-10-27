namespace CodexTrayMonitor.Core;

internal readonly record struct EffectiveLimit(double? UsedPercent, bool Renewed, DateTimeOffset? ResetsAt)
{
    public static EffectiveLimit From(RateObservation? observation, DateTimeOffset now)
    {
        if (observation?.UsedPercent is not { } used || !double.IsFinite(used) || used < 0 || used > 100
            || observation.ObservedAt > now.AddMinutes(5))
        {
            return new(null, false, null);
        }

        if (observation.ResetsAt is { } reset)
        {
            if (now >= reset && observation.ObservedAt < reset)
            {
                return new(0, true, reset);
            }

            if (reset > now)
            {
                return new(used, false, reset);
            }
        }
        return new(used, false, null);
    }
}
