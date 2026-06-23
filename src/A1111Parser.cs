using System.Globalization;
using System.Text.RegularExpressions;

namespace TheTagHag;

/// <summary>
/// Inverse of CivitaiHarvesterApp's A1111.BuildParams. Parses the A1111 "parameters" text
/// (from an embedded PNG chunk, EXIF, or a sidecar .txt) into a ParsedMeta. Shape:
///   positive prompt
///   Negative prompt: ...            (optional)
///   Steps: N, Sampler: X, CFG scale: C, Seed: S, Size: WxH, Model: M
/// Tolerant: missing fields are left null/0; the settings line is detected by content,
/// so a multi-line positive/negative still splits correctly.
/// </summary>
public static class A1111Parser
{
    private static readonly Regex SettingsLine =
        new(@"(^|\n)\s*Steps:\s*\d+.*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public static ParsedMeta Parse(string parameters, string source = "embedded")
    {
        var meta = new ParsedMeta { Format = "a1111", Source = source };
        if (string.IsNullOrWhiteSpace(parameters)) { meta.Format = "none"; return meta; }

        var text = parameters.Replace("\r\n", "\n").Trim();

        // Locate the trailing settings line (the last "Steps: ..." line).
        string settings = "";
        int settingsStart = -1;
        foreach (Match m in SettingsLine.Matches(text)) { settings = m.Value.Trim(); settingsStart = m.Index; }

        // The body (positive [+ negative]) is everything before the settings line.
        var body = settingsStart >= 0 ? text[..settingsStart].Trim() : text;

        var negIdx = IndexOfIgnoreCase(body, "\nNegative prompt:");
        if (negIdx < 0 && body.StartsWith("Negative prompt:", StringComparison.OrdinalIgnoreCase)) negIdx = 0;

        if (negIdx >= 0)
        {
            meta.Prompt = body[..negIdx].Trim();
            var negStart = body.IndexOf(':', negIdx) + 1;
            meta.Negative = body[negStart..].Trim();
        }
        else
        {
            meta.Prompt = body.Trim();
        }

        if (settings.Length > 0)
        {
            meta.Steps = ParseInt(Field(settings, "Steps")) ?? 0;
            meta.Sampler = Field(settings, "Sampler");
            meta.Cfg = ParseDouble(Field(settings, "CFG scale"));
            meta.Seed = ParseLong(Field(settings, "Seed"));
            meta.Model = Field(settings, "Model");
            var size = Field(settings, "Size");
            if (size is not null)
            {
                var mm = Regex.Match(size, @"(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase);
                if (mm.Success) { meta.Width = int.Parse(mm.Groups[1].Value); meta.Height = int.Parse(mm.Groups[2].Value); }
            }
        }

        if (string.IsNullOrWhiteSpace(meta.Prompt) && string.IsNullOrWhiteSpace(meta.Negative) && settings.Length == 0)
            meta.Format = "none";
        return meta;
    }

    /// <summary>Pulls "Key: value" from the comma-separated settings line (value up to the next ", Key:" or end).</summary>
    private static string? Field(string settings, string key)
    {
        var m = Regex.Match(settings, $@"{Regex.Escape(key)}:\s*(.+?)(?:,\s*[A-Za-z][\w ]*:\s|$)",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static int IndexOfIgnoreCase(string s, string sub) => s.IndexOf(sub, StringComparison.OrdinalIgnoreCase);
    private static int? ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static long? ParseLong(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static double? ParseDouble(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
