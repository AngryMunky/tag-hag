using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace TheTagHag;

/// <summary>
/// Owns library.db (the live local store). Designed for a SINGLE writer thread; callers
/// batch writes in transactions (the scanner in T6 does this). Schema + triggers keep
/// image_tags, tag_freq and the images_fts index in sync automatically:
///   - tag_freq is maintained by AFTER INSERT/DELETE triggers on image_tags.
///   - images_fts (FTS5 external-content over images) is maintained by AFTER
///     INSERT/UPDATE/DELETE triggers on images (the canonical external-content pattern).
/// So Upsert/Remove only touch `images` + `image_tags`; the derived tables follow.
/// </summary>
public sealed class LibraryDb : IDisposable
{
    /// <summary>Bump when the schema changes; Migrate() upgrades older databases.
    /// v2 adds images.phash (perceptual hash) for Find Duplicates.</summary>
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _con;

    public LibraryDb(string path)
    {
        _con = new SqliteConnection($"Data Source={path}");
        _con.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA foreign_keys=ON;");
        EnsureSchema();
        Migrate();
    }

    private void EnsureSchema()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS images (
  id INTEGER PRIMARY KEY,
  source_root TEXT NOT NULL, rel_path TEXT NOT NULL, abs_path TEXT NOT NULL,
  file_name TEXT NOT NULL, ext TEXT NOT NULL,
  size_bytes INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
  width INTEGER, height INTEGER,
  meta_format TEXT, meta_source TEXT,
  prompt TEXT NOT NULL DEFAULT '', negative TEXT NOT NULL DEFAULT '',
  params_json TEXT,
  thumb_path TEXT,
  original_state TEXT NOT NULL DEFAULT 'present',
  archived INTEGER NOT NULL DEFAULT 0,
  scanned_at TEXT NOT NULL,
  phash INTEGER,
  UNIQUE(abs_path)
);
CREATE INDEX IF NOT EXISTS ix_images_archived ON images(archived);
CREATE INDEX IF NOT EXISTS ix_images_phash ON images(phash);

CREATE TABLE IF NOT EXISTS image_tags (
  image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
  token TEXT NOT NULL,
  PRIMARY KEY (image_id, token)
);
CREATE INDEX IF NOT EXISTS ix_image_tags_token ON image_tags(token);

CREATE TABLE IF NOT EXISTS tag_freq ( token TEXT PRIMARY KEY, df INTEGER NOT NULL );

CREATE VIRTUAL TABLE IF NOT EXISTS images_fts
  USING fts5(prompt, negative, content='images', content_rowid='id');

CREATE TABLE IF NOT EXISTS meta ( k TEXT PRIMARY KEY, v TEXT );

-- images_fts kept in sync (FTS5 external-content triggers)
CREATE TRIGGER IF NOT EXISTS images_ai AFTER INSERT ON images BEGIN
  INSERT INTO images_fts(rowid, prompt, negative) VALUES (new.id, new.prompt, new.negative);
END;
CREATE TRIGGER IF NOT EXISTS images_ad AFTER DELETE ON images BEGIN
  INSERT INTO images_fts(images_fts, rowid, prompt, negative) VALUES ('delete', old.id, old.prompt, old.negative);
END;
CREATE TRIGGER IF NOT EXISTS images_au AFTER UPDATE ON images BEGIN
  INSERT INTO images_fts(images_fts, rowid, prompt, negative) VALUES ('delete', old.id, old.prompt, old.negative);
  INSERT INTO images_fts(rowid, prompt, negative) VALUES (new.id, new.prompt, new.negative);
END;

-- tag_freq maintained incrementally from image_tags
CREATE TRIGGER IF NOT EXISTS tags_ai AFTER INSERT ON image_tags BEGIN
  INSERT INTO tag_freq(token, df) VALUES (new.token, 1)
    ON CONFLICT(token) DO UPDATE SET df = df + 1;
END;
CREATE TRIGGER IF NOT EXISTS tags_ad AFTER DELETE ON image_tags BEGIN
  UPDATE tag_freq SET df = df - 1 WHERE token = old.token;
  DELETE FROM tag_freq WHERE token = old.token AND df <= 0;
END;");
    }

    private void Migrate()
    {
        var current = GetMetaInt("schema_version");
        if (current is null)
        {
            // Fresh DB — EnsureSchema already created the current shape (incl. phash).
            SetMeta("schema_version", SchemaVersion.ToString());
            return;
        }
        // v1 → v2: add the perceptual-hash column to existing databases.
        if (current < 2)
        {
            if (!ColumnExists("images", "phash")) Exec("ALTER TABLE images ADD COLUMN phash INTEGER;");
            Exec("CREATE INDEX IF NOT EXISTS ix_images_phash ON images(phash);");
        }
        if (current < SchemaVersion)
            SetMeta("schema_version", SchemaVersion.ToString());
    }

    private bool ColumnExists(string table, string col)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});"; // table is a trusted constant
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            if (string.Equals(rd.GetString(1), col, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Insert or update by abs_path. Replaces the image's tag set; triggers keep
    /// tag_freq + images_fts in sync. Runs in one transaction.</summary>
    public void Upsert(ImageRow r)
    {
        using var tx = _con.BeginTransaction();
        UpsertCore(r);
        tx.Commit();
    }

    /// <summary>Batch upsert in a single transaction (the scanner's single-writer path).</summary>
    public void UpsertBatch(IReadOnlyList<ImageRow> rows)
    {
        if (rows.Count == 0) return;
        using var tx = _con.BeginTransaction();
        foreach (var r in rows) UpsertCore(r);
        tx.Commit();
    }

    private void UpsertCore(ImageRow r)
    {
        var existingId = FindIdByAbsPath(r.AbsPath);
        if (existingId is long id)
        {
            r.Id = id;
            using (var cmd = _con.CreateCommand())
            {
                cmd.CommandText = @"UPDATE images SET
                    source_root=$src, rel_path=$rel, file_name=$fn, ext=$ext,
                    size_bytes=$size, mtime_ticks=$mt, width=$w, height=$h,
                    meta_format=$mf, meta_source=$ms, prompt=$p, negative=$n,
                    params_json=$pj, thumb_path=$tp, original_state=$os, archived=$arch,
                    scanned_at=$sa, phash=$phash
                    WHERE id=$id;";
                BindImage(cmd, r);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            Exec("DELETE FROM image_tags WHERE image_id=$id;", ("$id", id));
        }
        else
        {
            using var cmd = _con.CreateCommand();
            cmd.CommandText = @"INSERT INTO images
                (source_root, rel_path, abs_path, file_name, ext, size_bytes, mtime_ticks,
                 width, height, meta_format, meta_source, prompt, negative, params_json,
                 thumb_path, original_state, archived, scanned_at, phash)
                VALUES ($src,$rel,$abs,$fn,$ext,$size,$mt,$w,$h,$mf,$ms,$p,$n,$pj,$tp,$os,$arch,$sa,$phash);
                SELECT last_insert_rowid();";
            BindImage(cmd, r);
            cmd.Parameters.AddWithValue("$abs", r.AbsPath);
            r.Id = Convert.ToInt64(cmd.ExecuteScalar());
        }

        InsertTags(r.Id, r.Tags);
    }

    private void InsertTags(long imageId, IEnumerable<string> tags)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO image_tags(image_id, token) VALUES ($id, $t);";
        var pId = cmd.Parameters.AddWithValue("$id", imageId);
        var pT = cmd.Parameters.Add("$t", SqliteType.Text);
        foreach (var t in tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct())
        {
            pT.Value = t;
            cmd.ExecuteNonQuery();
        }
    }

    private static void BindImage(SqliteCommand cmd, ImageRow r)
    {
        cmd.Parameters.AddWithValue("$src", r.SourceRoot);
        cmd.Parameters.AddWithValue("$rel", r.RelPath);
        cmd.Parameters.AddWithValue("$fn", r.FileName);
        cmd.Parameters.AddWithValue("$ext", r.Ext);
        cmd.Parameters.AddWithValue("$size", r.SizeBytes);
        cmd.Parameters.AddWithValue("$mt", r.MtimeTicks);
        cmd.Parameters.AddWithValue("$w", (object?)r.Width ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", (object?)r.Height ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mf", (object?)r.MetaFormat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ms", (object?)r.MetaSource ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", r.Prompt ?? "");
        cmd.Parameters.AddWithValue("$n", r.Negative ?? "");
        cmd.Parameters.AddWithValue("$pj", (object?)r.ParamsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tp", (object?)r.ThumbPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$os", r.OriginalState);
        cmd.Parameters.AddWithValue("$arch", r.Archived ? 1 : 0);
        cmd.Parameters.AddWithValue("$sa", r.ScannedAt);
        cmd.Parameters.AddWithValue("$phash", (object?)r.Phash ?? DBNull.Value);
    }

    /// <summary>Remove by id. Cascade deletes image_tags (decrementing tag_freq) and the FTS row.</summary>
    public bool Remove(long id)
    {
        var n = Exec("DELETE FROM images WHERE id=$id;", ("$id", id));
        return n > 0;
    }

    public bool RemoveByAbsPath(string absPath)
    {
        var n = Exec("DELETE FROM images WHERE abs_path=$abs;", ("$abs", absPath));
        return n > 0;
    }

    /// <summary>Remove all images indexed under a source root (when the user removes a folder in Settings).</summary>
    public int RemoveBySourceRoot(string root) => Exec("DELETE FROM images WHERE source_root=$r;", ("$r", root));

    public long? FindIdByAbsPath(string absPath)
    {
        var v = Scalar("SELECT id FROM images WHERE abs_path=$abs;", ("$abs", absPath));
        return v is null || v is DBNull ? null : Convert.ToInt64(v);
    }

    /// <summary>Recompute tag_freq from scratch (repair / consistency check).</summary>
    public void RebuildMatrix()
    {
        using var tx = _con.BeginTransaction();
        Exec("DELETE FROM tag_freq;");
        Exec("INSERT INTO tag_freq(token, df) SELECT token, COUNT(*) FROM image_tags GROUP BY token;");
        tx.Commit();
    }

    /// <summary>Stored (mtime, size) for incremental scan skip; null if not indexed.</summary>
    public (long Mtime, long Size)? GetFileSig(string absPath)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT mtime_ticks, size_bytes FROM images WHERE abs_path=$a;";
        cmd.Parameters.AddWithValue("$a", absPath);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.GetInt64(0), rd.GetInt64(1)) : null;
    }

    /// <summary>Delete rows under the given roots that weren't seen this pass AND no longer exist on disk.</summary>
    public int PruneMissing(IReadOnlyList<string> roots, ISet<string> seenAbs)
    {
        var toDelete = new List<long>();
        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = "SELECT id, abs_path, source_root FROM images;";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var id = rd.GetInt64(0); var abs = rd.GetString(1); var root = rd.GetString(2);
                if (!roots.Any(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase))) continue;
                if (seenAbs.Contains(abs)) continue;
                if (!File.Exists(abs)) toDelete.Add(id);
            }
        }
        if (toDelete.Count > 0)
        {
            using var tx = _con.BeginTransaction();
            foreach (var id in toDelete) Remove(id);
            tx.Commit();
        }
        return toDelete.Count;
    }

    /// <summary>
    /// Search: comma-AND tokens (image_tags join + HAVING COUNT(DISTINCT)=N) plus one
    /// prompt LIKE per quoted phrase, plus the archived filter. Returns a page + total count.
    /// </summary>
    public (IReadOnlyList<ImageRow> Page, int Total) Query(SearchFilter f, int page, int size, bool includeArchived, bool untaggedOnly = false, bool archivedOnly = false)
    {
        var where = new List<string>();
        var ps = new List<(string, object)>();
        if (archivedOnly) where.Add("i.archived=1");          // "The Bog" view = archived only
        else if (!includeArchived) where.Add("i.archived=0");
        // "Untagged only": images the parser found no tags for (metadata-free, or prompt all-stopwords).
        if (untaggedOnly) where.Add("i.id NOT IN (SELECT image_id FROM image_tags)");
        for (int k = 0; k < f.Phrases.Count; k++)
        {
            where.Add($"i.prompt LIKE $ph{k} ESCAPE '\\'");
            ps.Add(($"$ph{k}", "%" + EscapeLike(f.Phrases[k]) + "%"));
        }

        string fromWhere;
        if (f.Tokens.Count > 0)
        {
            var inNames = new List<string>();
            for (int k = 0; k < f.Tokens.Count; k++) { inNames.Add($"$t{k}"); ps.Add(($"$t{k}", f.Tokens[k])); }
            var extra = where.Count > 0 ? " AND " + string.Join(" AND ", where) : "";
            ps.Add(("$tcount", f.Tokens.Count));
            fromWhere = $"FROM images i JOIN image_tags t ON t.image_id=i.id " +
                        $"WHERE t.token IN ({string.Join(",", inNames)}){extra} " +
                        $"GROUP BY i.id HAVING COUNT(DISTINCT t.token)=$tcount";
        }
        else
        {
            var w = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
            fromWhere = $"FROM images i{w}";
        }

        int total = f.Tokens.Count > 0
            ? Convert.ToInt32(Scalar($"SELECT COUNT(*) FROM (SELECT i.id {fromWhere});", ps.ToArray()) ?? 0)
            : Convert.ToInt32(Scalar($"SELECT COUNT(*) {fromWhere};", ps.ToArray()) ?? 0);

        var rows = new List<ImageRow>();
        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = $"SELECT i.* {fromWhere} ORDER BY i.id LIMIT $lim OFFSET $off;";
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            cmd.Parameters.AddWithValue("$lim", size);
            cmd.Parameters.AddWithValue("$off", page * size);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) rows.Add(MapRow(rd));
        }
        return (rows, total);
    }

    /// <summary>Move a row to archived state at a new on-disk path (the Bog). Query hides archived by default.</summary>
    public void SetArchived(long id, string newAbsPath) =>
        Exec("UPDATE images SET abs_path=$p, archived=1, original_state='archived' WHERE id=$id;", ("$p", newAbsPath), ("$id", id));

    /// <summary>Refresh the file signature + dimensions after an in-place rewrite (T14 downsample),
    /// so an incremental re-scan treats the file as unchanged and the mtime-keyed thumbnail cache
    /// invalidates. Tags/metadata are untouched — downsampling preserves the embedded prompt.</summary>
    public void UpdateFileSig(long id, long sizeBytes, long mtimeTicks, int width, int height) =>
        Exec("UPDATE images SET size_bytes=$s, mtime_ticks=$m, width=$w, height=$h WHERE id=$id;",
            ("$s", sizeBytes), ("$m", mtimeTicks), ("$w", width), ("$h", height), ("$id", id));

    /// <summary>Full row by id (lightbox / inspector).</summary>
    public ImageRow? GetById(long id)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT i.* FROM images i WHERE i.id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? MapRow(rd) : null;
    }

    /// <summary>Autocomplete: top tags by frequency for a prefix (indexed range scan). Empty prefix = global top-N.</summary>
    public IReadOnlyList<(string Token, int Df)> TopTags(string prefix, int n)
    {
        var list = new List<(string, int)>();
        using var cmd = _con.CreateCommand();
        if (string.IsNullOrEmpty(prefix))
            cmd.CommandText = "SELECT token, df FROM tag_freq ORDER BY df DESC LIMIT $n;";
        else
        {
            var p = prefix.ToLowerInvariant();
            cmd.CommandText = "SELECT token, df FROM tag_freq WHERE token >= $p AND token < $phi ORDER BY df DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$p", p);
            cmd.Parameters.AddWithValue("$phi", PrefixHi(p));
        }
        cmd.Parameters.AddWithValue("$n", n);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add((rd.GetString(0), rd.GetInt32(1)));
        return list;
    }

    private static string PrefixHi(string p)
    {
        var chars = p.ToCharArray();
        chars[^1] = (char)(chars[^1] + 1);
        return new string(chars);
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static ImageRow MapRow(SqliteDataReader rd)
    {
        object? G(string c) { var i = rd.GetOrdinal(c); return rd.IsDBNull(i) ? null : rd.GetValue(i); }
        return new ImageRow
        {
            Id = Convert.ToInt64(G("id") ?? 0L),
            SourceRoot = (string)(G("source_root") ?? ""),
            RelPath = (string)(G("rel_path") ?? ""),
            AbsPath = (string)(G("abs_path") ?? ""),
            FileName = (string)(G("file_name") ?? ""),
            Ext = (string)(G("ext") ?? ""),
            SizeBytes = Convert.ToInt64(G("size_bytes") ?? 0L),
            MtimeTicks = Convert.ToInt64(G("mtime_ticks") ?? 0L),
            Width = G("width") is { } w ? Convert.ToInt32(w) : null,
            Height = G("height") is { } h ? Convert.ToInt32(h) : null,
            MetaFormat = (string)(G("meta_format") ?? "none"),
            MetaSource = (string?)G("meta_source"),
            Prompt = (string)(G("prompt") ?? ""),
            Negative = (string)(G("negative") ?? ""),
            ParamsJson = (string?)G("params_json"),
            ThumbPath = (string?)G("thumb_path"),
            OriginalState = (string)(G("original_state") ?? "present"),
            Archived = Convert.ToInt64(G("archived") ?? 0L) != 0,
            ScannedAt = (string)(G("scanned_at") ?? ""),
            Phash = G("phash") is { } ph ? Convert.ToInt64(ph) : null
        };
    }

    // --- read helpers (df lookup feeds autocomplete in T9; counts used by tests/footer) ---
    public long TagDf(string token) => Convert.ToInt64(Scalar("SELECT df FROM tag_freq WHERE token=$t;", ("$t", token)) ?? 0L);
    public long ImageCount(bool includeArchived = true) =>
        Convert.ToInt64(Scalar(includeArchived
            ? "SELECT COUNT(*) FROM images;"
            : "SELECT COUNT(*) FROM images WHERE archived=0;") ?? 0L);
    public long TagRowCount() => Convert.ToInt64(Scalar("SELECT COUNT(*) FROM image_tags;") ?? 0L);
    public long DistinctTagCount() => Convert.ToInt64(Scalar("SELECT COUNT(*) FROM tag_freq;") ?? 0L);
    /// <summary>Non-archived images the parser found no tags for (drives the sidebar "Unsorted" count).</summary>
    public long UntaggedCount() => Convert.ToInt64(Scalar(
        "SELECT COUNT(*) FROM images WHERE archived=0 AND id NOT IN (SELECT image_id FROM image_tags);") ?? 0L);

    // ---------------- Find Duplicates (perceptual hash) ----------------

    /// <summary>Store (or clear) an image's perceptual hash.</summary>
    public void UpdatePhash(long id, long? phash) =>
        Exec("UPDATE images SET phash=$ph WHERE id=$id;", ("$ph", (object?)phash ?? DBNull.Value), ("$id", id));

    /// <summary>Non-archived rows that still need a perceptual hash (id + path to hash).</summary>
    public IReadOnlyList<(long Id, string AbsPath)> RowsMissingPhash()
    {
        var list = new List<(long, string)>();
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT id, abs_path FROM images WHERE phash IS NULL AND archived=0;";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add((rd.GetInt64(0), rd.GetString(1)));
        return list;
    }

    /// <summary>Compute + store dHashes for any non-archived rows missing one (files that still
    /// exist). Hashing is parallel (reads only); writes are one single-writer transaction.
    /// Returns the number computed. Lazy — only runs when Find Duplicates is invoked.</summary>
    public int BackfillPhashes(CancellationToken ct = default, IProgress<HarvestProgress>? progress = null)
    {
        var todo = RowsMissingPhash().Where(r => File.Exists(r.AbsPath)).ToList();
        if (todo.Count == 0) return 0;
        progress?.Report(new HarvestProgress("Hashing images", 0, todo.Count));
        var results = new ConcurrentBag<(long Id, long Hash)>();
        int done = 0;
        var opts = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.ForEach(todo, opts, r =>
        {
            var h = PerceptualHash.Compute(r.AbsPath);
            if (h is long hv) results.Add((r.Id, hv));
            var n = Interlocked.Increment(ref done);
            if (n % 250 == 0) progress?.Report(new HarvestProgress("Hashing images", n, todo.Count));
        });
        using var tx = _con.BeginTransaction();
        foreach (var (id, hash) in results) UpdatePhash(id, hash);
        tx.Commit();
        return results.Count;
    }

    /// <summary>All (id, phash) for non-archived images that have a hash.</summary>
    private IReadOnlyList<(long Id, long Phash)> AllPhashes()
    {
        var list = new List<(long, long)>();
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT id, phash FROM images WHERE phash IS NOT NULL AND archived=0;";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add((rd.GetInt64(0), rd.GetInt64(1)));
        return list;
    }

    /// <summary>Group non-archived images by perceptual similarity. maxDistance 0 = exact dHash
    /// match (O(n) via hashing — already catches re-encodes/resizes); 1..3 = near-duplicates by
    /// Hamming distance, found scalably via 16-bit band bucketing (pigeonhole: a distance ≤3 across
    /// four 16-bit bands guarantees ≥1 identical band, so banding misses no true pair). Returns
    /// groups of ≥2 image ids, largest groups first.</summary>
    public IReadOnlyList<long[]> FindDuplicateGroups(int maxDistance = 0)
    {
        var all = AllPhashes();
        if (all.Count < 2) return Array.Empty<long[]>();

        if (maxDistance <= 0)
            return all.GroupBy(x => x.Phash)
                      .Where(g => g.Count() > 1)
                      .Select(g => g.Select(x => x.Id).OrderBy(i => i).ToArray())
                      .OrderByDescending(a => a.Length)
                      .ToArray();

        int d = Math.Min(maxDistance, 3);
        var parent = new Dictionary<long, long>(all.Count);
        foreach (var x in all) parent[x.Id] = x.Id;
        long Find(long x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(long a, long b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }

        // Collapse exact-duplicate hashes FIRST: union all ids sharing a hash (distance 0) and keep
        // ONE representative per DISTINCT hash for the band scan. This bounds the pairwise work to the
        // distinct-hash count and removes the degenerate blank/flat-image explosion — thousands of
        // identical all-zero/all-one hashes (black frames, white canvases) collapse to a single rep
        // instead of forming one giant O(n²) bucket.
        var rep = new Dictionary<long, long>();          // phash -> representative id
        var reps = new List<(long Id, long Hash)>();
        foreach (var x in all)
        {
            if (rep.TryGetValue(x.Phash, out var r)) Union(x.Id, r);
            else { rep[x.Phash] = x.Id; reps.Add((x.Id, x.Phash)); }
        }

        // Band-bucket only the distinct representatives (pigeonhole: a pair at distance ≤ d ≤ 3 shares
        // ≥1 of the four 16-bit bands). A cap backstops any pathological band bucket of distinct hashes.
        const int Cap = 4000;
        var bucket = new Dictionary<long, List<(long Id, long Hash)>>();
        for (int b = 0; b < 4; b++)
        {
            bucket.Clear();
            int shift = b * 16;
            foreach (var x in reps)
            {
                long key = ((long)b << 48) | (long)(ushort)((x.Hash >> shift) & 0xFFFF);
                if (!bucket.TryGetValue(key, out var lst)) bucket[key] = lst = new();
                lst.Add(x);
            }
            foreach (var members in bucket.Values)
            {
                if (members.Count < 2 || members.Count > Cap) continue;   // skip degenerate over-cap band
                for (int i = 0; i < members.Count; i++)
                    for (int j = i + 1; j < members.Count; j++)
                        if (PerceptualHash.Hamming(members[i].Hash, members[j].Hash) <= d)
                            Union(members[i].Id, members[j].Id);
            }
        }
        return all.Select(x => x.Id).GroupBy(Find)
                  .Where(g => g.Count() > 1)
                  .Select(g => g.OrderBy(i => i).ToArray())
                  .OrderByDescending(a => a.Length)
                  .ToArray();
    }
    public long FtsMatchCount(string query) =>
        Convert.ToInt64(Scalar("SELECT COUNT(*) FROM images_fts WHERE images_fts MATCH $q;", ("$q", query)) ?? 0L);

    private int? GetMetaInt(string k)
    {
        var v = Scalar("SELECT v FROM meta WHERE k=$k;", ("$k", k));
        return v is null || v is DBNull ? null : int.Parse(v.ToString()!);
    }
    private void SetMeta(string k, string v) =>
        Exec("INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v;", ("$k", k), ("$v", v));

    private int Exec(string sql, params (string, object)[] ps)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, val) in ps) cmd.Parameters.AddWithValue(n, val);
        return cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object)[] ps)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, val) in ps) cmd.Parameters.AddWithValue(n, val);
        return cmd.ExecuteScalar();
    }

    public void Dispose() => _con.Dispose();
}
