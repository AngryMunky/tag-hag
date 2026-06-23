using System.Collections.Concurrent;
using System.Text.Json;

namespace TheTagHag;

public readonly record struct ScanResult(int Added, int Updated, int Unchanged, int Removed, int Failed);

/// <summary>
/// Scans source roots recursively, reads metadata, and keeps library.db in sync:
///   - incremental skip: a file whose stored (mtime, size) match on disk is not re-parsed;
///   - parallel metadata READS, then a single batched WRITE (one transaction) — the DB stays
///     single-writer (architecture concurrency model);
///   - removed-file pruning: rows under a scanned root not seen this pass whose file is gone;
///   - per-file try/catch so one bad file never aborts the scan (R5).
/// Tags come from PromptSimilarity.TokenSet(prompt) — the same function the search uses (T7).
/// </summary>
public sealed class LocalScanner
{
    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
    private readonly LibraryDb _db;

    public LocalScanner(LibraryDb db) => _db = db;

    public ScanResult Scan(IReadOnlyList<string> roots, CancellationToken ct = default, IProgress<HarvestProgress>? progress = null)
    {
        int unchanged = 0, failed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toParse = new List<(string Root, string Path, FileInfo Fi, bool Existed)>();

        // 1) Enumerate + incremental skip.
        foreach (var root in roots)
        {
            foreach (var path in SafeEnumerate(root))
            {
                ct.ThrowIfCancellationRequested();
                if (!Exts.Contains(Path.GetExtension(path))) continue;
                var abs = Path.GetFullPath(path);
                seen.Add(abs);
                try
                {
                    var fi = new FileInfo(path);
                    var sig = _db.GetFileSig(abs);
                    if (sig is { } s && s.Mtime == fi.LastWriteTimeUtc.Ticks && s.Size == fi.Length) { unchanged++; continue; }
                    toParse.Add((root, abs, fi, sig is not null));
                }
                catch { failed++; }
            }
        }

        progress?.Report(new HarvestProgress("Reading metadata", 0, toParse.Count));

        // 2) Parallel metadata reads → ImageRows.
        var parsed = new ConcurrentBag<(ImageRow Row, bool Existed)>();
        var failedReads = 0;
        var opts = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.ForEach(toParse, opts, item =>
        {
            try { parsed.Add((BuildRow(item.Root, item.Path, item.Fi), item.Existed)); }
            catch { Interlocked.Increment(ref failedReads); }
        });

        // 3) Single-writer batched upsert.
        var rows = parsed.Select(x => x.Row).ToList();
        _db.UpsertBatch(rows);
        int added = parsed.Count(x => !x.Existed);
        int updated = parsed.Count(x => x.Existed);

        // 4) Prune rows whose file is gone.
        int removed = _db.PruneMissing(roots, seen);

        return new ScanResult(added, updated, unchanged, removed, failed + failedReads);
    }

    private static ImageRow BuildRow(string root, string abs, FileInfo fi)
    {
        var meta = ImageMetadataReader.Read(abs);
        return new ImageRow
        {
            SourceRoot = root,
            AbsPath = abs,
            RelPath = Path.GetRelativePath(root, abs),
            FileName = Path.GetFileName(abs),
            Ext = Path.GetExtension(abs).ToLowerInvariant(),
            SizeBytes = fi.Length,
            MtimeTicks = fi.LastWriteTimeUtc.Ticks,
            Width = meta.Width > 0 ? meta.Width : null,
            Height = meta.Height > 0 ? meta.Height : null,
            MetaFormat = meta.Format,
            MetaSource = meta.Source,
            Prompt = meta.Prompt,
            Negative = meta.Negative,
            ParamsJson = BuildParamsJson(meta),
            ScannedAt = DateTime.UtcNow.ToString("o"),
            Tags = PromptSimilarity.TokenSet(meta.Prompt).ToList()
        };
    }

    private static string BuildParamsJson(ParsedMeta m)
    {
        var obj = new Dictionary<string, object?>
        {
            ["steps"] = m.Steps,
            ["cfg"] = m.Cfg,
            ["seed"] = m.Seed,
            ["sampler"] = m.Sampler,
            ["model"] = m.Model,
        };
        if (m.Format == "comfyui" && m.RawJson is not null) obj["raw"] = m.RawJson;
        return JsonSerializer.Serialize(obj);
    }

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        // EnumerateFiles can throw mid-iteration on access errors; guard the whole walk.
        IEnumerator<string> e;
        try { e = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).GetEnumerator(); }
        catch { yield break; }
        while (true)
        {
            string current;
            try { if (!e.MoveNext()) break; current = e.Current; }
            catch { break; }
            yield return current;
        }
    }
}
