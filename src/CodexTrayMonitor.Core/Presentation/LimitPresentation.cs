using System.Globalization;

namespace CodexTrayMonitor.Core;

public sealed record LimitPresentation(double? UsedPercent, bool Renewed, string RenewalText, string ExactResetText)
{
    public string PercentText => UsedPercent is { } used
        ? used.ToString("0.#", CultureInfo.InvariantCulture) + "%"
        : "—";
}
