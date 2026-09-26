namespace AstralPath.Core.Formatting;

/// <summary>三端统一数值格式化。score/impact 金样要求 F6。</summary>
public static class NumberFormatter
{
    public static string FormatScore(double score) => score.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

    public static string FormatImpact(double impact) => impact.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

    public static string FormatPercent(double ratio) => ratio.ToString("P0", System.Globalization.CultureInfo.InvariantCulture);

    public static string FormatMinutes(int minutes) => minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
