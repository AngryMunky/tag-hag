using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheTagHag;

public sealed record FeedItem(
    int Id, string? Type, int Likes, int Hearts, int Laugh, int Cry, int Dislike,
    string? Url, string? NsfwLevel, int Width, int Height, string? BaseModel)
{
    public int ReactionCount => Likes + Hearts + Laugh + Cry + Dislike;
}

public sealed record FeedPage(List<FeedItem> Items, string? NextCursor);

public sealed record GenData(
    string Prompt, string NegativePrompt, string? Sampler, int Steps,
    double? CfgScale, long? Seed, string? Model, List<ResourceRef> Resources);

public sealed record CollectionRef(string Requested, string Canonical, int Id);
public sealed record CollectionInfo(int Id, string Name, int Count);

/// <summary>
/// Typed wrapper over Civitai's REST v1 + tRPC endpoints with retry/backoff. Ported verbatim from
/// CivitaiHarvesterApp (T17 retain-only). REST v1 for feeds (honors nsfw); tRPC for gen-data/tags/
/// collections.
/// </summary>
public sealed class CivitaiClient : IDisposable
{
    private const string FeedUrl = "https://civitai.com/api/v1/images";
    private const string GenUrl = "https://civitai.com/api/trpc/image.getGenerationData";
    private const string TagUrl = "https://civitai.com/api/trpc/tag.getVotableTags";
    private const string CollUrl = "https://civitai.com/api/trpc/collection.getAllUser";
    private const string FollowUrl = "https://civitai.com/api/trpc/user.getFollowingUsers";
    private const string McpUrl = "https://mcp.civitai.com/mcp";
    private const string ImgCdn = "https://image.civitai.com/xG1nkqKTMzGDvpLrqFT7WA";

    private readonly HttpClient _http;
    private readonly Action<HarvestEvent> _log;

    public CivitaiClient(string apiKey, Action<HarvestEvent> log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TheTagHag/1.0");
    }

    private async Task<T> WithRetry<T>(Func<Task<T>> action, string what, int maxAttempts = 4)
    {
        for (int i = 1; ; i++)
        {
            try { return await action(); }
            catch (Exception ex)
            {
                if (i >= maxAttempts) throw;
                _log(new("WARN", $"  {what} attempt {i} failed ({ex.Message}); retrying..."));
                await Task.Delay(500 * i);
            }
        }
    }

    private async Task<JsonDocument> GetJson(string url, string what)
        => await WithRetry(async () =>
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return JsonDocument.Parse(bytes);
        }, what);

    private static string Enc(string s) => Uri.EscapeDataString(s);

    public async Task<FeedPage> GetFeedPage(HarvestOptions o, string? cursor)
    {
        var qs = $"limit={o.PageSize}&nsfw={Enc(o.Nsfw)}&sort={Enc(o.Sort)}&period={o.Period}";
        if (o.TagId > 0) qs += $"&tags={o.TagId}";
        if (!string.IsNullOrEmpty(cursor)) qs += $"&cursor={Enc(cursor)}";
        using var doc = await GetJson($"{FeedUrl}?{qs}", "feed");
        return ParseFeed(doc.RootElement);
    }

    public async Task<FeedPage> GetCollectionPage(HarvestOptions o, int collectionId, string? cursor, string colSort = "Newest")
    {
        var qs = $"collectionId={collectionId}&limit={o.CollectionPageSize}&nsfw={Enc(o.Nsfw)}&sort={Enc(colSort)}";
        if (!string.IsNullOrEmpty(cursor)) qs += $"&cursor={Enc(cursor)}";
        using var doc = await GetJson($"{FeedUrl}?{qs}", $"collection {collectionId}");
        return ParseFeed(doc.RootElement);
    }

    private static FeedPage ParseFeed(JsonElement root)
    {
        var items = new List<FeedItem>();
        foreach (var it in root.Arr("items"))
        {
            var stats = it.TryProp("stats", out var s) ? s : default;
            items.Add(new FeedItem(
                Id: it.IntOr("id"),
                Type: it.Str("type"),
                Likes: stats.ValueKind == JsonValueKind.Object ? stats.IntOr("likeCount") : 0,
                Hearts: stats.ValueKind == JsonValueKind.Object ? stats.IntOr("heartCount") : 0,
                Laugh: stats.ValueKind == JsonValueKind.Object ? stats.IntOr("laughCount") : 0,
                Cry: stats.ValueKind == JsonValueKind.Object ? stats.IntOr("cryCount") : 0,
                Dislike: stats.ValueKind == JsonValueKind.Object ? stats.IntOr("dislikeCount") : 0,
                Url: it.Str("url"),
                NsfwLevel: it.Str("nsfwLevel"),
                Width: it.IntOr("width"),
                Height: it.IntOr("height"),
                BaseModel: it.Str("baseModel")));
        }
        string? next = null;
        if (root.TryProp("metadata", out var meta)) next = meta.Str("nextCursor");
        return new FeedPage(items, next);
    }

    public async Task<List<CollectionRef>> ResolveCollectionIds(IEnumerable<string> names)
    {
        var input = Enc("{\"json\":{\"limit\":100}}");
        using var doc = await GetJson($"{CollUrl}?input={input}", "collection.getAllUser");
        var all = doc.RootElement.GetProperty("result").GetProperty("data").GetProperty("json");

        var resolved = new List<CollectionRef>();
        foreach (var name in names)
        {
            var n = name.Trim();
            foreach (var c in all.EnumerateArray())
            {
                if (string.Equals(c.Str("name"), n, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.Str("type"), "Image", StringComparison.Ordinal))
                {
                    resolved.Add(new CollectionRef(n, c.Str("name") ?? n, c.IntOr("id")));
                    _log(new("INFO", $"  Resolved collection '{n}' -> '{c.Str("name")}' (id={c.IntOr("id")})"));
                    goto next;
                }
            }
            _log(new("WARN", $"  Collection '{n}' not found (or not an Image collection). Skipping."));
            next: ;
        }
        return resolved;
    }

    public async Task<List<CollectionInfo>> ListImageCollections()
    {
        var input = Enc("{\"json\":{\"limit\":100}}");
        using var doc = await GetJson($"{CollUrl}?input={input}", "collection.getAllUser");
        var all = doc.RootElement.GetProperty("result").GetProperty("data").GetProperty("json");
        var list = new List<CollectionInfo>();
        if (all.ValueKind != JsonValueKind.Array) return list;
        foreach (var c in all.EnumerateArray())
        {
            if (!string.Equals(c.Str("type"), "Image", StringComparison.Ordinal)) continue;
            var name = c.Str("name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            int count = c.IntOr("imageCount", -1);
            if (count < 0 && c.TryProp("_count", out var cc)) count = cc.IntOr("items", -1);
            list.Add(new CollectionInfo(c.IntOr("id"), name, count));
        }
        return list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<GenData> GetGenerationData(int imageId)
    {
        var input = Enc($"{{\"json\":{{\"id\":{imageId}}}}}");
        using var doc = await GetJson($"{GenUrl}?input={input}", $"genData {imageId}");
        var j = doc.RootElement.GetProperty("result").GetProperty("data").GetProperty("json");
        var meta = j.TryProp("meta", out var m) ? m : default;

        var resources = new List<ResourceRef>();
        foreach (var r in j.Arr("resources"))
        {
            var name = r.Str("modelName");
            if (string.IsNullOrEmpty(name)) continue;
            resources.Add(new ResourceRef
            {
                name = name,
                type = r.Str("modelType"),
                version = r.Str("versionName"),
                baseModel = r.Str("baseModel"),
                weight = r.DoubleOrNull("strength"),
            });
        }

        return new GenData(
            Prompt: meta.ValueKind == JsonValueKind.Object ? (meta.Str("prompt") ?? "") : "",
            NegativePrompt: meta.ValueKind == JsonValueKind.Object ? (meta.Str("negativePrompt") ?? "") : "",
            Sampler: meta.ValueKind == JsonValueKind.Object ? meta.Str("sampler") : null,
            Steps: meta.ValueKind == JsonValueKind.Object ? meta.IntOr("steps") : 0,
            CfgScale: meta.ValueKind == JsonValueKind.Object ? meta.DoubleOrNull("cfgScale") : null,
            Seed: meta.ValueKind == JsonValueKind.Object ? meta.LongOrNull("seed") : null,
            Model: meta.ValueKind == JsonValueKind.Object ? meta.Str("Model") : null,
            Resources: resources);
    }

    public async Task<List<string>> GetImageTags(int imageId)
    {
        try
        {
            var input = Enc($"{{\"json\":{{\"id\":{imageId},\"type\":\"image\"}}}}");
            using var doc = await GetJson($"{TagUrl}?input={input}", $"tags {imageId}");
            var arr = doc.RootElement.GetProperty("result").GetProperty("data").GetProperty("json");
            if (arr.ValueKind != JsonValueKind.Array) return new();
            return arr.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.Object && t.TryProp("name", out _))
                .OrderByDescending(t => t.DoubleOrNull("score") ?? 0)
                .Select(t => t.Str("name") ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            _log(new("WARN", $"  tags fetch failed for image {imageId} (continuing without tags): {ex.Message}"));
            return new();
        }
    }

    public async Task<byte[]> DownloadImageJpeg(string guid, int imageWidth)
    {
        var render = imageWidth > 0 ? $"width={imageWidth}" : "original=true";
        var url = $"{ImgCdn}/{guid}/{render}/image.jpeg";
        return await WithRetry(async () =>
        {
            var b = await _http.GetByteArrayAsync(url);
            if (b.Length < 1024) throw new InvalidDataException($"download too small ({b.Length}b)");
            if (b[0] != 0xFF || b[1] != 0xD8) throw new InvalidDataException("not a JPEG");
            if (b[^2] != 0xFF || b[^1] != 0xD9) throw new InvalidDataException("truncated JPEG (no EOI marker)");
            return b;
        }, $"download {guid}");
    }

    public static string GuidFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var m = Regex.Match(url, "/([0-9a-fA-F-]{36})/");
        if (m.Success) return m.Groups[1].Value;
        var parts = url.Split('/');
        return parts[^1];
    }

    /// <summary>A width-capped CDN thumbnail URL for a feed item's image URL (preview, no download).</summary>
    public static string ThumbUrl(string? url, int width)
    {
        var guid = GuidFromUrl(url);
        return string.IsNullOrEmpty(guid) ? (url ?? "") : $"{ImgCdn}/{guid}/width={width}/image.jpeg";
    }

    // ---- tRPC: the user IDs you follow (Following feed is tRPC-only) --------
    public async Task<List<int>> GetFollowingUserIds()
    {
        var input = Enc("{\"json\":{}}");
        using var doc = await GetJson($"{FollowUrl}?input={input}", "getFollowingUsers");
        var json = doc.RootElement.GetProperty("result").GetProperty("data").GetProperty("json");
        var ids = new List<int>();
        if (json.ValueKind == JsonValueKind.Array)
            foreach (var e in json.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)) ids.Add(n);
                else if (e.ValueKind == JsonValueKind.Object) ids.Add(e.IntOr("id"));
            }
        return ids;
    }

    // ---- REST v1 feed for ONE creator (userId=). nsfw=X is NSFW-inclusive;
    //      this is how the Following feed is rebuilt with mature content (tRPC clamps to SFW).
    public async Task<FeedPage> GetUserImagesPage(int userId, string nsfw, string sort, int limit, string? cursor)
    {
        var qs = $"userId={userId}&limit={limit}&nsfw={Enc(nsfw)}&sort={Enc(sort)}";
        if (!string.IsNullOrEmpty(cursor)) qs += $"&cursor={Enc(cursor)}";
        using var doc = await GetJson($"{FeedUrl}?{qs}", $"user {userId} images");
        return ParseFeed(doc.RootElement);
    }

    private static readonly string[] ValidReactions = { "Like", "Heart", "Laugh", "Cry", "Dislike" };

    /// <summary>The exact JSON-RPC body for a react call — key must be literally "params". Extracted
    /// so the shape is unit-testable (no serializer field-name ambiguity). reaction is validated by the caller.</summary>
    public static string BuildReactPayload(int imageId, string reaction) =>
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"react\"," +
        $"\"arguments\":{{\"entityType\":\"image\",\"entityId\":{imageId},\"reaction\":\"{reaction}\"}}}}}}";

    // ---- MCP: react to an image. FIRE-AND-FORGET + a TOGGLE (re-reacting removes it),
    //      so the caller must avoid sending the same reaction twice. reaction = Like|Heart|Laugh|Cry.
    public async Task ReactAsync(int imageId, string reaction)
    {
        if (Array.IndexOf(ValidReactions, reaction) < 0)
            throw new ArgumentException($"Invalid reaction: {reaction}");

        var payload = BuildReactPayload(imageId, reaction);
        using var req = new HttpRequestMessage(HttpMethod.Post, McpUrl)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        // MCP can return HTTP 200 with a JSON-RPC error in the body — surface it (the PS routine
        // checks $resp.error the same way). A non-JSON (SSE) body is treated as success.
        var body = await resp.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(body))
        {
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(body); } catch (JsonException) { }
            if (doc is not null)
                using (doc)
                    if (doc.RootElement.TryProp("error", out var err) && err.ValueKind == JsonValueKind.Object)
                        throw new InvalidOperationException($"react error: {err.Str("message") ?? "rejected"}");
        }
    }

    public void Dispose() => _http.Dispose();
}
