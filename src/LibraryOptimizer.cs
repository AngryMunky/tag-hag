using System.Security.Cryptography;
using System.Text;

namespace TheTagHag;

/// <summary>Tally from one Library Optimization run (T30 / F20). <c>RecycleFailed</c> counts images that
/// were optimized + moved into the store but whose ORIGINAL could not be recycled (it lingers on disk;
/// its bytes are NOT counted as freed).</summary>
public readonly record struct OptimizeResult(int Optimized, int Skipped, int Failed, long FreedBytes, int RecycleFailed);

/// <summary>
/// T30 — Library Optimization (F20). Resamples each image (same format, metadata-preserving) into
/// the Tag Hag-managed store at <c>&lt;store&gt;/&lt;root-slug&gt;/&lt;rel_path&gt;</c>, moves the DB row to the
/// new in-store location via <see cref="LibraryDb.MarkOptimized"/> (keeping its id + all v3
/// user-state), and recycles the original ONLY after the store copy is verified written (R15). The
/// row id never changes, so the next scan of the store reads the file as an UPDATE, not a new row
/// (R16). Idempotent: already-optimized rows are filtered out by the caller, and an image already
/// within the size target is skipped (never moved). Pure logic over a LibraryDb + FileOps +
/// ImageOptimizer, so it is exercised headlessly by <c>--selftest-optimizelib</c>.
/// </summary>
public static class LibraryOptimizer
{
    /// <summary>
    /// Run the resample→store→recycle pipeline over <paramref name="ids"/>. Each item: skip if gone /
    /// archived / already optimized / already ≤ <paramref name="maxDim"/>; else resample into the store,
    /// verify, <see cref="LibraryDb.MarkOptimized"/>, recycle the original, regen the thumbnail.
    /// Cancellation is checked per item (a cancelled token throws <see cref="OperationCanceledException"/>).
    /// </summary>
    public static OptimizeResult Run(
        LibraryDb db, IReadOnlyList<long> ids, int maxDim,
        ThumbnailService? thumbs = null, IProgress<HarvestProgress>? progress = null, CancellationToken ct = default)
    {
        var store = AppPaths.EnsureLibraryStore();
        int optimized = 0, skipped = 0, failed = 0, recycleFailed = 0;
        long freed = 0;
        int done = 0, total = ids.Count;
        var at = DateTime.Now.ToString("s");

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new HarvestProgress("Optimizing", done, total));
            done++;
            try
            {
                var row = db.GetById(id);
                if (row is null || row.Optimized || row.Archived || !File.Exists(row.AbsPath)) { skipped++; continue; }

                var (w, h) = ImageOptimizer.ReadDimensions(row.AbsPath);
                if (w <= 0) { failed++; continue; }                          // unreadable
                if (w <= maxDim && h <= maxDim) { skipped++; continue; }      // already small → leave in place (AC4)

                var relDir = Path.GetDirectoryName(row.RelPath) ?? "";
                var destDir = Path.Combine(store, RootSlug(row.SourceRoot), relDir);
                Directory.CreateDirectory(destDir);
                // UniqueDestination is defensive — store paths are namespaced by slug+rel_path, but a
                // prior crashed attempt could have left a file. Never clobber.
                var dest = FileOps.UniqueDestination(destDir, row.FileName);

                var outcome = ImageOptimizer.DownsampleToCopy(row.AbsPath, dest, maxDim);
                if (outcome != OptimizeOutcome.Resized) { failed++; TryDelete(dest); continue; }

                // R15: verify the store copy exists and is non-empty (one stat) BEFORE any DB change.
                var destFi = new FileInfo(dest);
                if (!destFi.Exists || destFi.Length == 0) { failed++; TryDelete(dest); continue; }
                long newSize = destFi.Length;

                // Move the row into the store (same id → favorites/notes/user_tags/collections follow).
                // If MarkOptimized throws, delete the orphan store copy so a later scan can't index it
                // as a phantom row, and count the item as failed (the original is left untouched).
                try { db.MarkOptimized(id, dest, maxDim, at); }
                catch { TryDelete(dest); failed++; continue; }

                // Only now is it safe to recycle the original (the row already points at the verified
                // store copy). A failed recycle is non-fatal but DOES mean the original still occupies
                // disk — track it and DON'T credit its bytes as freed (the store copy also added space).
                bool recycled = true;
                try { FileOps.RecycleDelete(row.AbsPath); }
                catch { recycled = false; recycleFailed++; }
                if (recycled) freed += Math.Max(0, row.SizeBytes - newSize);

                // Regen the thumbnail for the new path+mtime (lazy anyway; this pre-warms it).
                try { thumbs?.GetOrCreate(id); } catch { }

                optimized++;
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }
        }

        progress?.Report(new HarvestProgress("Optimizing", total, total));
        return new OptimizeResult(optimized, skipped, failed, freed, recycleFailed);
    }

    /// <summary>
    /// A filesystem-safe, deterministic, collision-resistant directory name for a source root, used to
    /// namespace its files in the store (<c>&lt;store&gt;/&lt;slug&gt;/&lt;rel_path&gt;</c>, O-v21-A) so two roots with
    /// the same rel_path never collide. Form: a sanitized leaf name + an 8-hex SHA-1 of the normalized
    /// (case-folded) full path. Case-insensitive because Windows paths are.
    /// </summary>
    public static string RootSlug(string sourceRoot)
    {
        var norm = (sourceRoot ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var leaf = Path.GetFileName(norm);
        if (string.IsNullOrEmpty(leaf)) leaf = "root";
        var sb = new StringBuilder(leaf.Length);
        foreach (var c in leaf) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var safe = sb.ToString();
        if (safe.Length > 32) safe = safe[..32];
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(norm)))[..8].ToLowerInvariant();
        return $"{safe}-{hash}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
