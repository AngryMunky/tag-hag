using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheTagHag;

/// <summary>
/// Best-effort ComfyUI prompt-graph parser (R1 — accepted as best-effort for v1). Works on
/// the API "prompt" map: { "&lt;nodeId&gt;": { class_type, inputs{...}, _meta{title} } }.
/// Positive/negative disambiguation is layered:
///   1. _meta.title keywords ("positive"/"negative") — the strongest signal in practice.
///   2. sampler-link trace: follow the sampler node's positive/negative input links to the
///      nearest upstream node carrying a literal text.
///   3. fallback: among literal text nodes, longest = positive; a negative-sounding one = negative.
/// Sampler params (steps/cfg/seed) come from a *Sampler node (or any node with steps+cfg);
/// model from ckpt_name/unet_name. Raw JSON is preserved for the lightbox. Never throws.
/// </summary>
public static class ComfyGraphParser
{
    private static readonly string[] TextFields =
        { "populated_text", "wildcard_text", "text", "string", "prompt", "text_g", "text_l", "value" };
    private static readonly Regex LoraOnly = new(@"^\s*(<lora:[^>]+>\s*)+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] NegativeWords = { "worst quality", "low quality", "score_1", "score_2", "lowres", "bad anatomy" };

    public static ParsedMeta Parse(string promptJson, string? rawForView = null)
    {
        var meta = new ParsedMeta { Format = "comfyui", Source = "embedded", RawJson = rawForView ?? promptJson };
        try
        {
            using var doc = JsonDocument.Parse(promptJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return meta;

            var nodes = new List<Node>();
            foreach (var p in root.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                nodes.Add(Node.From(p.Name, p.Value));
            }

            // --- sampler params + model ---
            foreach (var n in nodes)
            {
                if (meta.Steps == 0 && (n.ClassType.Contains("sampler", StringComparison.OrdinalIgnoreCase)
                                        || (n.HasNum("steps") && n.HasNum("cfg"))))
                {
                    meta.Steps = n.Int("steps") ?? meta.Steps;
                    meta.Cfg = n.Dbl("cfg") ?? meta.Cfg;
                    meta.Seed = n.Long("seed") ?? n.Long("noise_seed") ?? meta.Seed;
                    if (n.ClassType.Length > 0) meta.Sampler = n.Str("sampler_name") ?? meta.Sampler;
                }
                if (meta.Model is null)
                {
                    var ckpt = n.Str("ckpt_name") ?? n.Str("unet_name") ?? n.Str("model_name");
                    if (ckpt is not null) meta.Model = Path.GetFileNameWithoutExtension(ckpt);
                }
            }

            // --- positive / negative text ---
            var posCands = new List<string>();
            var negCands = new List<string>();
            foreach (var n in nodes)
            {
                var text = n.LiteralText();
                if (text is null) continue;
                var title = n.Title.ToLowerInvariant();
                if (title.Contains("neg")) negCands.Add(text);
                else if (title.Contains("pos")) posCands.Add(text);
            }

            string? positive = Longest(posCands);
            string? negative = Longest(negCands);

            // Fallback 1: sampler-link trace when titles didn't classify.
            if (positive is null || negative is null)
            {
                var sampler = nodes.FirstOrDefault(n => n.HasLink("positive") && n.HasLink("negative"));
                if (sampler is not null)
                {
                    positive ??= TraceText(sampler.Link("positive"), nodes, new HashSet<string>());
                    negative ??= TraceText(sampler.Link("negative"), nodes, new HashSet<string>());
                }
            }

            // Fallback 2: among all literal-text nodes, longest = positive; negative-sounding = negative.
            if (positive is null || negative is null)
            {
                var all = nodes.Select(n => n.LiteralText()).Where(t => t is not null).Select(t => t!).ToList();
                positive ??= Longest(all);
                negative ??= all.FirstOrDefault(t => NegativeWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)) && t != positive);
            }

            meta.Prompt = positive ?? "";
            meta.Negative = negative ?? "";
        }
        catch { /* best-effort: keep format=comfyui + raw json, empty prompt */ }
        return meta;
    }

    private static string? Longest(List<string> xs) => xs.Count == 0 ? null : xs.OrderByDescending(s => s.Length).First();

    /// <summary>Follow a [nodeId, slot] link to the nearest upstream node with a literal text.</summary>
    private static string? TraceText(string? nodeId, List<Node> nodes, HashSet<string> seen)
    {
        if (nodeId is null || !seen.Add(nodeId)) return null;
        var n = nodes.FirstOrDefault(x => x.Id == nodeId);
        if (n is null) return null;
        var lit = n.LiteralText();
        if (lit is not null) return lit;
        foreach (var key in new[] { "text", "conditioning", "positive", "negative", "string" })
        {
            var l = n.Link(key);
            if (l is not null) { var r = TraceText(l, nodes, seen); if (r is not null) return r; }
        }
        return null;
    }

    /// <summary>One graph node + typed accessors over its `inputs`.</summary>
    private sealed class Node
    {
        public string Id = "";
        public string ClassType = "";
        public string Title = "";
        private JsonElement _inputs;
        private bool _hasInputs;

        public static Node From(string id, JsonElement node)
        {
            var n = new Node { Id = id };
            if (node.TryGetProperty("class_type", out var ct) && ct.ValueKind == JsonValueKind.String) n.ClassType = ct.GetString() ?? "";
            if (node.TryGetProperty("_meta", out var m) && m.ValueKind == JsonValueKind.Object
                && m.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) n.Title = t.GetString() ?? "";
            if (node.TryGetProperty("inputs", out var inp) && inp.ValueKind == JsonValueKind.Object) { n._inputs = inp; n._hasInputs = true; }
            return n;
        }

        public string? LiteralText()
        {
            foreach (var f in TextFields)
            {
                var s = Str(f);
                if (!string.IsNullOrWhiteSpace(s) && !LoraOnly.IsMatch(s)) return s.Trim();
            }
            return null;
        }

        public string? Str(string key) =>
            _hasInputs && _inputs.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        public bool HasNum(string key) =>
            _hasInputs && _inputs.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number;
        public int? Int(string key) => HasNum(key) && _inputs.GetProperty(key).TryGetInt32(out var i) ? i : null;
        public long? Long(string key) => HasNum(key) && _inputs.GetProperty(key).TryGetInt64(out var i) ? i : null;
        public double? Dbl(string key) => HasNum(key) && _inputs.GetProperty(key).TryGetDouble(out var d) ? d : null;

        /// <summary>True if input <paramref name="key"/> is a [nodeId, slot] link array.</summary>
        public bool HasLink(string key) => Link(key) is not null;
        public string? Link(string key)
        {
            if (!_hasInputs || !_inputs.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            var first = v.EnumerateArray().FirstOrDefault();
            return first.ValueKind == JsonValueKind.String ? first.GetString() : null;
        }
    }
}
