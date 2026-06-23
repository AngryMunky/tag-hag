namespace TheTagHag;

/// <summary>Parsed search query: tag tokens (comma-AND) + literal phrases (quoted).</summary>
public sealed class SearchFilter
{
    public List<string> Tokens { get; } = new();   // ANDed; each matched against image_tags
    public List<string> Phrases { get; } = new();   // ANDed; each matched as prompt LIKE %phrase%
    public bool IsEmpty => Tokens.Count == 0 && Phrases.Count == 0;
}

/// <summary>
/// Parses the search syntax (architecture §6 / UX Flow B):
///   - comma = boolean AND across tags; each unquoted comma-segment is normalized with
///     PromptSimilarity.TokenSet (so it matches image_tags exactly).
///   - double quotes = exact literal substring (commas inside are literal) → a phrase.
///   - mixable: <c>"red dress", smiling</c> → phrase "red dress" AND tag "smiling".
/// </summary>
public static class SearchParser
{
    public static SearchFilter Parse(string? raw)
    {
        var f = new SearchFilter();
        if (string.IsNullOrWhiteSpace(raw)) return f;

        var pending = new System.Text.StringBuilder(); // accumulates an unquoted run
        void FlushUnquoted()
        {
            var run = pending.ToString();
            pending.Clear();
            if (string.IsNullOrWhiteSpace(run)) return;
            foreach (var seg in run.Split(','))
                foreach (var tok in PromptSimilarity.TokenSet(seg))
                    if (!f.Tokens.Contains(tok)) f.Tokens.Add(tok);
        }

        bool inQuote = false;
        var phrase = new System.Text.StringBuilder();
        foreach (var ch in raw)
        {
            if (ch == '"')
            {
                if (inQuote)
                {
                    var p = phrase.ToString().Trim();
                    if (p.Length > 0 && !f.Phrases.Contains(p)) f.Phrases.Add(p);
                    phrase.Clear();
                    inQuote = false;
                }
                else { FlushUnquoted(); inQuote = true; }
            }
            else if (inQuote) phrase.Append(ch);
            else pending.Append(ch);
        }
        if (inQuote) { var p = phrase.ToString().Trim(); if (p.Length > 0 && !f.Phrases.Contains(p)) f.Phrases.Add(p); } // tolerant: unclosed quote
        else FlushUnquoted();

        return f;
    }
}
