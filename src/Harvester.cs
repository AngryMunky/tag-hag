using System.Text;
using System.Text.Json;

namespace TheTagHag;

/// <summary>
/// Civitai harvest engine (T17, retain-only) — ported from CivitaiHarvesterApp. Feed loop +
/// collection loop, prompt-similarity dedup, A1111 iTXt PNG embedding, state.json dedupe. Writes
/// to AppPaths.CivitaiDir (images\ + dataset.jsonl + state.json); those PNGs carry embedded A1111
/// metadata, so adding that folder as a source root indexes them into the library. No standalone
/// catalog (the Tag Hag gallery replaces it).
/// </summary>
public sealed class Harvester
{
    private readonly AppSettings _settings;
    private readonly Action<HarvestEvent> _log;

    private string OutputDir => AppPaths.CivitaiDir;
    private string ImagesDir => Path.Combine(OutputDir, "images");
    private string DatasetFile => Path.Combine(OutputDir, "dataset.jsonl");
    private string StateFile => Path.Combine(OutputDir, "state.json");

    private readonly List<(int id, HashSet<string> tokens)> _active = new();
    private Dictionary<string, double> _libIdf = new();
    private Action<HarvestProgress>? _progress;

    private void Report(string phase, int current, int total) => _progress?.Invoke(new(phase, current, total));

    public Harvester(AppSettings settings, Action<HarvestEvent> log)
    {
        _settings = settings;
        _log = msg =>
        {
            log(msg);
            try { File.AppendAllText(AppPaths.LogFile, msg + Environment.NewLine, new UTF8Encoding(false)); } catch { }
        };
    }

    public async Task<(int feed, int collections)> Run(HarvestOptions o, CancellationToken ct = default,
        Action<HarvestProgress>? progress = null)
    {
        _progress = progress;
        var mode = o.DryRun ? "DRY-RUN" : "LIVE";
        var modes = new List<string>();
        if (!o.SkipFeed) modes.Add("feed");
        if (o.Collections) modes.Add($"collections[{o.CollectionNames}]");
        if (modes.Count == 0) modes.Add("(no harvest mode active)");
        var tagLabel = o.TagId > 0 ? o.TagId.ToString() : "ALL";
        Log($"=== {AppInfo.Name} v{AppInfo.Version} harvest start [{mode}] modes={string.Join("+", modes)} tag={tagLabel} nsfw={o.Nsfw} ===");

        var key = _settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Civitai API key configured. Open Settings and paste your key.");

        if (!o.DryRun) Directory.CreateDirectory(ImagesDir);

        using var client = new CivitaiClient(key, _log);
        var state = StateStore.Load(StateFile, DatasetFile, _log);
        Log($"State: {state.Harvested.Count} already harvested, {state.Skipped.Count} previously skipped.");

        LoadSimilarityLibrary(o);

        int feed = 0, cols = 0;
        if (o.Collections) cols = await HarvestCollections(client, state, o, ct);
        if (!o.SkipFeed) feed = await HarvestFeed(client, state, o, ct);

        if (!o.DryRun) StateStore.Save(StateFile, state);

        int grand = feed + cols;
        var verb = o.DryRun ? "would harvest" : "harvested";
        Log($"=== Done [{mode}]: feed={feed}, collections={cols}, total={grand} {verb}. ===");
        return (feed, cols);
    }

    private void LoadSimilarityLibrary(HarvestOptions o)
    {
        if (o.SimilarityThreshold <= 0) { Log("Similarity check disabled (SimilarityThreshold=0)."); return; }
        if (!File.Exists(DatasetFile)) return;

        var sets = new List<HashSet<string>>();
        foreach (var line in File.ReadLines(DatasetFile, Encoding.UTF8))
        {
            if (line.Trim().Length == 0) continue;
            try
            {
                using var d = JsonDocument.Parse(line);
                var root = d.RootElement;
                var prompt = root.Str("prompt");
                if (string.IsNullOrEmpty(prompt)) continue;
                var ts = PromptSimilarity.TokenSet(prompt);
                sets.Add(ts);
                _active.Add((root.IntOr("id"), ts));
            }
            catch { }
        }
        if (sets.Count > 0) _libIdf = PromptSimilarity.CorpusIdf(sets);
        Log($"Similarity: loaded {_active.Count} existing prompt(s), threshold={o.SimilarityThreshold}.");
    }

    private (double score, int id) BestSimilarity(HashSet<string> tokens)
    {
        double best = 0; int bestId = 0;
        foreach (var (id, toks) in _active)
        {
            var sim = PromptSimilarity.WeightedJaccard(tokens, toks, _libIdf);
            if (sim > best) { best = sim; bestId = id; }
        }
        return (best, bestId);
    }

    private async Task<int> HarvestFeed(CivitaiClient client, HarvestState state, HarvestOptions o, CancellationToken ct)
    {
        Log($"--- Feed harvest: sort='{o.Sort}' period={o.Period} likes>{o.LikesMin} cap={o.MaxNew} ---");
        int harvested = 0, skipped = 0, processed = 0, pages = 0;
        int maxProcess = Math.Max(o.MaxNew * 6, 200);
        string? cursor = null;

        do
        {
            ct.ThrowIfCancellationRequested();
            pages++;
            FeedPage data;
            try { data = await client.GetFeedPage(o, cursor); }
            catch (Exception ex) { Log($"feed page {pages} failed: {ex.Message}", "ERROR"); break; }
            if (data.Items.Count == 0) break;

            foreach (var img in data.Items)
            {
                if (harvested >= o.MaxNew || processed >= maxProcess) break;
                ct.ThrowIfCancellationRequested();
                int id = img.Id;
                if (state.Seen(id)) continue;
                if (img.Type != null && img.Type != "image") { state.Skipped.Add(id); continue; }
                if (img.Likes <= o.LikesMin) continue;

                processed++;
                await Task.Delay(o.ThrottleMs, ct);

                GenData gen;
                try { gen = await client.GetGenerationData(id); }
                catch (Exception ex) { Log($"  genData failed for image {id} (skipping this run): {ex.Message}", "WARN"); continue; }

                if (FilterOrSimilar(gen, id, o, state, ref skipped, out var tokenSet)) continue;

                if (await EmitRecord(client, state, o, img, gen, tokenSet!, null) == EmitResult.Harvested)
                    harvested++;
                else
                    skipped++;
                Report("Feed", harvested, o.MaxNew);
            }

            if (harvested >= o.MaxNew || processed >= maxProcess) break;
            cursor = data.NextCursor;
        } while (!string.IsNullOrEmpty(cursor) && pages < o.MaxPages);

        Log($"Feed: scanned {pages} page(s), processed {processed} candidate(s), harvested {harvested}, skipped {skipped}.");
        if (processed >= maxProcess && harvested < o.MaxNew)
            Log($"Hit MaxProcess ceiling ({maxProcess}) before reaching cap; remaining backlog picked up next run.", "WARN");
        return harvested;
    }

    private async Task<int> HarvestCollections(CivitaiClient client, HarvestState state, HarvestOptions o, CancellationToken ct)
    {
        var requested = o.CollectionNames.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        Log($"--- Collection harvest: resolving {requested.Count} collection(s) ---");

        List<CollectionRef> map;
        try { map = await client.ResolveCollectionIds(requested); }
        catch (Exception ex) { Log($"Failed to resolve collections: {ex.Message}", "ERROR"); return 0; }

        int total = 0;
        foreach (var col in map)
        {
            ct.ThrowIfCancellationRequested();
            Log($"--- Collection: '{col.Canonical}' (id={col.Id}) ---");
            string? cursor = null;
            int page = 0, harvested = 0, skipped = 0, processed = 0, consecutiveSeen = 0;
            bool earlyExit = false;

            do
            {
                page++;
                FeedPage data;
                try { data = await client.GetCollectionPage(o, col.Id, cursor); }
                catch (Exception ex) { Log($"  collection page {page} failed: {ex.Message}", "ERROR"); break; }
                if (data.Items.Count == 0) break;

                foreach (var img in data.Items)
                {
                    if (earlyExit) break;
                    ct.ThrowIfCancellationRequested();
                    int id = img.Id;

                    if (state.Seen(id))
                    {
                        consecutiveSeen++;
                        if (o.CollectionEarlyExitSeen > 0 && consecutiveSeen >= o.CollectionEarlyExitSeen)
                        {
                            Log($"  {consecutiveSeen} consecutive already-seen images; early-exit collection '{col.Canonical}'.");
                            earlyExit = true;
                        }
                        continue;
                    }
                    consecutiveSeen = 0;

                    if (img.Type != null && img.Type != "image") { state.Skipped.Add(id); continue; }

                    processed++;
                    await Task.Delay(o.ThrottleMs, ct);

                    GenData gen;
                    try { gen = await client.GetGenerationData(id); }
                    catch (Exception ex) { Log($"  genData failed for image {id} (skipping this run): {ex.Message}", "WARN"); continue; }

                    if (FilterOrSimilar(gen, id, o, state, ref skipped, out var tokenSet)) continue;

                    if (await EmitRecord(client, state, o, img, gen, tokenSet!, col.Canonical) == EmitResult.Harvested)
                    {
                        harvested++;
                        if (o.MaxPerCollection > 0 && harvested >= o.MaxPerCollection)
                        {
                            Log($"  Reached MaxPerCollection ({o.MaxPerCollection}) for '{col.Canonical}'.");
                            earlyExit = true;
                        }
                    }
                    else { skipped++; }
                    Report(col.Canonical, harvested, o.MaxPerCollection);
                }

                if (earlyExit) break;
                cursor = data.NextCursor;
            } while (!string.IsNullOrEmpty(cursor) && page < o.MaxPages);

            Log($"  Collection '{col.Canonical}': {page} page(s), {processed} processed, {harvested} harvested, {skipped} skipped.");
            total += harvested;
        }
        return total;
    }

    private bool FilterOrSimilar(GenData gen, int id, HarvestOptions o, HarvestState state,
        ref int skipped, out HashSet<string>? tokenSet)
    {
        tokenSet = null;
        var prompt = gen.Prompt;
        if (string.IsNullOrWhiteSpace(prompt) || gen.Steps <= 0)
        {
            var reason = string.IsNullOrWhiteSpace(prompt) ? "no prompt" : $"non-SD (steps=0, sampler={gen.Sampler})";
            if (o.DryRun) Log($"  SKIP image {id} ({reason})");
            else state.Skipped.Add(id);
            skipped++;
            return true;
        }

        tokenSet = PromptSimilarity.TokenSet(prompt);
        if (o.SimilarityThreshold > 0 && _active.Count > 0)
        {
            var (score, bestId) = BestSimilarity(tokenSet);
            if (score >= o.SimilarityThreshold)
            {
                var snip = Snip(prompt, 70);
                Log($"  SIMILAR to image {bestId} (sim={score:F2}) -> skip {id}. [{snip}]");
                if (!o.DryRun) state.Skipped.Add(id);
                skipped++;
                return true;
            }
        }
        return false;
    }

    private enum EmitResult { Harvested, DownloadFailed }

    private async Task<EmitResult> EmitRecord(CivitaiClient client, HarvestState state, HarvestOptions o,
        FeedItem img, GenData gen, HashSet<string> tokenSet, string? collection)
    {
        var model = !string.IsNullOrEmpty(gen.Model)
            ? gen.Model
            : gen.Resources.FirstOrDefault(r => r.type == "Checkpoint")?.name;

        var tags = o.DryRun ? new List<string>() : await client.GetImageTags(img.Id);

        var rec = new ImageRecord
        {
            id = img.Id, likes = img.Likes, hearts = img.Hearts, reactionCount = img.ReactionCount,
            nsfwLevel = img.NsfwLevel, width = img.Width, height = img.Height, baseModel = img.BaseModel,
            prompt = gen.Prompt, negativePrompt = gen.NegativePrompt, sampler = gen.Sampler, steps = gen.Steps,
            cfgScale = gen.CfgScale, seed = gen.Seed, model = model, resources = gen.Resources, tags = tags,
            collection = collection, civitaiUrl = $"https://civitai.com/images/{img.Id}",
            imageFile = $"images/{img.Id}.png", harvestedAt = DateTime.Now.ToString("o"),
        };

        var snip = Snip(gen.Prompt, 90);
        var colTag = collection != null ? $" [{collection}]" : "";

        if (o.DryRun)
        {
            Log($"  WOULD harvest image {img.Id}{colTag}  likes={img.Likes}  nsfw={img.NsfwLevel}  steps={gen.Steps} cfg={Num(gen.CfgScale)} sampler={gen.Sampler}");
            Log($"       prompt: {snip}");
            if (o.SimilarityThreshold > 0) _active.Add((img.Id, tokenSet));
            return EmitResult.Harvested;
        }

        var imgPath = Path.Combine(ImagesDir, $"{img.Id}.png");
        try
        {
            var jpeg = await client.DownloadImageJpeg(CivitaiClient.GuidFromUrl(img.Url), o.ImageWidth);
            var png = PngWriter.TranscodeAndEmbed(jpeg, A1111.BuildParams(rec));
            await File.WriteAllBytesAsync(imgPath, png);
        }
        catch (Exception ex)
        {
            Log($"  download/embed failed for image {img.Id}{colTag} (marking skipped): {ex.Message}", "ERROR");
            state.Skipped.Add(img.Id);
            return EmitResult.DownloadFailed;
        }

        File.AppendAllText(DatasetFile, JsonSerializer.Serialize(rec, JsonX.Camel) + "\n", new UTF8Encoding(false));
        if (o.Sidecars)
            File.WriteAllText(Path.Combine(ImagesDir, $"{img.Id}.txt"), A1111.FormatSidecar(rec), new UTF8Encoding(false));

        state.Harvested.Add(img.Id);
        if (o.SimilarityThreshold > 0) _active.Add((img.Id, tokenSet));
        Log($"  harvested image {img.Id}{colTag}  likes={img.Likes}  [{snip}]");
        return EmitResult.Harvested;
    }

    private static string Snip(string s, int n)
    {
        var one = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ");
        return one.Length > n ? one[..n] + "..." : one;
    }

    private static string Num(double? d) => d?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private void Log(string msg, string level = "INFO") => _log(new(level, msg));
}
