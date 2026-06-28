namespace TheTagHag;

/// <summary>
/// One indexed image (camelCase maps to the SQLite `images` table). The internal `Id` is a
/// SQLite autoincrement — NOT a Civitai id. `Tags` are the prompt-derived tokens
/// (PromptSimilarity.TokenSet) written to `image_tags`.
/// </summary>
public sealed class ImageRow
{
    public long Id { get; set; }
    public string SourceRoot { get; set; } = "";
    public string RelPath { get; set; } = "";
    public string AbsPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Ext { get; set; } = "";
    public long SizeBytes { get; set; }
    public long MtimeTicks { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>'a1111' | 'comfyui' | 'sidecar' | 'none'</summary>
    public string MetaFormat { get; set; } = "none";
    /// <summary>'embedded' | 'sidecar-txt' | 'exif' | null</summary>
    public string? MetaSource { get; set; }

    public string Prompt { get; set; } = "";
    public string Negative { get; set; } = "";
    /// <summary>JSON blob: sampler/steps/cfg/seed/model (+ raw Comfy graph).</summary>
    public string? ParamsJson { get; set; }

    /// <summary>Cached thumbnail path; null until generated (T15).</summary>
    public string? ThumbPath { get; set; }
    /// <summary>'present' | 'archived' | 'deleted'</summary>
    public string OriginalState { get; set; } = "present";
    public bool Archived { get; set; }
    public string ScannedAt { get; set; } = "";

    /// <summary>64-bit perceptual hash (dHash) for duplicate detection; null until computed
    /// (lazily backfilled when Find Duplicates runs). Stored signed in SQLite.</summary>
    public long? Phash { get; set; }

    /// <summary>Prompt-derived tokens for this image (drives image_tags + tag_freq).</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Per-image favorite star (v3 images.favorite). Excluded from UpsertCore (pure user
    /// intent) so a rescan keeps it; mapped by name in MapRow.</summary>
    public bool Favorite { get; set; }

    /// <summary>v4 (v2.1): true once the image has been resampled into the managed store
    /// (images.optimized). Like Favorite, it is post-scan state EXCLUDED from UpsertCore, so a rescan
    /// of the store keeps it. Drives the ✨O badge (T31) + the idempotent optimize skip (T30).</summary>
    public bool Optimized { get; set; }
    /// <summary>Longest-edge target (px) the image was optimized to; null if never optimized (opt_dim).</summary>
    public int? OptDim { get; set; }
    /// <summary>ISO timestamp of optimization; null if never optimized (opt_at).</summary>
    public string? OptAt { get; set; }
}

/// <summary>One node in the derived folder tree (T33/F24). The hierarchy is DERIVED from
/// (source_root, rel_path) — there is no folders table. <see cref="Path"/> is the directory relative
/// to <see cref="Root"/> ("" = the root itself); <see cref="Count"/> is the recursive image total
/// (this folder + all descendants). Clicking a node sends Root+Path to the folderPath query filter.</summary>
public sealed class FolderNode
{
    /// <summary>The owning source_root (absolute). Disambiguates identical rel-dirs across roots
    /// (the managed store is always a second scanned root).</summary>
    public string Root { get; set; } = "";
    /// <summary>Directory relative to Root, OS-separated ("" = files directly under Root).</summary>
    public string Path { get; set; } = "";
    /// <summary>Display leaf (last path segment; the root node shows the root's folder name).</summary>
    public string Name { get; set; } = "";
    /// <summary>Recursive image count: this folder plus everything beneath it (non-archived).</summary>
    public int Count { get; set; }
    public List<FolderNode> Children { get; set; } = new();
}

/// <summary>One node in the nested collections tree (T41/F29). <see cref="Count"/> is the direct
/// membership count; <see cref="CountRecursive"/> is filled by <see cref="LibraryDb.CollectionTree"/>
/// and includes all descendants (mirrors FolderNode's recursive roll-up). Children are sorted by name.</summary>
public sealed class CollectionNode
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long? ParentId { get; set; }
    public int Count { get; set; }
    public int CountRecursive { get; set; }
    public List<CollectionNode> Children { get; set; } = new();
}

/// <summary>T44/F31 — one image whose collection memberships are tied (two or more collections share the
/// deepest depth). Passed to <see cref="ReviewTiesForm"/> so the user can pick the correct home.</summary>
public sealed class TieCandidate
{
    public long ImageId { get; set; }
    public string FileName { get; set; } = "";
    /// <summary>The tied collections: id + name + depth (all share the max depth for this image).</summary>
    public List<(long CollId, string Name, int Depth)> Tied { get; set; } = new();
}

/// <summary>T45/F32 — one saved search ("Potion"). Name is case-insensitive unique in the
/// potions table. Query is the raw search string (same syntax as the main search bar).
/// SortOrder drives the sidebar display order.</summary>
public sealed class PotionRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>One auto-tag suggestion: a candidate token + how many similar images carry it (T27).</summary>
public sealed record AutotagSuggestion(string Token, int Votes);

/// <summary>Result of a suggest-only KNN auto-tag run (T27): vote-ranked candidate tokens + the
/// visually-similar neighbors they came from. Producing this writes nothing (suggest-only).</summary>
public sealed class AutotagResult
{
    public List<(long Id, int Distance)> Neighbors { get; set; } = new();
    public List<AutotagSuggestion> Suggestions { get; set; } = new();
}

/// <summary>Progress tick for long operations (scan / file ops), surfaced to the status bar.
/// Mirrors CivitaiHarvesterApp's HarvestProgress so inherited code stays compatible.</summary>
public readonly record struct HarvestProgress(string Phase, int Current, int Total);

/// <summary>
/// Result of reading one image's generation metadata. Shared by the A1111, EXIF, sidecar,
/// and ComfyUI paths (T3–T5). `Format`/`Source` record how it was obtained.
/// </summary>
public sealed class ParsedMeta
{
    /// <summary>'a1111' | 'comfyui' | 'sidecar' | 'none'</summary>
    public string Format { get; set; } = "none";
    /// <summary>'embedded' | 'sidecar-txt' | 'exif' | null</summary>
    public string? Source { get; set; }
    public string Prompt { get; set; } = "";
    public string Negative { get; set; } = "";
    public string? Sampler { get; set; }
    public int Steps { get; set; }
    public double? Cfg { get; set; }
    public long? Seed { get; set; }
    public string? Model { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Raw graph JSON for ComfyUI (raw-metadata lightbox view); null otherwise.</summary>
    public string? RawJson { get; set; }
}

// ===================== Civitai harvest mode (T17) =====================

/// <summary>One harvested image — serialized to dataset.jsonl (camelCase). Ported from
/// CivitaiHarvesterApp; same on-disk shape so existing datasets stay compatible.</summary>
public sealed class ImageRecord
{
    public int id { get; set; }
    public int likes { get; set; }
    public int hearts { get; set; }
    public int reactionCount { get; set; }
    public string? nsfwLevel { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public string? baseModel { get; set; }
    public string prompt { get; set; } = "";
    public string negativePrompt { get; set; } = "";
    public string? sampler { get; set; }
    public int steps { get; set; }
    public double? cfgScale { get; set; }
    public long? seed { get; set; }
    public string? model { get; set; }
    public List<ResourceRef> resources { get; set; } = new();
    public List<string> tags { get; set; } = new();
    public string? collection { get; set; }
    public string civitaiUrl { get; set; } = "";
    public string imageFile { get; set; } = "";
    public string harvestedAt { get; set; } = "";
}

public sealed class ResourceRef
{
    public string? name { get; set; }
    public string? type { get; set; }
    public string? version { get; set; }
    public string? baseModel { get; set; }
    public double? weight { get; set; }
}

/// <summary>One harvest log line — streamed to the Civitai dialog's log pane and to taghag.log.</summary>
public readonly record struct HarvestEvent(string Level, string Message)
{
    public override string ToString() => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}";
}

/// <summary>Tunable harvest parameters (persisted in settings.json under Harvest).</summary>
public sealed class HarvestOptions
{
    public int MaxNew { get; set; } = 50;
    public int LikesMin { get; set; } = 150;
    public string Sort { get; set; } = "Most Reactions";
    public string Period { get; set; } = "AllTime";
    public int TagId { get; set; } = 0;
    public string Nsfw { get; set; } = "X";
    public int PageSize { get; set; } = 100;
    public int MaxPages { get; set; } = 80;
    public int ThrottleMs { get; set; } = 400;
    public int ImageWidth { get; set; } = 1024;

    public string CollectionNames { get; set; } = "";
    public int CollectionEarlyExitSeen { get; set; } = 20;
    public int MaxPerCollection { get; set; } = 200;
    public int CollectionPageSize { get; set; } = 75;

    public bool Sidecars { get; set; } = false;
    public double SimilarityThreshold { get; set; } = 0.75;

    [System.Text.Json.Serialization.JsonIgnore] public bool DryRun { get; set; } = false;
    [System.Text.Json.Serialization.JsonIgnore] public bool SkipFeed { get; set; } = false;
    [System.Text.Json.Serialization.JsonIgnore] public bool Collections { get; set; } = false;
}
