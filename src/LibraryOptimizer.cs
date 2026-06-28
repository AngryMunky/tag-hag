using System.Security.Cryptography;
using System.Text;

namespace TheTagHag;

/// <summary>T39/F27c — per-run consolidation mode.
/// Downsample resamples to maxDim and recycles the original (existing behavior).
/// MoveOnly relocates the full-resolution file into the store, no resample, no recycle.</summary>
public enum OptimizeMode { Downsample, MoveOnly }

/// <summary>T44/F31 — how the managed-store layout is organised.
/// SourceFolders mirrors the source directory tree (existing behavior).
/// Collections places each image under its deepest collection path in the store.</summary>
public enum OrganizeBy { SourceFolders, Collections }

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
    /// Run the resample→store→recycle pipeline (Downsample mode) or move→store pipeline (MoveOnly mode)
    /// over <paramref name="ids"/>. Each item: skip if gone / archived / already optimized (SourceFolders
    /// mode) / already ≤ <paramref name="maxDim"/> (Downsample only — MoveOnly moves regardless of size);
    /// else resample/move into the store, verify, <see cref="LibraryDb.MarkOptimized"/>, and (Downsample
    /// only) recycle the original. T44/F31: when <paramref name="organizeBy"/> is Collections, dest is
    /// the image's deepest collection path; already-optimized images that are at the wrong path are
    /// relocated via <see cref="LibraryDb.RepathRow"/> (opt_* preserved — absorbs T38). Cancellation is
    /// checked per item.
    /// </summary>
    public static OptimizeResult Run(
        LibraryDb db, IReadOnlyList<long> ids, int maxDim,
        OptimizeMode mode = OptimizeMode.Downsample,
        OrganizeBy organizeBy = OrganizeBy.SourceFolders,
        IReadOnlyDictionary<long, long>? tieOverrides = null,
        bool skipUncollected = false,
        IReadOnlyList<CollectionNode>? collectionTree = null,
        ThumbnailService? thumbs = null, IProgress<HarvestProgress>? progress = null, CancellationToken ct = default)
    {
        var store = AppPaths.EnsureLibraryStore();
        int optimized = 0, skipped = 0, failed = 0, recycleFailed = 0;
        long freed = 0;
        int done = 0, total = ids.Count;
        var at = DateTime.Now.ToString("s");

        // Pre-compute collection path data once for the whole run (Collections mode only).
        Dictionary<long, int>? depthMap = null;
        Dictionary<long, string[]>? collPaths = null;
        Dictionary<long, List<long>>? memberMap = null;
        if (organizeBy == OrganizeBy.Collections && ids.Count > 0)
        {
            var tree = collectionTree ?? db.CollectionTree();
            depthMap = BuildDepthMap(tree);
            collPaths = BuildCollectionPaths(tree);
            memberMap = db.GetCollectionMemberships(ids);
        }

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new HarvestProgress("Consolidating", done, total));
            done++;
            try
            {
                var row = db.GetById(id);
                if (row is null || row.Archived || !File.Exists(row.AbsPath)) { skipped++; continue; }

                // SourceFolders mode: skip already-optimized (existing T30 behavior).
                if (organizeBy == OrganizeBy.SourceFolders && row.Optimized) { skipped++; continue; }

                if (organizeBy == OrganizeBy.Collections)
                {
                    // --- T44 Collections organise-by path ---
                    var memberOf = memberMap != null && memberMap.TryGetValue(id, out var m) ? m : (IReadOnlyList<long>)Array.Empty<long>();
                    string destDir;
                    if (memberOf.Count == 0)
                    {
                        if (skipUncollected) { skipped++; continue; }
                        var relD = Path.GetDirectoryName(row.RelPath) ?? "";
                        destDir = Path.Combine(store, "_Uncollected", RootSlug(row.SourceRoot), relD);
                    }
                    else
                    {
                        var homeId = FindHome(memberOf, depthMap!, tieOverrides, id, out _);
                        destDir = collPaths!.TryGetValue(homeId, out var segs)
                            ? BuildPath(store, segs)
                            : Path.Combine(store, "_Uncollected", RootSlug(row.SourceRoot));
                    }
                    Directory.CreateDirectory(destDir);
                    var dest = FileOps.UniqueDestination(destDir, row.FileName);

                    if (row.Optimized)
                    {
                        // Already in the managed store — relocate to the correct collection path if needed.
                        // Use the ideal (non-unique) path for the idempotency check: UniqueDestination
                        // returns "img (2).png" when "img.png" already exists there, so comparing against
                        // `dest` would move an already-correct file to a suffixed name unnecessarily.
                        var idealDest = Path.Combine(destDir, row.FileName);
                        if (string.Equals(Path.GetFullPath(row.AbsPath), Path.GetFullPath(idealDest), StringComparison.OrdinalIgnoreCase))
                        { skipped++; continue; }
                        try { FileOps.Move(row.AbsPath, dest); }
                        catch { failed++; continue; }
                        var dfiR = new FileInfo(dest);
                        if (!dfiR.Exists || dfiR.Length == 0) { failed++; TryRestoreMove(dest, row.AbsPath); continue; }
                        try { db.RepathRow(id, dest, store); }
                        catch { TryRestoreMove(dest, row.AbsPath); failed++; continue; }
                        try { thumbs?.GetOrCreate(id); } catch { }
                        optimized++; continue;
                    }

                    // Not yet in managed store — first-consolidate using the chosen mode.
                    var (wC, hC) = ImageOptimizer.ReadDimensions(row.AbsPath);
                    if (wC <= 0) { failed++; continue; }

                    bool didDownsample = mode == OptimizeMode.Downsample && (wC > maxDim || hC > maxDim);
                    int optDimC;
                    if (didDownsample)
                    {
                        var outcome = ImageOptimizer.DownsampleToCopy(row.AbsPath, dest, maxDim);
                        if (outcome != OptimizeOutcome.Resized) { failed++; TryDelete(dest); continue; }
                        optDimC = maxDim;
                    }
                    else
                    {
                        // MoveOnly mode, or Downsample mode with a small image — just relocate full-res.
                        try { FileOps.Move(row.AbsPath, dest); }
                        catch { failed++; continue; }
                        optDimC = Math.Max(wC, hC);
                    }
                    var dfiC = new FileInfo(dest);
                    if (!dfiC.Exists || dfiC.Length == 0) { failed++; TryDelete(dest); continue; }
                    long newSizeC = dfiC.Length;
                    try { db.MarkOptimized(id, dest, optDimC, at); }
                    catch
                    {
                        if (!didDownsample) { try { FileOps.Move(dest, row.AbsPath); } catch { } }
                        else { TryDelete(dest); }
                        failed++; continue;
                    }
                    if (didDownsample)
                    {
                        bool recycled = true;
                        try { FileOps.RecycleDelete(row.AbsPath); }
                        catch { recycled = false; recycleFailed++; }
                        if (recycled) freed += Math.Max(0, row.SizeBytes - newSizeC);
                    }
                    try { thumbs?.GetOrCreate(id); } catch { }
                    optimized++; continue;
                }

                // --- Existing SourceFolders path (T30/T39) ---
                var (w, h) = ImageOptimizer.ReadDimensions(row.AbsPath);
                if (w <= 0) { failed++; continue; }                                  // unreadable

                // Downsample: skip images already within size target; MoveOnly: move regardless of size.
                if (mode == OptimizeMode.Downsample && w <= maxDim && h <= maxDim) { skipped++; continue; }

                var relDir = Path.GetDirectoryName(row.RelPath) ?? "";
                var destDir2 = Path.Combine(store, RootSlug(row.SourceRoot), relDir);
                Directory.CreateDirectory(destDir2);
                var dest2 = FileOps.UniqueDestination(destDir2, row.FileName);

                if (mode == OptimizeMode.Downsample)
                {
                    var outcome = ImageOptimizer.DownsampleToCopy(row.AbsPath, dest2, maxDim);
                    if (outcome != OptimizeOutcome.Resized) { failed++; TryDelete(dest2); continue; }
                }
                else // MoveOnly: relocate full-res, no resample, format/metadata untouched (R13)
                {
                    try { FileOps.Move(row.AbsPath, dest2); }
                    catch { failed++; continue; }
                }

                // R15: verify the store file exists and is non-empty BEFORE any DB change.
                var destFi = new FileInfo(dest2);
                if (!destFi.Exists || destFi.Length == 0) { failed++; TryDelete(dest2); continue; }
                long newSize = destFi.Length;

                // Move the row into the store (same id → favorites/notes/user_tags/collections follow).
                // MoveOnly: opt_dim = actual longest edge (no downscale). On failure, attempt rollback.
                int optDim = mode == OptimizeMode.MoveOnly ? Math.Max(w, h) : maxDim;
                try { db.MarkOptimized(id, dest2, optDim, at); }
                catch
                {
                    if (mode == OptimizeMode.MoveOnly) { try { FileOps.Move(dest2, row.AbsPath); } catch { } }
                    else { TryDelete(dest2); }
                    failed++; continue;
                }

                // Downsample: recycle the original (now safe — row already points at the store copy).
                // MoveOnly: the FileOps.Move already consumed the original; nothing to recycle.
                if (mode == OptimizeMode.Downsample)
                {
                    bool recycled = true;
                    try { FileOps.RecycleDelete(row.AbsPath); }
                    catch { recycled = false; recycleFailed++; }
                    if (recycled) freed += Math.Max(0, row.SizeBytes - newSize);
                }

                // Regen the thumbnail for the new path+mtime (lazy anyway; this pre-warms it).
                try { thumbs?.GetOrCreate(id); } catch { }

                optimized++;
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }
        }

        progress?.Report(new HarvestProgress("Consolidating", total, total));
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

    // ── T44 / F31 static helpers ──────────────────────────────────────────────

    /// <summary>Strips illegal filesystem characters from a collection name for use as a folder segment.
    /// Keeps letters, digits, <c>-</c>, <c>_</c>, and <c>.</c>; replaces everything else with <c>_</c>.
    /// Trims leading/trailing <c>_</c> and collapses runs so names stay legible.</summary>
    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "_unnamed";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var s = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(s) ? "_unnamed" : s;
    }

    /// <summary>Maps collection_id → depth (root=0) for the whole tree.</summary>
    public static Dictionary<long, int> BuildDepthMap(IReadOnlyList<CollectionNode> tree)
    {
        var map = new Dictionary<long, int>();
        void Walk(CollectionNode node, int depth) { map[node.Id] = depth; foreach (var c in node.Children) Walk(c, depth + 1); }
        foreach (var root in tree) Walk(root, 0);
        return map;
    }

    /// <summary>Maps collection_id → <see cref="CollectionNode"/> for O(1) lookup.</summary>
    public static Dictionary<long, CollectionNode> BuildNodeMap(IReadOnlyList<CollectionNode> tree)
    {
        var map = new Dictionary<long, CollectionNode>();
        void Walk(CollectionNode node) { map[node.Id] = node; foreach (var c in node.Children) Walk(c); }
        foreach (var root in tree) Walk(root);
        return map;
    }

    /// <summary>
    /// Maps collection_id → ordered array of sanitized folder name segments from root → leaf.
    /// Sibling collisions (two collections with the same sanitized name under the same parent) are
    /// disambiguated by appending <c>_id</c> so the store paths remain unique.
    /// </summary>
    public static Dictionary<long, string[]> BuildCollectionPaths(IReadOnlyList<CollectionNode> tree)
    {
        var result = new Dictionary<long, string[]>();
        void Walk(CollectionNode node, string[] parentSegs)
        {
            // Build a sibling-collision map: sanitized name → list of sibling ids.
            var siblingCounts = new Dictionary<string, List<long>>();
            foreach (var s in node.Children)
            {
                var seg = SanitizeFolderName(s.Name);
                if (!siblingCounts.TryGetValue(seg, out var lst)) siblingCounts[seg] = lst = new();
                lst.Add(s.Id);
            }
            foreach (var child in node.Children)
            {
                var seg = SanitizeFolderName(child.Name);
                if (siblingCounts[seg].Count > 1) seg = $"{seg}_{child.Id}";
                var segs = parentSegs.Append(seg).ToArray();
                result[child.Id] = segs;
                Walk(child, segs);
            }
        }
        // Handle root-level collisions across tree roots.
        var rootCounts = new Dictionary<string, List<long>>();
        foreach (var root in tree)
        {
            var seg = SanitizeFolderName(root.Name);
            if (!rootCounts.TryGetValue(seg, out var lst)) rootCounts[seg] = lst = new();
            lst.Add(root.Id);
        }
        foreach (var root in tree)
        {
            var seg = SanitizeFolderName(root.Name);
            if (rootCounts[seg].Count > 1) seg = $"{seg}_{root.Id}";
            result[root.Id] = new[] { seg };
            Walk(root, new[] { seg });
        }
        return result;
    }

    /// <summary>
    /// Picks the deepest collection for an image (tie-break: lowest id). If the image has a
    /// user-supplied override in <paramref name="tieOverrides"/>, that wins unconditionally.
    /// Sets <paramref name="isTied"/> when multiple collections share the maximum depth (after
    /// overrides are removed from consideration) — caller uses this to populate [Review ties…].
    /// </summary>
    public static long FindHome(
        IReadOnlyList<long> memberOf,
        Dictionary<long, int> depthMap,
        IReadOnlyDictionary<long, long>? tieOverrides,
        long imageId,
        out bool isTied)
    {
        if (tieOverrides != null && tieOverrides.TryGetValue(imageId, out var forced))
        { isTied = false; return forced; }

        int maxDepth = -1;
        long bestId = memberOf[0];
        int tiedCount = 0;
        foreach (var cid in memberOf)
        {
            int d = depthMap.TryGetValue(cid, out var dv) ? dv : 0;
            if (d > maxDepth)
            { maxDepth = d; bestId = cid; tiedCount = 1; }
            else if (d == maxDepth && cid < bestId)
            { bestId = cid; tiedCount++; }          // keep the count — old bestId is still a tie candidate
            else if (d == maxDepth)
            { tiedCount++; }
        }
        isTied = tiedCount > 1;
        return bestId;
    }

    /// <summary>Joins a store root with an array of sanitized path segments.</summary>
    public static string BuildPath(string storeRoot, string[] segments) =>
        segments.Length == 0 ? storeRoot : Path.Combine(storeRoot, Path.Combine(segments));

    // ─────────────────────────────────────────────────────────────────────────

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryRestoreMove(string wrongDest, string originalPath)
    {
        try { if (File.Exists(wrongDest)) FileOps.Move(wrongDest, originalPath); } catch { }
    }
}
