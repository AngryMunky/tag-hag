using System.Collections.Concurrent;
using System.Text.Json;

namespace TheTagHag;

/// <summary>Scan outcome. <c>ReLinked</c> = moved/renamed files matched back to their original row by
/// content (id + user-state preserved, T32/F22); <c>Unmatched</c> = phashed files that vanished and had
/// candidate look-alikes but couldn't be safely auto-linked (ambiguous → left as new, R14).</summary>
public readonly record struct ScanResult(int Added, int Updated, int Unchanged, int Removed, int Failed, int ReLinked = 0, int Unmatched = 0);

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

        // 1) Enumerate + incremental skip. (F26/T36: "Scanning folders…" is indeterminate — the total is
        // unknown until enumeration finishes, so report Total=0 with a running discovered count.)
        progress?.Report(new HarvestProgress("Scanning folders", 0, 0));
        foreach (var root in roots)
        {
            foreach (var path in SafeEnumerate(root))
            {
                ct.ThrowIfCancellationRequested();
                if (!Exts.Contains(Path.GetExtension(path))) continue;
                var abs = Path.GetFullPath(path);
                seen.Add(abs);
                if (seen.Count % 200 == 0) progress?.Report(new HarvestProgress("Scanning folders", seen.Count, 0));
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

        // 2) Parallel metadata reads → ImageRows. (F26/T36: determinate — report per-file via the
        // Interlocked+modulo idiom so the overlay bar advances; throttled to every 100 + the final tick.)
        progress?.Report(new HarvestProgress("Reading metadata", 0, toParse.Count));
        var parsed = new ConcurrentBag<(ImageRow Row, bool Existed)>();
        var failedReads = 0;
        var readDone = 0;
        var opts = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.ForEach(toParse, opts, item =>
        {
            try { parsed.Add((BuildRow(item.Root, item.Path, item.Fi), item.Existed)); }
            catch { Interlocked.Increment(ref failedReads); }
            var n = Interlocked.Increment(ref readDone);
            if (n % 100 == 0 || n == toParse.Count) progress?.Report(new HarvestProgress("Reading metadata", n, toParse.Count));
        });

        // 3) Single-writer batched upsert.
        progress?.Report(new HarvestProgress("Indexing", 0, 0));   // F26/T36: finalize phase (indeterminate)
        var rows = parsed.Select(x => x.Row).ToList();
        _db.UpsertBatch(rows);
        int added = parsed.Count(x => !x.Existed);
        int updated = parsed.Count(x => x.Existed);

        // 3b) Re-link moved/renamed files BEFORE prune (T32/F22): a file that moved shows up as a NEW
        // orphan row at its new path while its old row's file "disappeared". Match each disappeared
        // *phashed* row to a freshly-inserted orphan by UNIQUE exact phash + equal size, then fold the
        // orphan back into the original row (delete orphan, repath original) so the id — and every
        // favorite/note/tag/collection/archived bit hanging off it — survives the move. Unique-only:
        // any ambiguity is left as new (no wrong-merge, R14). Running before prune guarantees a move is
        // never read as delete+add. The pass is skipped entirely when nothing phashed disappeared, so a
        // plain add-only scan pays nothing.
        int reLinked = 0, unmatched = 0;
        var disappeared = _db.DisappearedCandidates(roots, seen);
        if (disappeared.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new HarvestProgress("Re-linking moved files", 0, 0));   // F26/T36 (indeterminate)
            var orphans = parsed.Where(x => !x.Existed).Select(x => x.Row).ToList(); // new rows (ids set by upsert)
            var orphanHash = new ConcurrentDictionary<long, long>();
            Parallel.ForEach(orphans, opts, o => { var h = PerceptualHash.Compute(o.AbsPath); if (h is long hv) orphanHash[o.Id] = hv; });

            var disByHash = disappeared.GroupBy(d => d.Phash).ToDictionary(g => g.Key, g => g.ToList());
            var orphByHash = orphans.Where(o => orphanHash.ContainsKey(o.Id))
                                    .GroupBy(o => orphanHash[o.Id]).ToDictionary(g => g.Key, g => g.ToList());
            var relinks = new List<(long OldId, string NewAbs, string NewRoot, long OrphanId)>();
            foreach (var (h, dis) in disByHash)
            {
                if (!orphByHash.TryGetValue(h, out var orph)) continue; // a phashed file just deleted → normal prune, not a re-link miss
                if (dis.Count == 1 && orph.Count == 1 && orph[0].SizeBytes == dis[0].Size)
                    relinks.Add((dis[0].Id, orph[0].AbsPath, orph[0].SourceRoot, orph[0].Id));
                else
                    unmatched += dis.Count;                             // ambiguous (≥2 either side) or size mismatch → leave as new
            }
            reLinked = _db.ApplyRelinks(relinks, orphanHash);
            added -= reLinked;                                          // a re-linked orphan is a move, not an addition
        }

        // 4) Prune rows whose file is gone (re-linked rows now point at a seen, on-disk path → kept).
        int removed = _db.PruneMissing(roots, seen);

        return new ScanResult(added, updated, unchanged, removed, failed + failedReads, reLinked, unmatched);
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
