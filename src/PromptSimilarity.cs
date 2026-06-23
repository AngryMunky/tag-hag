using System.Text.RegularExpressions;

namespace TheTagHag;

/// <summary>
/// Reused verbatim from CivitaiHarvesterApp. The load-bearing piece is <see cref="TokenSet"/>:
/// index-time tag extraction (LocalScanner) AND search-term normalization (SearchParser) both
/// call it, so the tokens written to image_tags always agree with what a query matches.
/// (CorpusIdf / WeightedJaccard retained for a future dedup feature — v2, not used in v1.)
/// </summary>
public static class PromptSimilarity
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "score_9","score_8_up","score_7_up","score_6_up","score_5_up","score_4_up",
        "score_8","score_7","score_6","score_5","score_4","score_3","score_2","score_1",
        "source_anime","source_cartoon","source_furry","source_pony","rating_safe",
        "rating_questionable","rating_explicit","safe","explicit","questionable",
        "masterpiece","best quality","high quality","good quality","normal quality",
        "low quality","worst quality","ultra quality","amazing quality","great quality",
        "highres","absurdres","lowres","incredibly absurdres","high resolution",
        "ultra detailed","ultra-detailed","ultra high res","highly detailed",
        "extremely detailed","intricate","intricate details","finely detailed",
        "very detailed","super detailed","detailed",
        "4k","8k","16k","2k","uhd","hd","hdr","ultra hd","ultra-high resolution",
        "very aesthetic","aesthetic","most aesthetic","aesthetic quality",
        "newest","oldest","recent","mid","early","late",
        "official art","game cg","quality"
    };

    private static readonly Regex RxLora = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex RxNewline = new("[\r\n]+", RegexOptions.Compiled);
    private static readonly Regex RxBreak = new("\\bbreak\\b", RegexOptions.Compiled);
    private static readonly Regex RxGroup = new("[()\\[\\]{}\\\\]", RegexOptions.Compiled);
    private static readonly Regex RxWeight = new(":\\s*[-+]?[0-9]*\\.?[0-9]+", RegexOptions.Compiled);
    private static readonly Regex RxSpace = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex RxNumeric = new("^[\\d\\s.:_+\\-]+$", RegexOptions.Compiled);

    /// <summary>Normalize a prompt into a set of distinct, signal-bearing tokens.</summary>
    public static HashSet<string> TokenSet(string? prompt)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(prompt)) return set;
        var p = prompt.ToLowerInvariant();
        p = RxLora.Replace(p, " ");
        p = RxNewline.Replace(p, ",");
        p = RxBreak.Replace(p, ",");
        foreach (var raw in p.Split(','))
        {
            var t = RxGroup.Replace(raw, "");
            t = RxWeight.Replace(t, "");
            t = RxSpace.Replace(t, " ").Trim();
            if (t.Length == 0) continue;
            if (RxNumeric.IsMatch(t)) continue;
            if (Stopwords.Contains(t)) continue;
            set.Add(t);
        }
        return set;
    }

    public static Dictionary<string, double> CorpusIdf(IReadOnlyCollection<HashSet<string>> tokenSets)
    {
        int n = tokenSets.Count;
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in tokenSets)
            foreach (var k in s)
                df[k] = df.TryGetValue(k, out var c) ? c + 1 : 1;
        var idf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var kv in df)
            idf[kv.Key] = Math.Log((n + 1.0) / (kv.Value + 0.5));
        return idf;
    }

    public static double WeightedJaccard(HashSet<string> a, HashSet<string> b, Dictionary<string, double> idf)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        double inter = 0, union = 0;
        foreach (var k in a)
        {
            double w = idf.TryGetValue(k, out var iw) ? iw : 1.0;
            union += w;
            if (b.Contains(k)) inter += w;
        }
        foreach (var k in b)
        {
            if (a.Contains(k)) continue;
            union += idf.TryGetValue(k, out var iw) ? iw : 1.0;
        }
        return union <= 0 ? 0.0 : inter / union;
    }
}
