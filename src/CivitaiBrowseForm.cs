using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TheTagHag;

/// <summary>
/// T17b — "Browse Civitai": a live preview gallery over the Civitai feed (period/sort/NSFW/min-likes
/// filters), thumbnails streamed straight from the CDN (nothing downloaded). The user selects images
/// and "Import selected" downloads + embeds + indexes only those — no harvest state needed. Reuses
/// the engine's CivitaiClient + download/embed; imported PNGs land in AppPaths.CivitaiDir and are
/// scanned into the library. Set DidImport so MainForm reloads on close.
/// </summary>
public sealed class CivitaiBrowseForm : Form
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AppSettings _s;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly CivitaiClient _client;
    private readonly Dictionary<int, FeedItem> _feedCache = new();
    private readonly Dictionary<int, GenData> _genCache = new();   // gen-data fetched on click (detail panel)
    private List<int>? _followedIds;   // cached on first 'Followed only' request
    private readonly HashSet<string> _reacted = new();   // "{id}:{reaction}" already sent — never re-toggle
    private volatile bool _closing;

    private string ImagesDir => Path.Combine(AppPaths.CivitaiDir, "images");
    private string DatasetFile => Path.Combine(AppPaths.CivitaiDir, "dataset.jsonl");

    public bool DidImport { get; private set; }

    public CivitaiBrowseForm(AppSettings settings)
    {
        _s = settings;
        _client = new CivitaiClient(_s.ApiKey, _ => { });
        Text = "Browse Civitai";
        AppIcon.Apply(this);
        BackColor = Color.FromArgb(0x14, 0x10, 0x18);
        StartPosition = FormStartPosition.CenterParent;
        Width = 1100; Height = 800;
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
        FormClosing += (_, _) => _closing = true;
        FormClosed += (_, _) => _client.Dispose();
    }

    private async Task InitAsync()
    {
        await _web.EnsureCoreWebView2Async(null);
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.CoreWebView2.WebMessageReceived += OnWebMessage;

        var uiDir = Path.Combine(AppPaths.ExeDir, "ui-browse");
        Directory.CreateDirectory(uiDir);
        File.WriteAllText(Path.Combine(uiDir, "index.html"), LoadTemplate());
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", uiDir, CoreWebView2HostResourceAccessKind.Allow);
        _web.CoreWebView2.Navigate("https://app.local/index.html");

        if (string.IsNullOrWhiteSpace(_s.ApiKey))
            Reply(new { type = "status", text = "No Civitai API key — set it in Settings first." });
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString() ?? ""; } catch { return; }
        try
        {
            using var d = JsonDocument.Parse(msg);
            var type = d.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "feed") _ = HandleFeed(d.RootElement);
            else if (type == "import")
            {
                var ids = d.RootElement.TryGetProperty("ids", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>();
                _ = HandleImport(ids);
            }
            else if (type == "react" && d.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
            {
                var reaction = d.RootElement.TryGetProperty("reaction", out var rr) ? rr.GetString() ?? "Like" : "Like";
                _ = HandleReact(idEl.GetInt32(), reaction);
            }
            else if (type == "inspect" && d.RootElement.TryGetProperty("id", out var ii) && ii.ValueKind == JsonValueKind.Number)
            {
                _ = HandleInspect(ii.GetInt32());
            }
            else if (type == "copy")
            {
                var cpy = d.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                if (!string.IsNullOrEmpty(cpy)) { try { Clipboard.SetText(cpy); } catch { } }
            }
        }
        catch { }
    }

    private async Task HandleFeed(JsonElement root)
    {
        if (string.IsNullOrWhiteSpace(_s.ApiKey)) { Reply(new { type = "status", text = "No Civitai API key — set it in Settings." }); return; }

        var o = new HarvestOptions
        {
            Period = root.TryGetProperty("period", out var p) ? p.GetString() ?? "AllTime" : "AllTime",
            Sort = root.TryGetProperty("sort", out var s) ? s.GetString() ?? "Most Reactions" : "Most Reactions",
            Nsfw = root.TryGetProperty("nsfw", out var n) ? n.GetString() ?? "X" : "X",
            PageSize = 100,
        };
        int minLikes = root.TryGetProperty("minLikes", out var ml) && ml.ValueKind == JsonValueKind.Number ? ml.GetInt32() : 0;
        string? cursor = root.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        bool followed = root.TryGetProperty("followed", out var f) && f.ValueKind == JsonValueKind.True;

        try
        {
            if (followed)
            {
                // Rebuild the Following feed from the followed-user list (tRPC followed feed clamps to
                // SFW under API-key auth). Cursor = index into the followed-id list (one creator per page).
                _followedIds ??= await _client.GetFollowingUserIds();
                if (_followedIds.Count == 0) { Reply(new { type = "feedpage", items = Array.Empty<object>(), nextCursor = (string?)null }); Reply(new { type = "status", text = "You don't follow anyone on Civitai." }); return; }
                int ui = 0; int.TryParse(cursor, out ui);
                if (ui >= _followedIds.Count) { Reply(new { type = "feedpage", items = Array.Empty<object>(), nextCursor = (string?)null }); return; }
                // Newest-200 per creator (intentional: browse creator-by-creator rather than exhaust one prolific creator before showing the next).
                var upage = await _client.GetUserImagesPage(_followedIds[ui], o.Nsfw, o.Sort, 200, null);
                var next = ui + 1 < _followedIds.Count ? (ui + 1).ToString() : null;
                Reply(new { type = "feedpage", items = BuildItems(upage.Items, minLikes), nextCursor = next });
                return;
            }

            var page = await _client.GetFeedPage(o, cursor);
            Reply(new { type = "feedpage", items = BuildItems(page.Items, minLikes), nextCursor = page.NextCursor });
        }
        catch (Exception ex) { Reply(new { type = "feederror", text = "Feed error: " + ex.Message }); }
    }

    /// <summary>Project feed items to the browse payload (cache them for import, mark already-imported).</summary>
    private List<object> BuildItems(List<FeedItem> feed, int minLikes)
    {
        var items = new List<object>();
        foreach (var it in feed)
        {
            if (it.Type is not null && it.Type != "image") continue;
            if (it.Likes < minLikes) continue;
            _feedCache[it.Id] = it;
            items.Add(new
            {
                id = it.Id,
                thumb = CivitaiClient.ThumbUrl(it.Url, 450),
                likes = it.Likes,
                hearts = it.Hearts,
                laugh = it.Laugh,
                cry = it.Cry,
                imported = File.Exists(Path.Combine(ImagesDir, $"{it.Id}.png"))
            });
        }
        return items;
    }

    /// <summary>Detail panel: fetch (and cache) the image's gen-data + project prompt/checkpoint/LoRAs.
    /// LoRAs come from the Civitai resources (their prompts rarely carry &lt;lora:&gt; tags).</summary>
    private async Task HandleInspect(int id)
    {
        if (!_feedCache.TryGetValue(id, out var fi)) return;
        GenData gen;
        if (!_genCache.TryGetValue(id, out gen!))
        {
            try { gen = await _client.GetGenerationData(id); _genCache[id] = gen; }
            catch (Exception ex) { Reply(new { type = "status", text = "Detail fetch failed: " + ex.Message }); return; }
        }

        var model = !string.IsNullOrEmpty(gen.Model) ? gen.Model : gen.Resources.FirstOrDefault(r => r.type == "Checkpoint")?.name;
        var loras = gen.Resources
            .Where(r => r.type is not null && (r.type.Contains("lora", StringComparison.OrdinalIgnoreCase)
                || r.type.Contains("locon", StringComparison.OrdinalIgnoreCase)
                || r.type.Contains("lyco", StringComparison.OrdinalIgnoreCase)
                || r.type.Contains("dora", StringComparison.OrdinalIgnoreCase)))
            .Select(r => new { name = r.name, weight = r.weight?.ToString() })
            .ToArray();

        Reply(new
        {
            type = "inspect",
            id,
            preview = CivitaiClient.ThumbUrl(fi.Url, 700),
            positive = gen.Prompt,
            negative = gen.NegativePrompt,
            model,
            sampler = gen.Sampler,
            steps = gen.Steps,
            cfg = gen.CfgScale,
            seed = gen.Seed,
            loras,
            likes = fi.Likes,
            width = fi.Width,
            height = fi.Height,
            nsfw = fi.NsfwLevel
        });
    }

    private async Task HandleReact(int id, string reaction)
    {
        var key = $"{id}:{reaction}";
        if (_reacted.Contains(key)) { Reply(new { type = "reacted", id, reaction, ok = true }); return; } // already sent — never re-toggle
        try { await _client.ReactAsync(id, reaction); _reacted.Add(key); Reply(new { type = "reacted", id, reaction, ok = true }); }
        catch (Exception ex) { Reply(new { type = "reacted", id, reaction, ok = false, error = ex.Message }); }
    }

    private async Task HandleImport(int[] ids)
    {
        if (ids.Length == 0) { Reply(new { type = "importdone", text = "Nothing selected." }); return; }

        int done = 0, failed = 0, skipped = 0;
        var importedIds = new List<int>();
        try
        {
        Directory.CreateDirectory(ImagesDir);

        for (int idx = 0; idx < ids.Length; idx++)
        {
            var id = ids[idx];
            Reply(new { type = "status", text = $"Importing {idx + 1}/{ids.Length}…" });
            var path = Path.Combine(ImagesDir, $"{id}.png");
            if (File.Exists(path)) { skipped++; importedIds.Add(id); continue; }
            if (!_feedCache.TryGetValue(id, out var fi)) { failed++; continue; }

            try
            {
                var gen = await _client.GetGenerationData(id);
                var model = !string.IsNullOrEmpty(gen.Model) ? gen.Model
                    : gen.Resources.FirstOrDefault(r => r.type == "Checkpoint")?.name;
                var tags = await _client.GetImageTags(id);
                var rec = new ImageRecord
                {
                    id = id, likes = fi.Likes, hearts = fi.Hearts, reactionCount = fi.ReactionCount,
                    nsfwLevel = fi.NsfwLevel, width = fi.Width, height = fi.Height, baseModel = fi.BaseModel,
                    prompt = gen.Prompt, negativePrompt = gen.NegativePrompt, sampler = gen.Sampler, steps = gen.Steps,
                    cfgScale = gen.CfgScale, seed = gen.Seed, model = model, resources = gen.Resources, tags = tags,
                    civitaiUrl = $"https://civitai.com/images/{id}", imageFile = $"images/{id}.png",
                    harvestedAt = DateTime.Now.ToString("o"),
                };
                var jpeg = await _client.DownloadImageJpeg(CivitaiClient.GuidFromUrl(fi.Url), 1024);
                var png = await Task.Run(() => PngWriter.TranscodeAndEmbed(jpeg, A1111.BuildParams(rec)));
                await File.WriteAllBytesAsync(path, png);
                await File.AppendAllTextAsync(DatasetFile, JsonSerializer.Serialize(rec, JsonX.Camel) + "\n");
                importedIds.Add(id); done++;
            }
            catch { failed++; }
        }

        // Make sure the harvest folder is a source root, then index the new images into the library.
        if (importedIds.Count > 0)
        {
            if (!_s.SourceRoots.Contains(AppPaths.CivitaiDir, StringComparer.OrdinalIgnoreCase))
            { _s.SourceRoots.Add(AppPaths.CivitaiDir); SettingsStore.Save(_s); }
            Reply(new { type = "status", text = "Indexing into library…" });
            await Task.Run(() => { using var db = new LibraryDb(AppPaths.LibraryDbFile); new LocalScanner(db).Scan(new[] { AppPaths.CivitaiDir }); });
            DidImport = true;
        }

        }
        catch (Exception ex) { Reply(new { type = "status", text = "Import error: " + ex.Message }); }
        finally
        {
            // Always send importdone so the browse window clears its 'busy' state even on failure.
            Reply(new { type = "imported", ids = importedIds });
            Reply(new { type = "importdone", text = $"Imported {done}" + (skipped > 0 ? $", {skipped} already had" : "") + (failed > 0 ? $", {failed} failed" : "") + "." });
        }
    }

    private void Reply(object o)
    {
        if (_closing) return;
        try
        {
            var json = JsonSerializer.Serialize(o, Json);
            if (InvokeRequired) BeginInvoke(() => { try { _web.CoreWebView2?.PostWebMessageAsString(json); } catch { } });
            else _web.CoreWebView2?.PostWebMessageAsString(json);
        }
        catch { }
    }

    private static string LoadTemplate()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("browse.template.html", StringComparison.OrdinalIgnoreCase));
        if (name is null) return "<html><body style='background:#141018;color:#D8D0BF'>browse template missing</body></html>";
        using var rd = new StreamReader(asm.GetManifestResourceStream(name)!);
        return rd.ReadToEnd().Replace("__VERSION__", AppInfo.Version);
    }
}
