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
