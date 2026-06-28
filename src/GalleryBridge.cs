using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheTagHag;

/// <summary>A LoRA reference parsed from a prompt's &lt;lora:name:weight&gt; tag.</summary>
public sealed record LoraRef(string Name, string? Weight);

/// <summary>
/// Translates WebView2 messages (JSON strings) into engine calls and back. Keeps MainForm
/// lean. Stateless except for the LibraryDb + a row→image-URL mapper (virtual-host aware).
///   JS → C#:  {type:'query', raw, page, size}  ·  {type:'ac', prefix}
///   C# → JS:  {type:'page', page, total, items:[{id,url,format,prompt}]}  ·  {type:'ac', items:[{token,df}]}
/// </summary>
public sealed class GalleryBridge
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly LibraryDb _db;
    private readonly Func<ImageRow, string> _urlFor;

    public GalleryBridge(LibraryDb db, Func<ImageRow, string> urlFor) { _db = db; _urlFor = urlFor; }

    /// <summary>Handle one inbound message; returns a JSON reply to post back, or null.</summary>
    public string? Handle(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            return type switch
            {
                "query" => HandleQuery(root),
                "selectall" => HandleSelectAll(root),   // T40/F28 — ids for the whole current view
                "ac" => HandleAutocomplete(root),
                "inspect" => HandleInspect(root),
                "counts" => HandleCounts(),
                // These synchronously RETURN a reply, so they're answered here (not host-side in
                // MainForm). Manual-tag add/del + the collections read. fav/note/col-writes are
                // intercepted in MainForm because they push {type:'reload'} / are silent.
                "tagadd"     => HandleTagAdd(root),
                "tagdel"     => HandleTagDel(root),
                "taglist"    => HandleTagList(),
                "tagbulkadd" => HandleTagBulkAdd(root),
                "cols" => HandleCols(),
                "folders" => HandleFolders(),
                "optimizepreview" => HandleOptimizePreview(root),
                // T45/F32 Potions — stateless synchronous reads + writes; all reply inline.
                "potions"   => HandlePotions(),
                "potcreate" => HandlePotCreate(root),
                "potupdate" => HandlePotUpdate(root),
                "potdelete" => HandlePotDelete(root),
                "potcount"  => HandlePotCount(root),
                _ => null
            };
        }
        catch { return null; }
    }

    private string HandleQuery(JsonElement root)
    {
        var raw = root.TryGetProperty("raw", out var r) ? r.GetString() ?? "" : "";
        var page = root.TryGetProperty("page", out var p) ? p.GetInt32() : 0;
        var size = root.TryGetProperty("size", out var s) ? s.GetInt32() : 60;
        var includeArchived = root.TryGetProperty("includeArchived", out var ia) && ia.ValueKind == JsonValueKind.True;
        var untaggedOnly = root.TryGetProperty("untaggedOnly", out var uo) && uo.ValueKind == JsonValueKind.True;
        var archivedOnly = root.TryGetProperty("archivedOnly", out var ao) && ao.ValueKind == JsonValueKind.True;
        var favoritesOnly = root.TryGetProperty("favoritesOnly", out var fo) && fo.ValueKind == JsonValueKind.True;
        var optimizedOnly = root.TryGetProperty("optimizedOnly", out var oo) && oo.ValueKind == JsonValueKind.True;   // T31
        long? collectionId = root.TryGetProperty("collectionId", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt64() : (long?)null;
        var includeSubcollections = root.TryGetProperty("includeSubcollections", out var isc) && isc.ValueKind == JsonValueKind.True;  // T41
        // Folder view (T33): folderPath is a rel-DIR ("" = root-level), folderRoot scopes it to one
        // source root, includeSubfolders widens to the subtree. Present together or not at all.
        var folderPath = root.TryGetProperty("folderPath", out var fp) && fp.ValueKind == JsonValueKind.String ? fp.GetString() : null;
        var folderRoot = root.TryGetProperty("folderRoot", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;
        var includeSubfolders = root.TryGetProperty("includeSubfolders", out var isf) && isf.ValueKind == JsonValueKind.True;
        // Generation token: the page echoes it so the client can drop replies from a superseded query.
        var gen = root.TryGetProperty("gen", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : 0;

        var (rows, total) = _db.Query(SearchParser.Parse(raw), page, size, includeArchived, untaggedOnly, archivedOnly, favoritesOnly, collectionId, optimizedOnly, folderPath, folderRoot, includeSubfolders, includeSubcollections);
        var items = rows.Select(x => new
        {
            id = x.Id,
            url = _urlFor(x),
            fileName = x.FileName,
            format = x.MetaFormat,
            prompt = x.Prompt,
            favorite = x.Favorite,
            optimized = x.Optimized      // T31 — drives the ✨O card badge
        });
        return JsonSerializer.Serialize(new { type = "page", page, total, gen, items }, Json);
    }

    /// <summary>Return EVERY image id matching the current view's filter, no paging (T40/F28 — Ctrl+A
    /// "select all in the current view"). Mirrors HandleQuery's filter extraction exactly (minus
    /// page/size/gen) and calls QueryAllIds, so the selection matches what the grid is showing.</summary>
    private string HandleSelectAll(JsonElement root)
    {
        var raw = root.TryGetProperty("raw", out var r) ? r.GetString() ?? "" : "";
        var includeArchived = root.TryGetProperty("includeArchived", out var ia) && ia.ValueKind == JsonValueKind.True;
        var untaggedOnly = root.TryGetProperty("untaggedOnly", out var uo) && uo.ValueKind == JsonValueKind.True;
        var archivedOnly = root.TryGetProperty("archivedOnly", out var ao) && ao.ValueKind == JsonValueKind.True;
        var favoritesOnly = root.TryGetProperty("favoritesOnly", out var fo) && fo.ValueKind == JsonValueKind.True;
        var optimizedOnly = root.TryGetProperty("optimizedOnly", out var oo) && oo.ValueKind == JsonValueKind.True;
        long? collectionId = root.TryGetProperty("collectionId", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt64() : (long?)null;
        var includeSubcollections = root.TryGetProperty("includeSubcollections", out var isc) && isc.ValueKind == JsonValueKind.True;  // T41
        var folderPath = root.TryGetProperty("folderPath", out var fp) && fp.ValueKind == JsonValueKind.String ? fp.GetString() : null;
        var folderRoot = root.TryGetProperty("folderRoot", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;
        var includeSubfolders = root.TryGetProperty("includeSubfolders", out var isf) && isf.ValueKind == JsonValueKind.True;
        var gen = root.TryGetProperty("gen", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : 0;   // echoed so the client drops a stale select-all after a view switch
        var ids = _db.QueryAllIds(SearchParser.Parse(raw), includeArchived, untaggedOnly, archivedOnly, favoritesOnly, collectionId, optimizedOnly, folderPath, folderRoot, includeSubfolders, includeSubcollections);
        return JsonSerializer.Serialize(new { type = "allids", gen, ids }, Json);
    }

    /// <summary>Sidebar counts: All Images (non-archived), Unsorted (untagged), The Bog (archived).</summary>
    private string HandleCounts()
    {
        long all = _db.ImageCount(includeArchived: false);
        long bog = _db.ImageCount(includeArchived: true) - all;
        long unsorted = _db.UntaggedCount();
        long favorites = _db.FavoriteCount();
        long optimized = _db.OptimizedCount();   // T31
        return JsonSerializer.Serialize(new { type = "counts", all, unsorted, bog, favorites, optimized }, Json);
    }

    /// <summary>T30 Library Optimization preview: a cheap (count, skip, est bytes) tally for a scope at a
    /// target size. T39: reads optional "mode" field ("MoveOnly" → all non-optimized regardless of size).
    /// scope = "all" | "selection" (with ids[]) | "folder:&lt;rel-path-prefix&gt;".</summary>
    private string HandleOptimizePreview(JsonElement root)
    {
        var scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() ?? "all" : "all";
        var maxDim = root.TryGetProperty("maxDim", out var md) && md.ValueKind == JsonValueKind.Number ? md.GetInt32() : ImageOptimizer.DefaultMaxDim;
        long[]? ids = root.TryGetProperty("ids", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetInt64()).ToArray() : null;
        var mode = root.TryGetProperty("mode", out var mv) && mv.GetString() == "MoveOnly"
            ? OptimizeMode.MoveOnly : OptimizeMode.Downsample;
        var (count, skip, bytes) = _db.OptimizePreview(scope, maxDim, ids, mode);
        return JsonSerializer.Serialize(new { type = "optpreview", count, skip, bytes }, Json);
    }

    /// <summary>T30: gen-echoed completion reply for the background optimize job (the client drops a
    /// superseded run by gen). recycleFailed = optimized images whose original couldn't be recycled.</summary>
    public static string OptDoneReply(int gen, int optimized, int skipped, int failed, long freedBytes, int recycleFailed) =>
        JsonSerializer.Serialize(new { type = "optdone", gen, optimized, skipped, failed, freedBytes, recycleFailed }, Json);

    private string HandleAutocomplete(JsonElement root)
    {
        var prefix = root.TryGetProperty("prefix", out var p) ? p.GetString() ?? "" : "";
        // acTarget routes the reply client-side (search box vs the TAGS add-input); echoed unchanged.
        var acTarget = root.TryGetProperty("acTarget", out var at) ? at.GetString() ?? "search" : "search";
        var items = _db.TopTags(prefix, 8).Select(x => new { token = x.Token, df = x.Df });
        return JsonSerializer.Serialize(new { type = "ac", acTarget, items }, Json);
    }

    // -- Tags dropdown (T49/F36): full user-tag list for the toolbar dropdown; bulk-apply one tag to many images. --
    private string HandleTagList()
    {
        var tags = _db.GetAllUserTags().Select(x => new { token = x.Token, df = x.Df });
        return JsonSerializer.Serialize(new { type = "taglist", tags }, Json);
    }

    private string? HandleTagBulkAdd(JsonElement root)
    {
        if (!root.TryGetProperty("token", out var tokEl)) return null;
        var token = tokEl.GetString() ?? "";
        if (token.Length == 0) return null;
        long[] ids = root.TryGetProperty("ids", out var idsEl)
            ? idsEl.EnumerateArray().Select(x => x.GetInt64()).ToArray()
            : [];
        var count = _db.AddUserTagBulk(ids, token);
        return JsonSerializer.Serialize(new { type = "tagbulkdone", count, token }, Json);
    }

    // -- Manual tags (T26): write then return the refreshed {type:'tags',id,prompt[],user[]} reply. --
    private string? HandleTagAdd(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idEl)) return null;
        var id = idEl.GetInt64();
        var text = root.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
        _db.AddUserTags(id, text);            // TokenSet-normalized inside (R9 consistency); 0..N tokens
        return BuildTagsReply(id);
    }

    private string? HandleTagDel(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idEl)) return null;
        var id = idEl.GetInt64();
        var token = root.TryGetProperty("token", out var tk) ? tk.GetString() ?? "" : "";
        if (token.Length > 0) _db.RemoveUserTag(id, token);
        return BuildTagsReply(id);
    }

    private string BuildTagsReply(long id) =>
        JsonSerializer.Serialize(new { type = "tags", id, prompt = _db.PromptTagsFor(id), user = _db.UserTagsFor(id) }, Json);

    // -- Collections read (T28/T41): the nested tree for the sidebar; writes are host-side. The action-bar
    // picker (openColPicker) also consumes this — it renders a flat indented list client-side. --
    private string HandleCols()
    {
        var tree = _db.CollectionTree();
        return JsonSerializer.Serialize(new { type = "cols", tree }, Json);
    }

    // -- Folder tree read (T33/F24): the physical-folder navigation tree for the sidebar (distinct
    // from logical Collections). Derived from (source_root, rel_path); nodes carry Root/Path/Name/Count
    // /Children and serialize camelCase (root/path/name/count/children). Rename is host-side. --
    private string HandleFolders()
    {
        var tree = _db.FolderTree();
        return JsonSerializer.Serialize(new { type = "folders", tree }, Json);
    }

    private string? HandleInspect(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idEl)) return null;
        var row = _db.GetById(idEl.GetInt64());
        if (row is null) return null;

        int steps = 0; double? cfg = null; long? seed = null; string? sampler = null, model = null, raw = null;
        if (row.ParamsJson is not null)
        {
            try
            {
                using var d = JsonDocument.Parse(row.ParamsJson);
                var e = d.RootElement;
                if (e.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.Number) steps = st.GetInt32();
                if (e.TryGetProperty("cfg", out var cf) && cf.ValueKind == JsonValueKind.Number) cfg = cf.GetDouble();
                if (e.TryGetProperty("seed", out var se) && se.ValueKind == JsonValueKind.Number) seed = se.GetInt64();
                if (e.TryGetProperty("sampler", out var sa) && sa.ValueKind == JsonValueKind.String) sampler = sa.GetString();
                if (e.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String) model = mo.GetString();
                if (e.TryGetProperty("raw", out var rw) && rw.ValueKind == JsonValueKind.String) raw = rw.GetString();
            }
            catch { /* tolerant */ }
        }

        var target = root.TryGetProperty("target", out var tg) ? tg.GetString() ?? "panel" : "panel";

        // v2.0: the inspect reply is the read path for the NOTES tab (T25) and the TAGS tab (T26).
        var note = _db.GetNote(row.Id);

        return JsonSerializer.Serialize(new
        {
            type = "inspect",
            target,
            id = row.Id,
            url = _urlFor(row),
            fileName = row.FileName,
            format = row.MetaFormat,
            source = row.MetaSource,
            positive = row.Prompt,
            negative = row.Negative,
            model, steps, cfg, seed, sampler, raw,
            loras = ExtractLoras(row.Prompt),
            width = row.Width, height = row.Height,
            sizeBytes = row.SizeBytes,
            folder = row.SourceRoot,
            note = note?.Body,                    // T25 — null when no note (lightbox hides it)
            noteUpdatedAt = note?.UpdatedAt,
            promptTags = _db.PromptTagsFor(row.Id),  // T26 — read-only Charms
            userTags = _db.UserTagsFor(row.Id),      // T26 — removable manual tags
            optimized = row.Optimized,            // T31 — INFO "Optimized" line
            optDim = row.OptDim,
            optAt = row.OptAt
        }, Json);
    }

    /// <summary>Build the gallery 'dupes' reply: each duplicate image as a page-style item plus its
    /// 1-based group index (so the grid can badge/group them). Ordered by group.</summary>
    public static string DupesReply(LibraryDb db, Func<ImageRow, string> urlFor, IReadOnlyList<long[]> groups, int gen)
    {
        var items = new List<object>();
        for (int g = 0; g < groups.Count; g++)
            foreach (var id in groups[g])
            {
                var row = db.GetById(id);
                if (row is null) continue;
                items.Add(new
                {
                    id = row.Id,
                    url = urlFor(row),
                    fileName = row.FileName,
                    format = row.MetaFormat,
                    prompt = row.Prompt,
                    optimized = row.Optimized,   // T31 — ✨O badge shows in the dupes view too
                    group = g + 1
                });
            }
        return JsonSerializer.Serialize(new { type = "dupes", gen, groups = groups.Count, total = items.Count, items }, Json);
    }

    /// <summary>Build the 'autotag' reply (T27): vote-ranked suggestions + the similar neighbors
    /// (as thumbnail items with their Hamming distance). gen-echoed so stale runs are dropped.</summary>
    public static string AutotagReply(LibraryDb db, Func<ImageRow, string> urlFor, long id, AutotagResult res, int gen)
    {
        var neighbors = new List<object>();
        foreach (var n in res.Neighbors)
        {
            var row = db.GetById(n.Id);
            if (row is not null) neighbors.Add(new { id = row.Id, url = urlFor(row), fileName = row.FileName, distance = n.Distance });
        }
        var suggestions = res.Suggestions.Select(s => new { token = s.Token, votes = s.Votes });
        return JsonSerializer.Serialize(new { type = "autotag", gen, id, suggestions, neighbors }, Json);
    }

    // ---- T45/F32 Potions ---- all handlers are synchronous (no background job; stateless writes). ----

    private string HandlePotions()
    {
        var potions = _db.GetPotions();
        var items = potions.Select(p => new
        {
            id = p.Id, name = p.Name, query = p.Query,
            count = _db.CountForFilter(SearchParser.Parse(p.Query))
        });
        return JsonSerializer.Serialize(new { type = "potions", items }, Json);
    }

    private string HandlePotCreate(JsonElement root)
    {
        var name  = root.TryGetProperty("name",  out var n) ? n.GetString() ?? "" : "";
        var query = root.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var newId = _db.CreatePotion(name, query);
        if (newId == -1)
            return JsonSerializer.Serialize(new { type = "poterror", message = $"A Potion named \"{name.Trim()}\" already exists." }, Json);
        return HandlePotions();
    }

    private string HandlePotUpdate(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idEl)) return HandlePotions();
        var name  = root.TryGetProperty("name",  out var n) ? n.GetString() ?? "" : "";
        var query = root.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        if (!_db.UpdatePotion(idEl.GetInt64(), name, query))
            return JsonSerializer.Serialize(new { type = "poterror", message = $"A Potion named \"{name.Trim()}\" already exists." }, Json);
        return HandlePotions();
    }

    private string HandlePotDelete(JsonElement root)
    {
        if (root.TryGetProperty("id", out var idEl)) _db.DeletePotion(idEl.GetInt64());
        return HandlePotions();
    }

    private string HandlePotCount(JsonElement root)
    {
        var query = root.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var count = _db.CountForFilter(SearchParser.Parse(query));
        return JsonSerializer.Serialize(new { type = "potcount", query, count }, Json);
    }

    private static readonly Regex LoraRx =
        new(@"<(?:lora|lyco):([^:>]+?)(?::([0-9.]+))?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Pull &lt;lora:name:weight&gt; / &lt;lyco:…&gt; references out of a prompt (checkpoint = model, shown separately).</summary>
    public static LoraRef[] ExtractLoras(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return Array.Empty<LoraRef>();
        var list = new List<LoraRef>();
        foreach (Match m in LoraRx.Matches(prompt))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length == 0) continue;
            list.Add(new LoraRef(name, m.Groups[2].Success ? m.Groups[2].Value : null));
        }
        return list.ToArray();
    }
}
