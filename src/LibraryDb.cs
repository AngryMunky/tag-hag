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
    /// v2 adds images.phash (perceptual hash) for Find Duplicates.
    /// v3 (additive, v2.0) adds images.favorite + tables image_notes, user_tags
    /// (+user_tag_freq +triggers), collections and collection_items for
    /// Favorites / Notes / Manual-Tagging / Collections. All v2.0 user state lives in
    /// separate tables/columns that UpsertCore never touches, so a rescan can't wipe it.
    /// v4 (additive, v2.1) adds images.optimized + opt_dim + opt_at (managed-store optimization
    /// state). Like favorite, 'optimized' is post-scan state EXCLUDED from UpsertCore, so a rescan
    /// of the managed store keeps it. See MarkOptimized + AppPaths.LibraryStoreDir.</summary>
    public const int SchemaVersion = 4;

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
  favorite INTEGER NOT NULL DEFAULT 0,
  optimized INTEGER NOT NULL DEFAULT 0,
  opt_dim INTEGER,
  opt_at TEXT,
  UNIQUE(abs_path)
);
CREATE INDEX IF NOT EXISTS ix_images_archived ON images(archived);
-- ix_images_phash + ix_images_favorite + ix_images_optimized are built in Migrate(), NOT here:
-- their columns (phash @ v2, favorite @ v3, optimized @ v4) may arrive via ALTER on an older DB,
-- and don't exist yet when EnsureSchema runs (BUG-T23-01: building an index over a not-yet-ALTERed
-- column here throws 'no such column' and crashes the open).

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
END;

-- ===== v3 (additive) — Favorites / Notes / Manual-Tags / Collections =====
-- NOTE: images.favorite is declared INLINE in the images CREATE above (so fresh DBs are born
-- with it) and ALTERed in for existing DBs by Migrate(). Its index ix_images_favorite is built
-- in Migrate() (NOT here): on an existing v2 DB the column does not exist yet when EnsureSchema
-- runs, so referencing images(favorite) here would fail 'no such column'.

-- Per-image free-text note (0..1 per image; one row per image, upserted on image_id).
CREATE TABLE IF NOT EXISTS image_notes (
  image_id INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
  body TEXT NOT NULL DEFAULT '',
  updated_at TEXT NOT NULL
);

-- Manual ('Your') tags: a SEPARATE table from image_tags so a rescan (which fully replaces
-- image_tags in UpsertCore) never wipes them. Blended into search via a query-time UNION.
CREATE TABLE IF NOT EXISTS user_tags (
  image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
  token TEXT NOT NULL,
  PRIMARY KEY (image_id, token)
);
CREATE INDEX IF NOT EXISTS ix_user_tags_token ON user_tags(token);

-- Frequency matrix for user_tags (mirrors tag_freq) — feeds blended autocomplete.
CREATE TABLE IF NOT EXISTS user_tag_freq ( token TEXT PRIMARY KEY, df INTEGER NOT NULL );

-- user_tag_freq maintained incrementally from user_tags (cloned from tags_ai/tags_ad).
-- user_tags + user_tag_freq are created ABOVE before these triggers reference them.
CREATE TRIGGER IF NOT EXISTS utags_ai AFTER INSERT ON user_tags BEGIN
  INSERT INTO user_tag_freq(token, df) VALUES (new.token, 1)
    ON CONFLICT(token) DO UPDATE SET df = df + 1;
END;
CREATE TRIGGER IF NOT EXISTS utags_ad AFTER DELETE ON user_tags BEGIN
  UPDATE user_tag_freq SET df = df - 1 WHERE token = old.token;
  DELETE FROM user_tag_freq WHERE token = old.token AND df <= 0;
END;

-- Named collections (many-to-many groups). Case-insensitive unique names.
CREATE TABLE IF NOT EXISTS collections (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  UNIQUE(name COLLATE NOCASE)
);

-- Collection membership. Double ON DELETE CASCADE keeps it orphan-free when either the
-- collection or the image is removed. INSERT OR IGNORE on the PK makes re-adds idempotent.
CREATE TABLE IF NOT EXISTS collection_items (
  collection_id INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
  image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
  PRIMARY KEY (collection_id, image_id)
);
CREATE INDEX IF NOT EXISTS ix_collection_items_image ON collection_items(image_id);");
    }

    private void Migrate()
    {
        // current is null on a fresh DB — EnsureSchema already created the full current shape
        // (incl. images.favorite and the v3 tables). Existing DBs run the additive branches below;
        // each only does what EnsureSchema's CREATE … IF NOT EXISTS cannot — add a COLUMN to an
        // existing table (SQLite can't add columns via CREATE TABLE IF NOT EXISTS).
        var current = GetMetaInt("schema_version");
        if (current is not null)
        {
            // v1 → v2: add the perceptual-hash column to existing databases.
            if (current < 2 && !ColumnExists("images", "phash"))
                Exec("ALTER TABLE images ADD COLUMN phash INTEGER;");
            // v2 → v3: add the favorite flag. The new v3 TABLES/indexes/triggers (image_notes,
            // user_tags(+freq+triggers), collections, collection_items) are already created by
            // EnsureSchema; only this existing-table COLUMN needs an ALTER. ADD COLUMN with a
            // constant DEFAULT is a metadata-only change in SQLite — instant even on a 100k DB,
            // and every existing row reads back as 0.
            if (current < 3 && !ColumnExists("images", "favorite"))
                Exec("ALTER TABLE images ADD COLUMN favorite INTEGER NOT NULL DEFAULT 0;");
            // v3 → v4: add the managed-store optimization columns. All three move together, so one
            // ColumnExists guard on 'optimized' is enough. ADD COLUMN with a constant/NULL default is
            // a metadata-only change in SQLite — instant even on a 100k DB; existing rows read back 0/NULL.
            if (current < 4 && !ColumnExists("images", "optimized"))
            {
                Exec("ALTER TABLE images ADD COLUMN optimized INTEGER NOT NULL DEFAULT 0;");
                Exec("ALTER TABLE images ADD COLUMN opt_dim INTEGER;");
                Exec("ALTER TABLE images ADD COLUMN opt_at TEXT;");
            }
        }

        // Indexes over columns that arrive via ALTER (phash @ v2, favorite @ v3, optimized @ v4) live
        // HERE, not in EnsureSchema: EnsureSchema runs FIRST, and on an older DB the column doesn't
        // exist yet, so a CREATE INDEX … ON images(<col>) there throws 'no such column' and crashes
        // the open. By this point the column is guaranteed present on every path (fresh: born in
        // EnsureSchema; upgraded: ALTERed just above). All are idempotent (IF NOT EXISTS).
        Exec("CREATE INDEX IF NOT EXISTS ix_images_phash ON images(phash);");
        Exec("CREATE INDEX IF NOT EXISTS ix_images_favorite ON images(favorite);");
        Exec("CREATE INDEX IF NOT EXISTS ix_images_optimized ON images(optimized);");

        if (current is null || current < SchemaVersion)
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
        // v3/v4 LOAD-BEARING INVARIANT: 'favorite' (v3), the v4 optimization columns ('optimized',
        // 'opt_dim', 'opt_at'), and all v2.0 user state (image_notes, user_tags, collection_items)
        // are deliberately ABSENT from the UPDATE/INSERT column lists below and from BindImage. A
        // rescan rewrites only scanned facts + image_tags; post-scan state must survive untouched
        // (UPDATE leaves them as-is; INSERT lets them default). This is what lets a rescan of the
        // managed store keep an image's optimized flag (MarkOptimized sets it; the next scan of the
        // same in-store abs_path is an UPDATE that must NOT clear it). phash IS bound here — it's
        // recomputed from pixels, not user/post-scan state. Do NOT add favorite/optimized here.
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
    public (IReadOnlyList<ImageRow> Page, int Total) Query(SearchFilter f, int page, int size, bool includeArchived, bool untaggedOnly = false, bool archivedOnly = false, bool favoritesOnly = false, long? collectionId = null, bool optimizedOnly = false, string? folderPath = null, string? folderRoot = null, bool includeSubfolders = false)
    {
        var where = new List<string>();
        var ps = new List<(string, object)>();
        if (archivedOnly) where.Add("i.archived=1");          // "The Bog" view = archived only
        else if (!includeArchived) where.Add("i.archived=0");
        // "Untagged only": images the parser found no tags for (metadata-free, or prompt all-stopwords).
        if (untaggedOnly) where.Add("i.id NOT IN (SELECT image_id FROM image_tags)");
        // v2.0 scope filters — folded into the SAME where list so they apply in BOTH the token and
        // no-token branches AND the COUNT (one fromWhere → page==total, no desync).
        if (favoritesOnly) where.Add("i.favorite=1");                                  // Favorites view (T24)
        if (optimizedOnly) where.Add("i.optimized=1");                                 // Optimized view (T31)
        if (collectionId is long cid)                                                  // Collection view (T28)
        {
            where.Add("i.id IN (SELECT image_id FROM collection_items WHERE collection_id=$cid)");
            ps.Add(("$cid", cid));
        }
        // Folder view (T33/F24): scoped to a (folderRoot, folderPath) node so identical rel-dirs in
        // different roots — the managed store is always a second root — don't merge. folderPath is a
        // rel-DIR ("" = files directly under the root); includeSubfolders widens to the whole subtree.
        // substr/instr (NOT LIKE) so a folder name containing %/_ can't act as a wildcard. Folds into
        // the shared where list → page==total preserved.
        if (folderRoot is { Length: > 0 }) { where.Add("i.source_root=$froot"); ps.Add(("$froot", folderRoot)); }
        if (folderPath is not null)
        {
            if (folderPath.Length == 0)
            {
                if (!includeSubfolders) where.Add("instr(i.rel_path,'\\')=0"); // files directly under the root only
                // includeSubfolders at root level → no rel_path constraint (the whole root)
            }
            else
            {
                var pfx = folderPath.TrimEnd('\\') + "\\";
                ps.Add(("$fpfx", pfx));
                where.Add("substr(i.rel_path,1,length($fpfx))=$fpfx");                        // under this folder
                if (!includeSubfolders) where.Add("instr(substr(i.rel_path,length($fpfx)+1),'\\')=0"); // direct children only
            }
        }
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
            // UNION (not ALL) of prompt tags + manual user tags: a token in both counts once, so the
            // comma-AND arity (HAVING COUNT(DISTINCT)=N) is preserved while manual tags join search (T26).
            fromWhere = $"FROM images i JOIN (SELECT image_id, token FROM image_tags " +
                        $"UNION SELECT image_id, token FROM user_tags) t ON t.image_id=i.id " +
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

    // ---------------- v4 managed-store optimization (v2.1) ----------------

    /// <summary>Record that an image has been resampled into the Tag Hag-managed store (T30): repoint
    /// its path columns to the new in-store location and set optimized/opt_dim/opt_at. This is the SAME
    /// row id — a MOVE, not a new row — so every v3 user-state FK (favorite, notes, user_tags,
    /// collection_items) follows the image automatically. source_root/rel_path are recomputed RELATIVE
    /// to the managed store root so the next scan of the store reads this exact file as 'unchanged'
    /// rather than inserting a fresh row (the no-double-row invariant, R16). 'optimized' is set HERE on
    /// purpose: it is excluded from UpsertCore, so a later rescan never clears it.</summary>
    public void MarkOptimized(long id, string newAbsPath, int maxDim, string at)
    {
        var store = AppPaths.LibraryStoreDir;
        var abs = Path.GetFullPath(newAbsPath);
        var rel = Path.GetRelativePath(store, abs);
        Exec(@"UPDATE images SET abs_path=$abs, source_root=$src, rel_path=$rel,
                optimized=1, opt_dim=$dim, opt_at=$at WHERE id=$id;",
            ("$abs", abs), ("$src", store), ("$rel", rel),
            ("$dim", maxDim), ("$at", at), ("$id", id));
    }

    // ===================== Path re-link / repath (T32 + T33) =====================
    // Shared contract (coordination note c): a row is MOVED by rewriting ONLY its path columns
    // (abs_path/source_root/rel_path) WHERE id — the id is preserved, so every id-keyed FK
    // (image_tags, user_tags, image_notes, collection_items) and every post-scan column (favorite,
    // optimized, archived) survives untouched. rel_path is always recomputed from the new root via
    // Path.GetRelativePath (the same rule as MarkOptimized). RepathRow = one row (re-link);
    // RepathFolder = a folder subtree (rename); neither ever touches user/post-scan state.

    /// <summary>Move a single row to a new path, preserving its id + all user/post-scan state
    /// (T32/F22 re-link). rel_path is recomputed relative to <paramref name="sourceRoot"/>.</summary>
    public void RepathRow(long id, string newAbsPath, string sourceRoot)
    {
        var abs = Path.GetFullPath(newAbsPath);
        var rel = Path.GetRelativePath(sourceRoot, abs);
        Exec(@"UPDATE images SET abs_path=$abs, source_root=$src, rel_path=$rel WHERE id=$id;",
            ("$abs", abs), ("$src", sourceRoot), ("$rel", rel), ("$id", id));
    }

    /// <summary>Rename a single image in the index (T34/F23): like <see cref="RepathRow"/> but also
    /// rewrites file_name (the only extra column a rename touches vs a move). id preserved → all
    /// user/post-scan state survives. The physical file rename is done by the caller first.</summary>
    public void RenameRow(long id, string newAbsPath, string sourceRoot)
    {
        var abs = Path.GetFullPath(newAbsPath);
        var rel = Path.GetRelativePath(sourceRoot, abs);
        Exec(@"UPDATE images SET abs_path=$abs, source_root=$src, rel_path=$rel, file_name=$fn WHERE id=$id;",
            ("$abs", abs), ("$src", sourceRoot), ("$rel", rel), ("$fn", Path.GetFileName(abs)), ("$id", id));
    }

    /// <summary>A row whose indexed file is gone AND that carries a perceptual hash — a candidate
    /// for re-linking against a freshly-seen orphan file (T32/F22).</summary>
    public readonly record struct DisappearedRow(long Id, long Phash, long Size);

    /// <summary>Rows under the scanned roots whose abs_path was NOT seen this pass, no longer exist
    /// on disk, and HAVE a phash — files that vanished from their indexed location and can be matched
    /// by content. (Rows with no phash can't be matched — a vanished file can't be re-hashed — so they
    /// fall through to the normal prune exactly as before: no regression. Archived rows ARE included
    /// so a moved Bog file keeps its archived flag via the surviving id.)</summary>
    public IReadOnlyList<DisappearedRow> DisappearedCandidates(IReadOnlyList<string> roots, ISet<string> seenAbs)
    {
        var list = new List<DisappearedRow>();
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT id, abs_path, source_root, phash, size_bytes FROM images WHERE phash IS NOT NULL;";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var id = rd.GetInt64(0); var abs = rd.GetString(1); var root = rd.GetString(2);
            if (!roots.Any(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase))) continue;
            if (seenAbs.Contains(abs)) continue;   // still where we indexed it — not disappeared
            if (File.Exists(abs)) continue;        // still on disk somehow — leave it
            list.Add(new DisappearedRow(id, rd.GetInt64(3), rd.GetInt64(4)));
        }
        return list;
    }

    /// <summary>Apply re-link decisions in one transaction (T32/F22): for each (old row → orphan)
    /// pair, delete the freshly-inserted orphan row (freeing its abs_path) then repath the original
    /// row onto it (keeps id + user-state); afterwards persist the computed phash for any orphan we
    /// hashed but did NOT consume (backfill-on-demand for orphans, per F22). Returns the re-link
    /// count. Order is delete-then-repath so the UNIQUE abs_path is never violated.</summary>
    public int ApplyRelinks(IReadOnlyList<(long OldId, string NewAbs, string NewRoot, long OrphanId)> relinks,
                            IReadOnlyDictionary<long, long> orphanHashes)
    {
        var consumed = new HashSet<long>();
        using var tx = _con.BeginTransaction();
        foreach (var (oldId, newAbs, newRoot, orphanId) in relinks)
        {
            Remove(orphanId);                  // drop the duplicate new row (cascade-clears its empty FKs)
            RepathRow(oldId, newAbs, newRoot); // original row inherits the new location
            consumed.Add(orphanId);
        }
        foreach (var (id, hash) in orphanHashes)
            if (!consumed.Contains(id)) UpdatePhash(id, hash);
        tx.Commit();
        return relinks.Count;
    }

    /// <summary>Rename/move a folder subtree in the index (T33/F24): every row under
    /// <paramref name="oldRelDir"/> (within <paramref name="sourceRoot"/>) is repathed to the same
    /// position under <paramref name="newRelDir"/>, in ONE transaction. The physical move is done by
    /// FileOps.MoveFolder first; this keeps the DB in lock-step (ids + user-state preserved). Returns
    /// the number of rows moved. source_root is unchanged (a rename stays within its root).</summary>
    public int RepathFolder(string sourceRoot, string oldRelDir, string newRelDir)
    {
        oldRelDir = oldRelDir.TrimEnd('\\');
        newRelDir = newRelDir.TrimEnd('\\');
        var oldSep = oldRelDir + "\\";
        var toMove = new List<(long Id, string Rel)>();
        using (var cmd = _con.CreateCommand())
        {
            // substr-prefix (NOT LIKE — a folder name may contain %/_), scoped to the one root.
            cmd.CommandText = "SELECT id, rel_path FROM images WHERE source_root=$r AND substr(rel_path,1,length($p))=$p;";
            cmd.Parameters.AddWithValue("$r", sourceRoot);
            cmd.Parameters.AddWithValue("$p", oldSep);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) toMove.Add((rd.GetInt64(0), rd.GetString(1)));
        }
        if (toMove.Count == 0) return 0;
        using var tx = _con.BeginTransaction();
        foreach (var (id, rel) in toMove)
        {
            var newRel = newRelDir + rel.Substring(oldRelDir.Length); // rel begins with oldRelDir + "\..."
            var newAbs = Path.GetFullPath(Path.Combine(sourceRoot, newRel));
            Exec("UPDATE images SET abs_path=$a, rel_path=$rl WHERE id=$id;", ("$a", newAbs), ("$rl", newRel), ("$id", id));
        }
        tx.Commit();
        return toMove.Count;
    }

    /// <summary>The derived folder tree (T33/F24): one top node per source_root, nested rel-dirs
    /// beneath, each node carrying a recursive (subtree) non-archived image count. No folders table —
    /// built from the distinct (source_root, rel-dir) of the indexed images. O(images) to read; the
    /// tree itself is O(distinct folders) (R17 — fine at v2.1 scale; cache later if needed).</summary>
    public IReadOnlyList<FolderNode> FolderTree()
    {
        // 1) direct file counts per (root, rel-dir).
        var direct = new Dictionary<(string Root, string Dir), int>();
        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = "SELECT source_root, rel_path FROM images WHERE archived=0;";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var root = rd.GetString(0); var rel = rd.GetString(1);
                var slash = rel.LastIndexOf('\\');
                var dir = slash < 0 ? "" : rel.Substring(0, slash);
                var key = (root, dir);
                direct[key] = direct.TryGetValue(key, out var c) ? c + 1 : 1;
            }
        }

        // 2) build a node per root, splitting each rel-dir into segments (intermediate dirs created
        //    on the way even if they hold no files directly).
        var roots = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        FolderNode RootNode(string root)
        {
            if (!roots.TryGetValue(root, out var n))
            {
                var leaf = Path.GetFileName(root.TrimEnd('\\', '/'));
                roots[root] = n = new FolderNode { Root = root, Path = "", Name = string.IsNullOrEmpty(leaf) ? root : leaf };
            }
            return n;
        }
        foreach (var ((root, dir), cnt) in direct)
        {
            var node = RootNode(root);
            if (dir.Length == 0) { node.Count += cnt; continue; } // files directly under the root
            var segs = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var cur = node; var built = "";
            foreach (var seg in segs)
            {
                built = built.Length == 0 ? seg : built + "\\" + seg;
                var child = cur.Children.FirstOrDefault(c => string.Equals(c.Name, seg, StringComparison.OrdinalIgnoreCase));
                if (child is null) { child = new FolderNode { Root = root, Path = built, Name = seg }; cur.Children.Add(child); }
                cur = child;
            }
            cur.Count += cnt; // direct count lands on the leaf segment node
        }

        // 3) roll counts up so each node totals its whole subtree, and sort children by name.
        int RollUp(FolderNode n)
        {
            int total = n.Count;
            n.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var c in n.Children) total += RollUp(c);
            n.Count = total;
            return total;
        }
        var ordered = roots.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var r in ordered) RollUp(r);
        return ordered;
    }

    /// <summary>Persist the managed-store root in meta(library_store_root) so the app + future
    /// migrations know which scanned root Tag Hag owns. Idempotent (called once per scan).</summary>
    public void SetLibraryStoreRoot(string path) => SetMeta("library_store_root", path);

    /// <summary>The recorded managed-store root, or null if never set.</summary>
    public string? GetLibraryStoreRoot() => Scalar("SELECT v FROM meta WHERE k='library_store_root';") as string;

    // ---------------- v4 Library Optimization scope/preview (T30 / F20) ----------------

    /// <summary>Build the WHERE fragment + bound params for an optimize scope and append them to
    /// <paramref name="ps"/>. Scopes: "all" (the whole library), "selection" (the explicit
    /// <paramref name="ids"/>), or "folder:&lt;rel-path-prefix&gt;" (an exact dir or any descendant).
    /// Returns "0" (matches nothing) for an empty selection. The fragment is combined by callers with
    /// the archived/optimized predicates so a single shared clause drives both preview and the id set.</summary>
    private static string ScopeClause(string? scope, IReadOnlyList<long>? ids, List<(string, object)> ps)
    {
        scope ??= "all";
        if (scope == "selection")
        {
            if (ids is null || ids.Count == 0) return "0";
            var names = new List<string>();
            for (int i = 0; i < ids.Count; i++) { names.Add($"$os{i}"); ps.Add(($"$os{i}", ids[i])); }
            return $"id IN ({string.Join(",", names)})";
        }
        if (scope.StartsWith("folder:", StringComparison.Ordinal))
        {
            var prefix = scope["folder:".Length..];
            // rel_path is stored with the OS separator the scanner produced (Windows '\'). Match the
            // exact folder OR any descendant via an exact-prefix substr comparison (NOT LIKE — that
            // would treat %/_ in a folder name as wildcards and over-match, BUG flagged in review).
            ps.Add(("$ofp", prefix));
            ps.Add(("$ofd", prefix + "\\"));
            return "(rel_path = $ofp OR substr(rel_path, 1, length($ofd)) = $ofd)";
        }
        return "1"; // all
    }

    /// <summary>Ids eligible to optimize within a scope at the given <paramref name="maxDim"/>: not
    /// archived, not already optimized, and NOT already within the size target (using the stored
    /// width/height — rows with unknown dims are included and the runtime re-checks). Oldest-first.
    /// Excluding already-within-budget rows here (rather than only at runtime) keeps the preview honest
    /// and lets a later re-run at a SMALLER maxDim pick up images that were within the old budget.</summary>
    public IReadOnlyList<long> OptimizeEligibleIds(string? scope, int maxDim, IReadOnlyList<long>? ids)
    {
        var ps = new List<(string, object)>();
        var clause = ScopeClause(scope, ids, ps);
        ps.Add(("$omd", maxDim));
        var list = new List<long>();
        using var cmd = _con.CreateCommand();
        cmd.CommandText = $"SELECT id FROM images WHERE ({clause}) AND archived=0 AND optimized=0 " +
                          "AND (width IS NULL OR height IS NULL OR width > $omd OR height > $omd) ORDER BY id;";
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetInt64(0));
        return list;
    }

    /// <summary>Cheap preview tally for a scope at <paramref name="maxDim"/> (no disk reads):
    /// Count = images that WILL be optimized (eligible), Skip = the rest in scope (already optimized OR
    /// already within budget), Bytes = SUM(size_bytes) of the eligible set. Bytes is an ESTIMATE of the
    /// current footprint of those images — the post-resample saving is only known once the job runs.</summary>
    public (int Count, int Skip, long Bytes) OptimizePreview(string? scope, int maxDim, IReadOnlyList<long>? ids)
    {
        var ps = new List<(string, object)>();
        var clause = ScopeClause(scope, ids, ps);
        ps.Add(("$omd", maxDim));
        var arr = ps.ToArray();
        var elig = $"({clause}) AND archived=0 AND optimized=0 AND (width IS NULL OR height IS NULL OR width > $omd OR height > $omd)";
        int count = Convert.ToInt32(Scalar($"SELECT COUNT(*) FROM images WHERE {elig};", arr) ?? 0);
        int inScope = Convert.ToInt32(Scalar($"SELECT COUNT(*) FROM images WHERE ({clause}) AND archived=0;", arr) ?? 0);
        long bytes = Convert.ToInt64(Scalar($"SELECT COALESCE(SUM(size_bytes),0) FROM images WHERE {elig};", arr) ?? 0L);
        return (count, inScope - count, bytes);
    }

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
            cmd.CommandText = "SELECT token, SUM(df) AS df FROM (" +
                "SELECT token, df FROM tag_freq UNION ALL SELECT token, df FROM user_tag_freq" +
                ") GROUP BY token ORDER BY df DESC LIMIT $n;";
        else
        {
            var p = prefix.ToLowerInvariant();
            // Blend prompt tags (tag_freq) + manual tags (user_tag_freq); the prefix range is pushed
            // into BOTH legs (each uses its index) then summed, so a token in both ranks by total df.
            cmd.CommandText = "SELECT token, SUM(df) AS df FROM (" +
                "SELECT token, df FROM tag_freq WHERE token >= $p AND token < $phi " +
                "UNION ALL SELECT token, df FROM user_tag_freq WHERE token >= $p AND token < $phi" +
                ") GROUP BY token ORDER BY df DESC LIMIT $n;";
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
            Phash = G("phash") is { } ph ? Convert.ToInt64(ph) : null,
            Favorite = Convert.ToInt64(G("favorite") ?? 0L) != 0,
            Optimized = Convert.ToInt64(G("optimized") ?? 0L) != 0,
            OptDim = G("opt_dim") is { } od ? Convert.ToInt32(od) : null,
            OptAt = (string?)G("opt_at")
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

    // ---------------- v3 user state: favorites / notes / user tags / collections ----------------
    // All of this lives in separate tables/columns that UpsertCore never rewrites, so a rescan
    // never clears it (the load-bearing v3 invariant). Manual tags are normalized via the SAME
    // PromptSimilarity.TokenSet the scanner + SearchParser use, so search/autocomplete stay consistent.

    /// <summary>Set/clear an image's favorite flag. Excluded from UpsertCore → survives a rescan.</summary>
    public void SetFavorite(long id, bool on) =>
        Exec("UPDATE images SET favorite=$f WHERE id=$id;", ("$f", on ? 1 : 0), ("$id", id));

    /// <summary>Bulk set/clear the favorite flag over a selection in one transaction (T34/F23
    /// action-bar ★). Same per-row UPDATE as <see cref="SetFavorite"/>; survives rescan.</summary>
    public void SetFavoriteBulk(IEnumerable<long> ids, bool on)
    {
        using var tx = _con.BeginTransaction();
        foreach (var id in ids) Exec("UPDATE images SET favorite=$f WHERE id=$id;", ("$f", on ? 1 : 0), ("$id", id));
        tx.Commit();
    }

    /// <summary>Non-archived favorited images (sidebar Favorites count).</summary>
    public long FavoriteCount() =>
        Convert.ToInt64(Scalar("SELECT COUNT(*) FROM images WHERE favorite=1 AND archived=0;") ?? 0L);

    /// <summary>Non-archived optimized images (sidebar Optimized count, T31).</summary>
    public long OptimizedCount() =>
        Convert.ToInt64(Scalar("SELECT COUNT(*) FROM images WHERE optimized=1 AND archived=0;") ?? 0L);

    /// <summary>Upsert the per-image note (empty body clears it to ''); stamps updated_at (local ISO).</summary>
    public void SetNote(long id, string body) =>
        Exec("INSERT INTO image_notes(image_id, body, updated_at) VALUES($id,$b,$u) " +
             "ON CONFLICT(image_id) DO UPDATE SET body=$b, updated_at=$u;",
             ("$id", id), ("$b", body ?? ""), ("$u", DateTime.Now.ToString("s")));

    /// <summary>(body, updatedAt) for an image, or null when no note exists.</summary>
    public (string Body, string UpdatedAt)? GetNote(long id)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT body, updated_at FROM image_notes WHERE image_id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.GetString(0), rd.GetString(1)) : null;
    }

    /// <summary>Add manual tags from free text (TokenSet → 0..N normalized tokens; INSERT OR IGNORE).
    /// Returns the tokens parsed from the text. user_tag_freq is trigger-maintained.</summary>
    public IReadOnlyList<string> AddUserTags(long id, string text)
    {
        var tokens = PromptSimilarity.TokenSet(text).ToList();
        if (tokens.Count == 0) return tokens;
        using var tx = _con.BeginTransaction();
        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO user_tags(image_id, token) VALUES($id,$t);";
            cmd.Parameters.AddWithValue("$id", id);
            var pT = cmd.Parameters.Add("$t", SqliteType.Text);
            foreach (var t in tokens) { pT.Value = t; cmd.ExecuteNonQuery(); }
        }
        tx.Commit();
        return tokens;
    }

    /// <summary>Remove one manual tag from an image.</summary>
    public void RemoveUserTag(long id, string token) =>
        Exec("DELETE FROM user_tags WHERE image_id=$id AND token=$t;", ("$id", id), ("$t", token));

    /// <summary>Prompt-derived tags (read-only Charms) for an image — from image_tags.</summary>
    public IReadOnlyList<string> PromptTagsFor(long id) => TokenList("image_tags", id);
    /// <summary>Manual ('Your') tags for an image — from user_tags.</summary>
    public IReadOnlyList<string> UserTagsFor(long id) => TokenList("user_tags", id);
    private IReadOnlyList<string> TokenList(string table, long id)
    {
        var list = new List<string>();
        using var cmd = _con.CreateCommand();
        cmd.CommandText = $"SELECT token FROM {table} WHERE image_id=$id ORDER BY token;"; // table is a trusted constant
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetString(0));
        return list;
    }

    /// <summary>Collections name-sorted (COLLATE NOCASE) with their membership count.</summary>
    public IReadOnlyList<(long Id, string Name, int Count)> ListCollections()
    {
        var list = new List<(long, string, int)>();
        using var cmd = _con.CreateCommand();
        cmd.CommandText = @"SELECT c.id, c.name,
                              (SELECT COUNT(*) FROM collection_items ci WHERE ci.collection_id=c.id)
                            FROM collections c ORDER BY c.name COLLATE NOCASE;";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add((rd.GetInt64(0), rd.GetString(1), rd.GetInt32(2)));
        return list;
    }

    /// <summary>Create a collection. Returns its id, or -1 if the name already exists (UNIQUE NOCASE).</summary>
    public long CreateCollection(string name)
    {
        try
        {
            using var cmd = _con.CreateCommand();
            cmd.CommandText = "INSERT INTO collections(name) VALUES($n); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", (name ?? "").Trim());
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (SqliteException) { return -1; }   // UNIQUE(name COLLATE NOCASE) violation
    }

    /// <summary>Rename a collection. Returns false if the new name collides (UNIQUE NOCASE) or no row.</summary>
    public bool RenameCollection(long id, string name)
    {
        try { return Exec("UPDATE collections SET name=$n WHERE id=$id;", ("$n", (name ?? "").Trim()), ("$id", id)) > 0; }
        catch (SqliteException) { return false; }
    }

    /// <summary>Delete a collection; collection_items rows cascade away.</summary>
    public void DeleteCollection(long id) => Exec("DELETE FROM collections WHERE id=$id;", ("$id", id));

    /// <summary>Add images to a collection (idempotent — INSERT OR IGNORE on the PK).</summary>
    public void AddToCollection(long collectionId, IEnumerable<long> imageIds) =>
        CollectionMembership("INSERT OR IGNORE INTO collection_items(collection_id, image_id) VALUES($c,$i);", collectionId, imageIds);

    /// <summary>Remove images from a collection.</summary>
    public void RemoveFromCollection(long collectionId, IEnumerable<long> imageIds) =>
        CollectionMembership("DELETE FROM collection_items WHERE collection_id=$c AND image_id=$i;", collectionId, imageIds);

    private void CollectionMembership(string sql, long collectionId, IEnumerable<long> imageIds)
    {
        using var tx = _con.BeginTransaction();
        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$c", collectionId);
            var pI = cmd.Parameters.Add("$i", SqliteType.Integer);
            foreach (var id in imageIds) { pI.Value = id; cmd.ExecuteNonQuery(); }
        }
        tx.Commit();
    }

    /// <summary>T27 Auto-Tag (suggest-only KNN): find non-archived images within Hamming maxDistance
    /// of the target's perceptual hash that ALSO carry manual user tags, then vote-rank their user
    /// tags (excluding tokens already on the target) capped at <paramref name="max"/>. Writes NOTHING
    /// — the caller backfills phashes first (so a NULL target hash yields an empty result here).</summary>
    public AutotagResult SuggestTagsByPhash(long id, int maxDistance = 3, int max = 20)
    {
        var res = new AutotagResult();
        if (GetById(id)?.Phash is not long th) return res;   // not hashed yet → nothing to suggest
        int d = Math.Min(Math.Max(maxDistance, 0), 3);

        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = @"SELECT i.id, i.phash FROM images i
                                WHERE i.archived=0 AND i.phash IS NOT NULL AND i.id<>$id
                                  AND EXISTS (SELECT 1 FROM user_tags u WHERE u.image_id=i.id);";
            cmd.Parameters.AddWithValue("$id", id);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var dist = PerceptualHash.Hamming(th, rd.GetInt64(1));
                if (dist <= d) res.Neighbors.Add((rd.GetInt64(0), dist));
            }
        }
        res.Neighbors.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (res.Neighbors.Count == 0) return res;

        // Exclude tokens already on the target (its manual tags + prompt tags).
        var existing = new HashSet<string>(UserTagsFor(id), StringComparer.Ordinal);
        foreach (var t in PromptTagsFor(id)) existing.Add(t);

        var votes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (nid, _) in res.Neighbors)
            foreach (var tok in UserTagsFor(nid))
                if (!existing.Contains(tok)) votes[tok] = votes.GetValueOrDefault(tok) + 1;

        res.Suggestions = votes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(max).Select(kv => new AutotagSuggestion(kv.Key, kv.Value)).ToList();
        return res;
    }

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
