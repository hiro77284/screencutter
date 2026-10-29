using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenCutter;

/// <summary>
/// ファイル名テンプレートを展開する。
/// {date} {time} {date:書式} {seq} {seq:桁数} {w} {h} {name} が使える。
/// </summary>
public static partial class FileNameBuilder
{
    [GeneratedRegex(@"\{(date|time|seq|w|h|name)(?::([^}]*))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    public static bool UsesSequence(string template) =>
        TokenRegex().Matches(template).Any(m => m.Groups[1].Value.Equals("seq", StringComparison.OrdinalIgnoreCase));

    public static string Build(string template, DateTime now, int sequence, SizePreset preset)
    {
        string name = TokenRegex().Replace(template, m =>
        {
            string token = m.Groups[1].Value.ToLowerInvariant();
            string arg = m.Groups[2].Success ? m.Groups[2].Value : "";
            return token switch
            {
                "date" => FormatDate(now, arg.Length > 0 ? arg : "yyyyMMdd"),
                "time" => FormatDate(now, arg.Length > 0 ? arg : "HHmmss"),
                "seq" => sequence.ToString(new string('0', ParseDigits(arg)), CultureInfo.InvariantCulture),
                "w" => preset.Width.ToString(CultureInfo.InvariantCulture),
                "h" => preset.Height.ToString(CultureInfo.InvariantCulture),
                "name" => preset.Name,
                _ => m.Value,
            };
        });

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "capture" : name;
    }

    private static string FormatDate(DateTime now, string format)
    {
        try
        {
            return now.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        }
    }

    private static int ParseDigits(string arg) =>
        int.TryParse(arg, out int digits) ? Math.Clamp(digits, 1, 10) : 3;
}
