using System.Text;
using Microsoft.Data.Sqlite;
using Exif = SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace TheTagHag;

/// <summary>
/// T1 skeleton entry point. The GUI (MainForm + WebView2 gallery) arrives in later tickets;
/// for now this proves the single-file self-contained publish model carries SQLite + FTS5.
/// Run `TheTagHag.exe --selftest` to execute the SQLite/FTS5 smoke test.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest();

        if (args.Any(a => string.Equals(a, "--selftest-db", StringComparison.OrdinalIgnoreCase)))
            return DbSelfTest();

        var pngIdx = Array.FindIndex(args, a => string.Equals(a, "--selftest-png", StringComparison.OrdinalIgnoreCase));
        if (pngIdx >= 0)
            return PngSelfTest(pngIdx + 1 < args.Length ? args[pngIdx + 1] : ".");

        var metaIdx = Array.FindIndex(args, a => string.Equals(a, "--selftest-meta", StringComparison.OrdinalIgnoreCase));
        if (metaIdx >= 0)
            return MetaSelfTest(metaIdx + 1 < args.Length ? args[metaIdx + 1] : ".");

        var comfyIdx = Array.FindIndex(args, a => string.Equals(a, "--selftest-comfy", StringComparison.OrdinalIgnoreCase));
        if (comfyIdx >= 0)
            return ComfySelfTest(comfyIdx + 1 < args.Length ? args[comfyIdx + 1] : ".");

        var scanIdx = Array.FindIndex(args, a => string.Equals(a, "--selftest-scan", StringComparison.OrdinalIgnoreCase));
        if (scanIdx >= 0)
            return ScanSelfTest(scanIdx + 1 < args.Length ? args[scanIdx + 1] : ".");

        var uiIdx = Array.FindIndex(args, a => string.Equals(a, "--selftest-ui", StringComparison.OrdinalIgnoreCase));
        if (uiIdx >= 0)
            return UiSelfTest(uiIdx + 1 < args.Length ? args[uiIdx + 1] : ".");

        var foIdx = Array.FindIndex(args, a => string.Equals(a, "--selftest-fileops", StringComparison.OrdinalIgnoreCase));
        if (foIdx >= 0)
            return FileOpsSelfTest(foIdx + 1 < args.Length ? args[foIdx + 1] : ".");

        var optIdx = Array.FindIndex(args, a => string.Equals(a, "--selftest-optimize", StringComparison.OrdinalIgnoreCase));
        if (optIdx >= 0)
            return OptimizeSelfTest(optIdx + 1 < args.Length ? args[optIdx + 1] : ".");

        var setIdx = Array.FindIndex(args, a => string.Equals(a, "--selftest-settings", StringComparison.OrdinalIgnoreCase));
        if (setIdx >= 0)
            return SettingsSelfTest(setIdx + 1 < args.Length ? args[setIdx + 1] : ".");

        var harvIdx = Array.FindIndex(args, a => string.Equals(a, "--selftest-harvest", StringComparison.OrdinalIgnoreCase));
        if (harvIdx >= 0)
            return HarvestSelfTest(harvIdx + 1 < args.Length ? args[harvIdx + 1] : ".");

        if (args.Any(a => string.Equals(a, "--selftest-react", StringComparison.OrdinalIgnoreCase)))
            return ReactSelfTest();

        if (args.Any(a => string.Equals(a, "--selftest-dupes", StringComparison.OrdinalIgnoreCase)))
            return DupesSelfTest();

        if (args.Any(a => string.Equals(a, "--selftest-v3migrate", StringComparison.OrdinalIgnoreCase)))
            return V3MigrateSelfTest();

        if (args.Any(a => string.Equals(a, "--selftest-favorites", StringComparison.OrdinalIgnoreCase)))
            return FavoritesSelfTest();
        if (args.Any(a => string.Equals(a, "--selftest-notes", StringComparison.OrdinalIgnoreCase)))
            return NotesSelfTest();
        if (args.Any(a => string.Equals(a, "--selftest-usertags", StringComparison.OrdinalIgnoreCase)))
            return UserTagsSelfTest();
        if (args.Any(a => string.Equals(a, "--selftest-collections", StringComparison.OrdinalIgnoreCase)))
            return CollectionsSelfTest();
        if (args.Any(a => string.Equals(a, "--selftest-autotag", StringComparison.OrdinalIgnoreCase)))
            return AutotagSelfTest();

        if (args.Any(a => string.Equals(a, "--selftest-optimize", StringComparison.OrdinalIgnoreCase)))
            return OptimizeSelfTest();

        // Dev tool: (re)generate the app icon. `--makeicon [path]` (default Resources\app.ico).
        var iconIdx = Array.FindIndex(args, a => string.Equals(a, "--makeicon", StringComparison.OrdinalIgnoreCase));
        if (iconIdx >= 0)
        {
            Native.TryAttachParentConsole();
            var outPath = iconIdx + 1 < args.Length ? args[iconIdx + 1] : Path.Combine(AppPaths.ExeDir, "app.ico");
            var srcPng = iconIdx + 2 < args.Length ? args[iconIdx + 2] : null;
            if (srcPng is not null && File.Exists(srcPng)) IconMaker.WriteFromImage(srcPng, outPath);
            else IconMaker.Write(outPath);
            Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length / 1024} KB)" +
                              (srcPng is not null ? $" from {srcPng}" : " (generated)"));
            return 0;
        }

        // Dev diagnostic: print a decoded PNG text chunk. `--dump <file> <keyword>`
        var dumpIdx = Array.FindIndex(args, a => string.Equals(a, "--dump", StringComparison.OrdinalIgnoreCase));
        if (dumpIdx >= 0 && dumpIdx + 2 < args.Length)
        {
            Native.TryAttachParentConsole();
            var chunks = PngChunkReader.Read(args[dumpIdx + 1]);
            if (chunks is null) { Console.WriteLine("not a PNG"); return 2; }
            Console.WriteLine(chunks.Text.TryGetValue(args[dumpIdx + 2], out var v) ? v : "(keyword not found)");
            return 0;
        }

        // No CLI flag → launch the gallery (T10).
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// AC for T1: create library.db beside the exe, enable WAL, and run a trivial FTS5 query.
    /// Writes a human-readable result to selftest-result.txt and returns 0 on PASS.
    /// </summary>
    private static int SelfTest()
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }

        try
        {
            var dbPath = AppPaths.LibraryDbFile;
            W($"{AppInfo.Name} v{AppInfo.Version} — SQLite/FTS5 self-test");
            W($"library.db: {dbPath}");

            using var con = new SqliteConnection($"Data Source={dbPath}");
            con.Open();

            Exec(con, "PRAGMA journal_mode=WAL;");
            Exec(con, "PRAGMA synchronous=NORMAL;");

            var journalMode = Scalar(con, "PRAGMA journal_mode;")?.ToString() ?? "?";
            var sqliteVer = Scalar(con, "SELECT sqlite_version();")?.ToString() ?? "?";
            W($"journal_mode = {journalMode}");
            W($"sqlite_version = {sqliteVer}");

            // FTS5 round-trip — proves the bundled native lib self-extracted with FTS5 enabled.
            Exec(con, "DROP TABLE IF EXISTS fts_probe;");
            Exec(con, "CREATE VIRTUAL TABLE fts_probe USING fts5(body);");
            Exec(con, "INSERT INTO fts_probe(body) VALUES('the tag hag tames the ai image hoard');");
            var hits = Convert.ToInt64(Scalar(con, "SELECT count(*) FROM fts_probe WHERE fts_probe MATCH 'hoard';"));
            W($"FTS5 MATCH 'hoard' hits = {hits}");
            Exec(con, "DROP TABLE fts_probe;");

            var ok = string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase) && hits == 1;
            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResult(log);
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResult(log);
            return 2;
        }
    }

    /// <summary>
    /// T2 AC: schema + triggers keep image_tags / tag_freq / images_fts in sync; delete
    /// cascades and decrements tag_freq; RebuildMatrix() reproduces the counts.
    /// </summary>
    private static int DbSelfTest()
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        try
        {
            var dbPath = Path.Combine(AppPaths.ExeDir, "selftest-lib.db");
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);

            W($"{AppInfo.Name} v{AppInfo.Version} — LibraryDb (T2) self-test");
            using var db = new LibraryDb(dbPath);

            ImageRow Row(string abs, string prompt, string neg, params string[] tags) => new()
            {
                SourceRoot = "C:\\src", RelPath = abs, AbsPath = abs, FileName = abs, Ext = ".png",
                SizeBytes = 100, MtimeTicks = 1, Width = 512, Height = 768,
                MetaFormat = "a1111", MetaSource = "embedded",
                Prompt = prompt, Negative = neg, ScannedAt = "2026-06-16",
                Tags = tags.ToList()
            };

            db.Upsert(Row("A.png", "1girl, ancient forest temple", "blurry", "1girl", "forest", "ancient", "temple"));
            db.Upsert(Row("B.png", "1girl, red dress, city street", "lowres", "1girl", "red", "dress", "city"));

            Check("2 images indexed", db.ImageCount() == 2);
            Check("tag_freq '1girl' = 2", db.TagDf("1girl") == 2);
            Check("tag_freq 'forest' = 1", db.TagDf("forest") == 1);
            Check("FTS MATCH 'temple' = 1", db.FtsMatchCount("temple") == 1);
            Check("FTS MATCH '1girl' = 2", db.FtsMatchCount("1girl") == 2);

            // Re-upsert A with a changed tag set + prompt → tags swap, fts updates, still 2 images.
            db.Upsert(Row("A.png", "1girl, ancient forest at night", "blurry", "1girl", "forest", "ancient", "night"));
            Check("still 2 images after re-upsert", db.ImageCount() == 2);
            Check("'temple' removed (df gone)", db.TagDf("temple") == 0);
            Check("'night' added (df = 1)", db.TagDf("night") == 1);
            Check("FTS reflects update ('temple' gone)", db.FtsMatchCount("temple") == 0);
            Check("FTS reflects update ('night' = 1)", db.FtsMatchCount("night") == 1);

            // Remove B → cascade: its tags drop, tag_freq decrements, FTS row removed.
            db.Remove(db.FindIdByAbsPath("B.png")!.Value);
            Check("1 image after remove", db.ImageCount() == 1);
            Check("'1girl' df decremented to 1", db.TagDf("1girl") == 1);
            Check("'city' df gone after cascade", db.TagDf("city") == 0);
            Check("image_tags rows = 4 (only A)", db.TagRowCount() == 4);
            Check("FTS MATCH 'city' = 0", db.FtsMatchCount("city") == 0);

            // RebuildMatrix reproduces the same counts.
            db.RebuildMatrix();
            Check("after rebuild: '1girl' df = 1", db.TagDf("1girl") == 1);
            Check("after rebuild: distinct tags = 4", db.DistinctTagCount() == 4);

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-db-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-db-result.txt");
            return 2;
        }
    }

    /// <summary>
    /// T3 AC: PngChunkReader extracts text chunks + IHDR dims from real PNGs; A1111Parser
    /// parses any "parameters" chunk into positive/negative/steps/sampler/cfg/seed/size/model.
    /// Walks every PNG under <paramref name="dir"/> and prints what it found.
    /// </summary>
    private static int PngSelfTest(string dir)
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }

        W($"{AppInfo.Name} v{AppInfo.Version} — PNG reader + A1111 parser (T3) self-test");
        W($"scanning: {dir}");
        if (!Directory.Exists(dir)) { W("RESULT: FAIL (dir not found)"); WriteResultNamed(log, "selftest-png-result.txt"); return 2; }

        var pngs = Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories).OrderBy(p => p).ToList();
        W($"found {pngs.Count} PNG(s)");

        int read = 0, a1111Parsed = 0, exceptions = 0;
        foreach (var path in pngs)
        {
            var name = Path.GetFileName(path);
            try
            {
                var chunks = PngChunkReader.Read(path);
                if (chunks is null) { W($"  [skip] {name}: not a PNG"); continue; }
                read++;
                var keys = chunks.Text.Count > 0 ? string.Join(",", chunks.Text.Keys) : "(none)";
                W($"  {name}: {chunks.Width}x{chunks.Height}  chunks=[{keys}]");

                if (chunks.Text.TryGetValue("parameters", out var paramsText))
                {
                    var meta = A1111Parser.Parse(paramsText);
                    if (meta.Format == "a1111" && !string.IsNullOrWhiteSpace(meta.Prompt))
                    {
                        a1111Parsed++;
                        W($"      A1111 → prompt[{meta.Prompt.Length} ch] neg[{meta.Negative.Length} ch] " +
                          $"steps={meta.Steps} sampler={meta.Sampler} cfg={meta.Cfg} seed={meta.Seed} " +
                          $"size={meta.Width}x{meta.Height} model={meta.Model}");
                        W($"      positive: {Trunc(meta.Prompt, 90)}");
                    }
                }
            }
            catch (Exception ex) { exceptions++; W($"  [ERR] {name}: {ex.GetType().Name}: {ex.Message}"); }
        }

        W($"summary: read {read}/{pngs.Count}, a1111 parsed {a1111Parsed}, exceptions {exceptions}");
        var ok = exceptions == 0 && read == pngs.Count && (a1111Parsed > 0 || pngs.Count == 0);
        W(ok ? "RESULT: PASS" : "RESULT: FAIL");
        WriteResultNamed(log, "selftest-png-result.txt");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// T4 AC: ImageMetadataReader resolves embedded → EXIF → sidecar. Runs over the
    /// exif-jpeg-webp fixtures and a synthesized metadata-free image + .txt sidecar.
    /// </summary>
    private static int MetaSelfTest(string fixturesRoot)
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — ImageMetadataReader (T4) self-test");

            // 1a) Report what the real jpg/jpeg/webp fixtures contain (informational — the
            //     supplied novaOrange jpg/jpeg/webp are metadata-free re-encodes; no EXIF).
            var exifDir = Path.Combine(fixturesRoot, "exif-jpeg-webp");
            int realWithMeta = 0, realScanned = 0;
            if (Directory.Exists(exifDir))
                foreach (var f in Directory.EnumerateFiles(exifDir).OrderBy(x => x))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is not (".jpg" or ".jpeg" or ".webp" or ".png")) continue;
                    realScanned++;
                    var m = ImageMetadataReader.Read(f);
                    if (m.Format != "none") realWithMeta++;
                    W($"  {Path.GetFileName(f)}: format={m.Format} source={m.Source} {m.Width}x{m.Height} prompt[{m.Prompt.Length}]");
                }
            W($"  (real jpg/webp fixtures carrying embedded metadata: {realWithMeta}/{realScanned})");

            // 1b) EXIF round-trip — synthesize a JPEG with an A1111 UserComment, read it back.
            var exTmp = Path.Combine(AppPaths.ExeDir, "selftest-exif");
            Directory.CreateDirectory(exTmp);
            var jpgPath = Path.Combine(exTmp, "probe.jpg");
            const string a1111 = "a knight in a misty forest, cinematic\nNegative prompt: blurry, lowres\n" +
                                  "Steps: 24, Sampler: Euler a, CFG scale: 5.0, Seed: 42, Size: 768x1152, Model: testModel_v1";
            using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(8, 8))
            {
                var prof = new Exif.ExifProfile();
                prof.SetValue(Exif.ExifTag.UserComment, new Exif.EncodedString(Exif.EncodedString.CharacterCode.Unicode, a1111));
                img.Metadata.ExifProfile = prof;
                SixLabors.ImageSharp.ImageExtensions.SaveAsJpeg(img, jpgPath);
            }
            var ex = ImageMetadataReader.Read(jpgPath);
            W($"  exif round-trip: format={ex.Format} source={ex.Source} prompt[{ex.Prompt.Length}] steps={ex.Steps} sampler={ex.Sampler} seed={ex.Seed}");
            Check("exif: format=a1111", ex.Format == "a1111");
            Check("exif: source=exif", ex.Source == "exif");
            Check("exif: positive parsed", ex.Prompt.Contains("knight"));
            Check("exif: steps=24", ex.Steps == 24);
            Check("exif: seed=42", ex.Seed == 42);

            // 2) Sidecar path: synthesize a metadata-free PNG + matching .txt.
            var tmp = Path.Combine(AppPaths.ExeDir, "selftest-sidecar");
            Directory.CreateDirectory(tmp);
            var imgPath = Path.Combine(tmp, "probe.png");
            var txtPath = Path.Combine(tmp, "probe.txt");
            using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(8, 8))
                SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, imgPath);
            File.WriteAllText(txtPath,
                "a serene mountain lake at sunrise, pine forest\nNegative prompt: blurry, lowres\n" +
                "Steps: 28, Sampler: DPM++ 2M Karras, CFG scale: 6.5, Seed: 1234567890, Size: 832x1216, Model: novaOrangeAM_v20\n");

            var side = ImageMetadataReader.Read(imgPath);
            W($"  sidecar probe: format={side.Format} source={side.Source} prompt[{side.Prompt.Length}] " +
              $"steps={side.Steps} sampler={side.Sampler} model={side.Model}");
            Check("sidecar: format=a1111", side.Format == "a1111");
            Check("sidecar: source=sidecar-txt", side.Source == "sidecar-txt");
            Check("sidecar: positive parsed", side.Prompt.Contains("mountain lake"));
            Check("sidecar: negative parsed", side.Negative.Contains("blurry"));
            Check("sidecar: steps=28", side.Steps == 28);
            Check("sidecar: cfg=6.5", side.Cfg == 6.5);

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-meta-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-meta-result.txt");
            return 2;
        }
    }

    /// <summary>
    /// T5 AC: ComfyGraphParser extracts positive/negative + sampler params from real ComfyUI
    /// prompt graphs (best-effort). Runs the parser directly on the "prompt" chunk of every
    /// ComfyUI PNG so it's exercised even on files that also carry an A1111 "parameters" chunk.
    /// </summary>
    private static int ComfySelfTest(string dir)
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }

        W($"{AppInfo.Name} v{AppInfo.Version} — ComfyGraphParser (T5) self-test");
        if (!Directory.Exists(dir)) { W("RESULT: FAIL (dir not found)"); WriteResultNamed(log, "selftest-comfy-result.txt"); return 2; }

        var pngs = Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories).OrderBy(p => p).ToList();
        int withGraph = 0, gotPositive = 0, gotSampler = 0, exceptions = 0;

        foreach (var path in pngs)
        {
            var name = Path.GetFileName(path);
            try
            {
                var chunks = PngChunkReader.Read(path);
                if (chunks is null || !chunks.Text.TryGetValue("prompt", out var pj) || string.IsNullOrWhiteSpace(pj)) continue;
                withGraph++;
                var m = ComfyGraphParser.Parse(pj, chunks.Text.GetValueOrDefault("workflow"));
                if (!string.IsNullOrWhiteSpace(m.Prompt)) gotPositive++;
                if (m.Steps > 0) gotSampler++;
                W($"  {name}: pos[{m.Prompt.Length}] neg[{m.Negative.Length}] steps={m.Steps} cfg={m.Cfg} seed={m.Seed} model={m.Model}");
                W($"      positive: {Trunc(m.Prompt.Replace("\n", " "), 90)}");
                W($"      negative: {Trunc(m.Negative.Replace("\n", " "), 70)}");
            }
            catch (Exception ex) { exceptions++; W($"  [ERR] {name}: {ex.GetType().Name}: {ex.Message}"); }
        }

        W($"summary: comfy graphs {withGraph}, positive extracted {gotPositive}, sampler params {gotSampler}, exceptions {exceptions}");
        // Best-effort: PASS = no exceptions AND a positive extracted for every graph found.
        var ok = exceptions == 0 && withGraph > 0 && gotPositive == withGraph;
        W(ok ? "RESULT: PASS" : "RESULT: FAIL");
        WriteResultNamed(log, "selftest-comfy-result.txt");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// T6–T9 end-to-end: scan a real folder → metadata + tags indexed → search (comma-AND,
    /// quoted phrase, mixed) → autocomplete → incremental re-scan → removed-file prune.
    /// </summary>
    private static int ScanSelfTest(string fixturesRoot)
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — scan→search→autocomplete (T6–T9) self-test");
            fixturesRoot = Path.GetFullPath(fixturesRoot);

            string[] exts = { ".png", ".jpg", ".jpeg", ".webp" };
            int expected = Directory.EnumerateFiles(fixturesRoot, "*", SearchOption.AllDirectories)
                .Count(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()));
            W($"  expected image files under fixtures: {expected}");

            // Part A — full scan + query + autocomplete.
            var dbPath = FreshDb("selftest-scan.db");
            using (var db = new LibraryDb(dbPath))
            {
                var scanner = new LocalScanner(db);
                var r = scanner.Scan(new[] { fixturesRoot });
                W($"  scan: added={r.Added} updated={r.Updated} unchanged={r.Unchanged} removed={r.Removed} failed={r.Failed}");
                Check($"indexed all images ({expected})", db.ImageCount() == expected);
                Check("scan added == expected on first pass", r.Added == expected && r.Failed == 0);
                Check("tag matrix populated", db.DistinctTagCount() > 0);
                W($"  distinct tags: {db.DistinctTagCount()}, image_tags rows: {db.TagRowCount()}");

                int Q(string raw) => db.Query(SearchParser.Parse(raw), 0, 500, includeArchived: true).Total;

                int g = Q("1girl");
                int gSolo = Q("1girl, solo");
                int phrase = Q("\"best quality\"");           // stopworded as a tag, but matchable as a phrase
                int mixed = Q("\"score_7\", solo");           // phrase AND tag
                W($"  query counts: '1girl'={g}  '1girl, solo'={gSolo}  \"best quality\"={phrase}  mixed=\"score_7\"+solo={mixed}");
                Check("token search '1girl' > 0", g > 0);
                Check("comma-AND is no looser than single token", gSolo <= g && gSolo > 0);
                Check("quoted-phrase finds stopworded text ('best quality')", phrase > 0);
                Check("mixed phrase+tag > 0", mixed > 0);

                var ac1 = db.TopTags("1g", 8).Select(t => t.Token).ToList();
                var acSo = db.TopTags("so", 8).Select(t => t.Token).ToList();
                W($"  autocomplete '1g' → [{string.Join(", ", ac1)}]");
                W($"  autocomplete 'so' → [{string.Join(", ", acSo)}]");
                Check("autocomplete '1g' yields '1girl'", ac1.Contains("1girl"));
                Check("autocomplete 'so' yields 'solo'", acSo.Contains("solo"));

                // Untagged-only filter: images the parser found no tags for (the metadata-free jpg/jpeg/webp).
                var untagged = db.Query(SearchParser.Parse(""), 0, 1000, includeArchived: true, untaggedOnly: true).Total;
                W($"  untagged-only total = {untagged} (expected the metadata-free images)");
                Check("untagged filter > 0 and < all", untagged >= 3 && untagged < expected);

                // Incremental re-scan: nothing changed → all unchanged, none added.
                var r2 = scanner.Scan(new[] { fixturesRoot });
                W($"  re-scan: added={r2.Added} unchanged={r2.Unchanged} removed={r2.Removed}");
                Check("incremental re-scan skips everything", r2.Unchanged == expected && r2.Added == 0 && r2.Removed == 0);
            }

            // Part B — removed-file prune (isolated temp dir so we can delete a file).
            var pruneDir = Path.Combine(AppPaths.ExeDir, "selftest-scan-prune");
            if (Directory.Exists(pruneDir)) Directory.Delete(pruneDir, true);
            Directory.CreateDirectory(pruneDir);
            var sample = Directory.EnumerateFiles(fixturesRoot, "*.png", SearchOption.AllDirectories).First();
            var copied = Path.Combine(pruneDir, Path.GetFileName(sample));
            File.Copy(sample, copied, true);

            var pruneDbPath = FreshDb("selftest-scan-prune.db");
            using (var db = new LibraryDb(pruneDbPath))
            {
                var scanner = new LocalScanner(db);
                var a = scanner.Scan(new[] { pruneDir });
                Check("prune setup: 1 file indexed", a.Added == 1 && db.ImageCount() == 1);
                File.Delete(copied);
                var b = scanner.Scan(new[] { pruneDir });
                W($"  after delete: removed={b.Removed} imageCount={db.ImageCount()}");
                Check("deleted file pruned next scan", b.Removed == 1 && db.ImageCount() == 0);
            }

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-scan-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-scan-result.txt");
            return 2;
        }
    }

    /// <summary>
    /// T10 logic check (headless — the window itself needs a display): the embedded gallery
    /// template loads, and GalleryBridge produces correct page/autocomplete JSON against a real scan.
    /// </summary>
    private static int UiSelfTest(string fixturesRoot)
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — gallery bridge + template (T10) self-test");

            // Embedded template present + version substituted.
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("taghag.template.html", StringComparison.OrdinalIgnoreCase));
            string html = "";
            if (resName is not null) { using var rd = new StreamReader(asm.GetManifestResourceStream(resName)!); html = rd.ReadToEnd(); }
            Check("embedded gallery template found", resName is not null);
            Check("template has the search input + bridge", html.Contains("id=\"search\"") && html.Contains("chrome.webview"));

            // Scan fixtures, then drive the bridge like the WebView2 host would.
            var dbPath = FreshDb("selftest-ui.db");
            using var db = new LibraryDb(dbPath);
            new LocalScanner(db).Scan(new[] { Path.GetFullPath(fixturesRoot) });
            var bridge = new GalleryBridge(db, r => $"https://full.local/{r.Id}");

            string? pageAll = bridge.Handle("{\"type\":\"query\",\"raw\":\"\",\"page\":0,\"size\":60}");
            string? pageGirl = bridge.Handle("{\"type\":\"query\",\"raw\":\"1girl\",\"page\":0,\"size\":60}");
            string? ac = bridge.Handle("{\"type\":\"ac\",\"prefix\":\"so\"}");
            W($"  page(all): {Trunc(pageAll ?? "null", 120)}");
            W($"  ac(so):    {Trunc(ac ?? "null", 120)}");

            using var dAll = System.Text.Json.JsonDocument.Parse(pageAll!);
            using var dGirl = System.Text.Json.JsonDocument.Parse(pageGirl!);
            using var dAc = System.Text.Json.JsonDocument.Parse(ac!);
            int totalAll = dAll.RootElement.GetProperty("total").GetInt32();
            int totalGirl = dGirl.RootElement.GetProperty("total").GetInt32();
            int itemCount = dAll.RootElement.GetProperty("items").GetArrayLength();
            var firstUrl = dAll.RootElement.GetProperty("items")[0].GetProperty("url").GetString() ?? "";
            var acTokens = dAc.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("token").GetString()).ToList();

            Check("page reply type=page", dAll.RootElement.GetProperty("type").GetString() == "page");
            Check("page total = 13", totalAll == 13);
            Check("page items populated", itemCount > 0);
            Check("item url is id-based (full.local)", firstUrl.StartsWith("https://full.local/"));
            Check("item carries format + prompt", dAll.RootElement.GetProperty("items")[0].TryGetProperty("format", out _));
            Check("query '1girl' total = 5", totalGirl == 5);
            Check("autocomplete 'so' includes 'solo'", acTokens.Contains("solo"));

            // LoRA extraction for the inspector panel (T-inspector).
            var loras = GalleryBridge.ExtractLoras("1girl, <lora:foo:0.8>, detailed, <lyco:bar>, <lora:baz:1.2>");
            Check("ExtractLoras finds 3 loras", loras.Length == 3);
            Check("ExtractLoras parses name+weight", loras[0].Name == "foo" && loras[0].Weight == "0.8" && loras[1].Name == "bar" && loras[1].Weight is null);

            // Lightbox inspect path (T11).
            var firstId = dAll.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64();
            var inspect = bridge.Handle($"{{\"type\":\"inspect\",\"id\":{firstId}}}");
            using var dIns = System.Text.Json.JsonDocument.Parse(inspect!);
            Check("inspect reply type=inspect", dIns.RootElement.GetProperty("type").GetString() == "inspect");
            Check("inspect carries url + positive + format",
                (dIns.RootElement.GetProperty("url").GetString() ?? "").StartsWith("https://full.local/")
                && dIns.RootElement.TryGetProperty("positive", out _)
                && dIns.RootElement.TryGetProperty("format", out _));
            W($"  inspect(id={firstId}): {Trunc(inspect ?? "null", 120)}");

            // Thumbnail cache (T15a): generate a 512px WebP for the first image.
            var thumbsDir = Path.Combine(AppPaths.ExeDir, "selftest-thumbs");
            if (Directory.Exists(thumbsDir)) Directory.Delete(thumbsDir, true);
            using (var thumbs = new ThumbnailService(dbPath, thumbsDir))
            {
                var tp = thumbs.GetOrCreate(firstId);
                var okThumb = tp is not null && File.Exists(tp) && new FileInfo(tp).Length > 0;
                Check("thumbnail generated (512px webp)", okThumb);
                if (okThumb) W($"  thumb: {Path.GetFileName(tp)} ({new FileInfo(tp!).Length / 1024} KB)");
                var tp2 = thumbs.GetOrCreate(firstId); // cached path returned, no regen
                Check("thumbnail cache hit returns same path", tp2 == tp);
            }

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-ui-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-ui-result.txt");
            return 2;
        }
    }

    /// <summary>
    /// T13 file-op logic (headless): Copy (+collision suffix), Move (row removed), Archive→Bog
    /// (row archived, hidden from default query), and Recycle delete. Mirrors MainForm's op runner
    /// on real temp files so the destructive paths are proven before any button is clicked.
    /// </summary>
    private static int FileOpsSelfTest(string fixturesRoot)
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — FileOps (T13) self-test");
            var srcPng = Directory.EnumerateFiles(Path.GetFullPath(fixturesRoot), "*.png", SearchOption.AllDirectories).First();

            var work = Path.Combine(AppPaths.ExeDir, "selftest-fileops");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            var srcDir = Path.Combine(work, "src");
            var export = Path.Combine(work, "export");
            Directory.CreateDirectory(srcDir); Directory.CreateDirectory(export);
            foreach (var n in new[] { "a.png", "b.png", "c.png" }) File.Copy(srcPng, Path.Combine(srcDir, n));

            var dbPath = FreshDb("selftest-fileops.db");
            using var db = new LibraryDb(dbPath);
            new LocalScanner(db).Scan(new[] { srcDir });
            long Id(string n) => db.FindIdByAbsPath(Path.Combine(srcDir, n))!.Value;
            long aId = Id("a.png"), bId = Id("b.png"), cId = Id("c.png");
            Check("scanned 3 files", db.ImageCount() == 3);

            // Copy + collision suffix (original stays in library).
            File.Copy(db.GetById(aId)!.AbsPath, FileOps.UniqueDestination(export, "a.png"));
            var dup = FileOps.UniqueDestination(export, "a.png");
            File.Copy(db.GetById(aId)!.AbsPath, dup);
            Check("copy keeps original (still 3 in library)", db.ImageCount() == 3);
            Check("collision suffix → 'a (2).png'", Path.GetFileName(dup) == "a (2).png" && File.Exists(dup));

            // Move (file leaves library).
            var bAbs = db.GetById(bId)!.AbsPath;
            var bDest = FileOps.UniqueDestination(export, "b.png");
            FileOps.Move(bAbs, bDest); db.Remove(bId);
            Check("move: file relocated + row removed", !File.Exists(bAbs) && File.Exists(bDest) && db.ImageCount() == 2);

            // Archive → the Bog (file moved, row archived + hidden by default).
            var arch = Path.Combine(export, "-Archive");
            Directory.CreateDirectory(arch);
            var cDest = FileOps.UniqueDestination(arch, "c.png");
            FileOps.Move(db.GetById(cId)!.AbsPath, cDest); db.SetArchived(cId, cDest);
            Check("archive: row hidden from default query", db.ImageCount(includeArchived: false) == 1);
            Check("archive: row retained when including Bog", db.ImageCount(includeArchived: true) == 2);
            Check("archive: row points at -Archive + archived flag", db.GetById(cId)!.Archived && db.GetById(cId)!.AbsPath == cDest);
            Check("query hides archived by default", db.Query(SearchParser.Parse(""), 0, 100, false).Total == 1);
            Check("query shows archived with Bog on", db.Query(SearchParser.Parse(""), 0, 100, true).Total == 2);

            // NOTE: recycle-delete is intentionally NOT exercised here — it would drop real files
            // into the user's Recycle Bin on every test run. It's verified by the user in-app
            // (delete an image → confirm it appears in the Recycle Bin under its real name).

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-fileops-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-fileops-result.txt");
            return 2;
        }
    }

    /// <summary>
    /// T14 AC: metadata-preserving downsample. Synthesizes large/small PNG + JPEG carrying embedded
    /// generation metadata, then verifies large images shrink to ≤ maxDim with aspect preserved,
    /// already-small images are skipped/untouched, the embedded A1111 parameters, ComfyUI prompt
    /// graph, and JPEG EXIF all survive the re-encode, and in-place mode replaces the original.
    /// </summary>
    private static int OptimizeSelfTest()
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — ImageOptimizer (T14) self-test");
            const int maxDim = ImageOptimizer.DefaultMaxDim; // 1024
            var work = Path.Combine(AppPaths.ExeDir, "selftest-optimize");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            byte[] PngBytes(int w, int h)
            {
                using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
                using var ms = new MemoryStream();
                SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, ms);
                return ms.ToArray();
            }
            (int w, int h) Dim(string p) { var i = SixLabors.ImageSharp.Image.Identify(p); return (i.Width, i.Height); }

            const string a1111 = "an enchanted forest cottage, golden hour, highly detailed\n" +
                                 "Negative prompt: blurry, lowres\n" +
                                 "Steps: 30, Sampler: DPM++ 2M Karras, CFG scale: 6.0, Seed: 987654321, Size: 2000x1200, Model: testModel_v2";

            // 1) Large A1111 PNG → copy-downsample preserves the parameters chunk.
            var bigPng = Path.Combine(work, "big_a1111.png");
            File.WriteAllBytes(bigPng, PngWriter.WithTextChunks(PngBytes(2000, 1200), new[] { ("parameters", a1111) }));
            var bigCopy = Path.Combine(work, "big_a1111_copy.png");
            var o1 = ImageOptimizer.DownsampleToCopy(bigPng, bigCopy, maxDim);
            var (cw, ch) = Dim(bigCopy);
            var m1 = ImageMetadataReader.Read(bigCopy);
            W($"  big PNG copy: outcome={o1} dims={cw}x{ch} format={m1.Format} prompt[{m1.Prompt.Length}]");
            Check("big PNG → Resized", o1 == OptimizeOutcome.Resized);
            Check("downsampled within 1024", cw <= maxDim && ch <= maxDim);
            Check("longest edge shrunk to 1024", Math.Max(cw, ch) == maxDim);
            Check("aspect preserved (~1.667)", Math.Abs((double)cw / ch - 2000.0 / 1200.0) < 0.02);
            Check("A1111 metadata survived", m1.Format == "a1111" && m1.Prompt.Contains("enchanted forest"));

            // 2) Already-small PNG → skipped (copied unchanged).
            var smallPng = Path.Combine(work, "small.png");
            File.WriteAllBytes(smallPng, PngWriter.WithTextChunks(PngBytes(512, 512), new[] { ("parameters", a1111) }));
            var smallCopy = Path.Combine(work, "small_copy.png");
            var o2 = ImageOptimizer.DownsampleToCopy(smallPng, smallCopy, maxDim);
            var (sw, sh) = Dim(smallCopy);
            Check("small PNG → SkippedSmall", o2 == OptimizeOutcome.SkippedSmall);
            Check("small PNG dims unchanged (512x512)", sw == 512 && sh == 512);

            // 3) ComfyUI prompt graph chunk survives a downsample.
            const string comfyJson = "{\"3\":{\"class_type\":\"KSampler\",\"inputs\":{\"seed\":42,\"steps\":20}}}";
            var comfyPng = Path.Combine(work, "comfy.png");
            File.WriteAllBytes(comfyPng, PngWriter.WithTextChunks(PngBytes(1800, 1800), new[] { ("prompt", comfyJson) }));
            var comfyCopy = Path.Combine(work, "comfy_copy.png");
            ImageOptimizer.DownsampleToCopy(comfyPng, comfyCopy, maxDim);
            var rc = PngChunkReader.Read(comfyCopy);
            var (kw, kh) = Dim(comfyCopy);
            Check("comfy downsampled within 1024", kw <= maxDim && kh <= maxDim);
            Check("comfy 'prompt' chunk preserved verbatim",
                rc is not null && rc.Text.TryGetValue("prompt", out var pj) && pj == comfyJson);

            // 4) In-place overwrite (large) replaces the original + preserves metadata.
            var inPlace = Path.Combine(work, "inplace.png");
            File.WriteAllBytes(inPlace, PngWriter.WithTextChunks(PngBytes(2400, 1600), new[] { ("parameters", a1111) }));
            long beforeLen = new FileInfo(inPlace).Length;
            var o4 = ImageOptimizer.DownsampleInPlace(inPlace, maxDim);
            var (iw, ih) = Dim(inPlace);
            var m4 = ImageMetadataReader.Read(inPlace);
            W($"  in-place: outcome={o4} dims={iw}x{ih} bytes {beforeLen}->{new FileInfo(inPlace).Length} format={m4.Format}");
            Check("in-place large → Resized", o4 == OptimizeOutcome.Resized);
            Check("in-place dims within 1024", iw <= maxDim && ih <= maxDim);
            Check("in-place metadata survived", m4.Format == "a1111" && m4.Prompt.Contains("enchanted forest"));

            // 5) In-place on an already-small image leaves it byte-for-byte untouched.
            var smallInPlace = Path.Combine(work, "small_inplace.png");
            var origBytes = PngWriter.WithTextChunks(PngBytes(640, 480), new[] { ("parameters", a1111) });
            File.WriteAllBytes(smallInPlace, origBytes);
            var o5 = ImageOptimizer.DownsampleInPlace(smallInPlace, maxDim);
            Check("in-place small → SkippedSmall", o5 == OptimizeOutcome.SkippedSmall);
            Check("in-place small untouched (bytes identical)", File.ReadAllBytes(smallInPlace).SequenceEqual(origBytes));

            // 6) JPEG EXIF survives downsample (copy).
            var bigJpg = Path.Combine(work, "big_exif.jpg");
            using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(2000, 1500))
            {
                var prof = new Exif.ExifProfile();
                prof.SetValue(Exif.ExifTag.UserComment, new Exif.EncodedString(Exif.EncodedString.CharacterCode.Unicode, a1111));
                img.Metadata.ExifProfile = prof;
                SixLabors.ImageSharp.ImageExtensions.SaveAsJpeg(img, bigJpg);
            }
            var jpgCopy = Path.Combine(work, "big_exif_copy.jpg");
            var o6 = ImageOptimizer.DownsampleToCopy(bigJpg, jpgCopy, maxDim);
            var (jw, jh) = Dim(jpgCopy);
            var m6 = ImageMetadataReader.Read(jpgCopy);
            W($"  jpeg copy: outcome={o6} dims={jw}x{jh} format={m6.Format} source={m6.Source} steps={m6.Steps}");
            Check("jpeg → Resized", o6 == OptimizeOutcome.Resized);
            Check("jpeg dims within 1024", jw <= maxDim && jh <= maxDim);
            Check("jpeg EXIF metadata survived", m6.Format == "a1111" && m6.Source == "exif" && m6.Prompt.Contains("enchanted forest"));

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-optimize-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-optimize-result.txt");
            return 2;
        }
    }

    /// <summary>
    /// T14 AC (headless): downsample preserves generation metadata. Copy mode + in-place mode,
    /// for both A1111 (PNG text) and ComfyUI (graph) — re-read the output and confirm prompt
    /// survives and dimensions shrank; verify shrink-only leaves small images untouched.
    /// </summary>
    private static int OptimizeSelfTest(string fixturesRoot)
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — ImageOptimizer (T14) self-test");
            string FirstPng(string sub) => Directory.EnumerateFiles(Path.Combine(fixturesRoot, sub), "*.png").OrderBy(x => x).First();
            var a1111Src = FirstPng("a1111-png");
            var comfySrc = FirstPng("comfyui-png");

            var work = Path.Combine(AppPaths.ExeDir, "selftest-optimize");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            string Tmp(string n) => Path.Combine(work, n);

            const int MAX = 512;

            // Copy mode — A1111: resized + dims shrink + prompt preserved.
            var aOut = Tmp("a_small.png");
            var oa = ImageOptimizer.DownsampleToCopy(a1111Src, aOut, MAX);
            var (aw, ah) = ImageOptimizer.ReadDimensions(aOut);
            var am = ImageMetadataReader.Read(aOut);
            Check("a1111 copy: resized", oa == OptimizeOutcome.Resized);
            Check("a1111 copy: within 512", Math.Max(aw, ah) <= MAX && aw > 0);
            Check("a1111 copy: prompt preserved", am.Format == "a1111" && am.Prompt.Length > 0);
            W($"      a1111 → {aw}x{ah}, format={am.Format}, prompt[{am.Prompt.Length}]");

            // Copy mode — ComfyUI: graph chunk preserved.
            var cOut = Tmp("c_small.png");
            var oc = ImageOptimizer.DownsampleToCopy(comfySrc, cOut, MAX);
            var (cw, ch) = ImageOptimizer.ReadDimensions(cOut);
            var cm = ImageMetadataReader.Read(cOut);
            Check("comfy copy: resized", oc == OptimizeOutcome.Resized);
            Check("comfy copy: within 512", Math.Max(cw, ch) <= MAX && cw > 0);
            Check("comfy copy: graph preserved", cm.Format == "comfyui" && (cm.Prompt.Length > 0 || (cm.RawJson?.Length ?? 0) > 0));
            W($"      comfy → {cw}x{ch}, format={cm.Format}, prompt[{cm.Prompt.Length}] raw[{cm.RawJson?.Length ?? 0}]");

            // Shrink-only: huge limit → skipped, file present + metadata intact.
            var aBig = Tmp("a_big.png");
            var ob = ImageOptimizer.DownsampleToCopy(a1111Src, aBig, 8192);
            Check("shrink-only: skipped when already small", ob == OptimizeOutcome.SkippedSmall && File.Exists(aBig));
            Check("shrink-only: verbatim copy keeps prompt", ImageMetadataReader.Read(aBig).Prompt.Length > 0);

            // In-place: overwrite, dims shrink, prompt preserved, original was larger.
            var ip = Tmp("ip.png");
            File.Copy(a1111Src, ip);
            var (ow0, oh0) = ImageOptimizer.ReadDimensions(ip);
            var oi = ImageOptimizer.DownsampleInPlace(ip, MAX);
            var (iw, ih) = ImageOptimizer.ReadDimensions(ip);
            var im = ImageMetadataReader.Read(ip);
            Check("in-place: resized", oi == OptimizeOutcome.Resized);
            Check("in-place: shrank below original", Math.Max(iw, ih) <= MAX && Math.Max(iw, ih) < Math.Max(ow0, oh0));
            Check("in-place: prompt preserved", im.Format == "a1111" && im.Prompt.Length > 0);
            W($"      in-place {ow0}x{oh0} → {iw}x{ih}, prompt[{im.Prompt.Length}]");

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-optimize-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-optimize-result.txt");
            return 2;
        }
    }

    /// <summary>
    /// T16 (headless): DPAPI round-trip for the API key (plaintext never serialized), settings
    /// field persistence, and RemoveBySourceRoot pruning a removed folder.
    /// </summary>
    private static int SettingsSelfTest(string fixturesRoot)
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — Settings (T16) self-test");
            const string secret = "sk-civitai-SECRET-9XYZ";

            var s = new AppSettings { ApiKey = secret };
            Check("api key encrypted (not plaintext)", !string.IsNullOrEmpty(s.ApiKeyEncrypted) && !s.ApiKeyEncrypted!.Contains("SECRET"));
            Check("api key getter round-trips", s.ApiKey == secret);

            var json = System.Text.Json.JsonSerializer.Serialize(s);
            Check("serialized JSON has encrypted blob", json.Contains("ApiKeyEncrypted"));
            Check("serialized JSON has NO plaintext key", !json.Contains(secret));
            var s2 = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
            Check("api key decrypts from persisted blob", s2.ApiKey == secret);

            var empty = new AppSettings();
            Check("empty key → null blob + empty plaintext", empty.ApiKeyEncrypted is null && empty.ApiKey == "");

            var s3 = new AppSettings { SourceRoots = new() { @"C:\a", @"C:\b" }, ExportDir = @"C:\exp", MaxDim = 2048 };
            var s4 = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(s3))!;
            Check("settings fields persist", s4.SourceRoots.Count == 2 && s4.ExportDir == @"C:\exp" && s4.MaxDim == 2048);

            // RemoveBySourceRoot prunes a removed folder.
            var work = Path.Combine(AppPaths.ExeDir, "selftest-settings");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            var src = Directory.EnumerateFiles(Path.GetFullPath(fixturesRoot), "*.png", SearchOption.AllDirectories).First();
            File.Copy(src, Path.Combine(work, "x.png"));
            var dbPath = FreshDb("selftest-settings.db");
            using (var db = new LibraryDb(dbPath))
            {
                new LocalScanner(db).Scan(new[] { work });
                Check("scanned 1 image", db.ImageCount() == 1);
                var n = db.RemoveBySourceRoot(work);
                Check("RemoveBySourceRoot prunes the folder", n == 1 && db.ImageCount() == 0);
            }

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-settings-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-settings-result.txt");
            return 2;
        }
    }

    /// <summary>
    /// T17 (headless, offline pieces): the harvest's JPEG→PNG metadata embed round-trips through the
    /// reader (so harvested images index into the library), and StateStore dedupe persists correctly.
    /// The live API harvest is verified by the user in-app (dry run first).
    /// </summary>
    private static int HarvestSelfTest(string fixturesRoot)
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — Civitai harvest offline pieces (T17) self-test");
            var work = Path.Combine(AppPaths.ExeDir, "selftest-harvest");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            // 1) TranscodeAndEmbed: JPEG + A1111 params → PNG → re-read prompt.
            var jpg = Directory.EnumerateFiles(Path.Combine(fixturesRoot, "exif-jpeg-webp"), "*.jpg").First();
            var rec = new ImageRecord { id = 1, prompt = "harvest probe, neon city, rain", negativePrompt = "blurry",
                steps = 22, sampler = "Euler a", cfgScale = 6.0, seed = 7, width = 64, height = 64, model = "probeModel_v1" };
            var png = PngWriter.TranscodeAndEmbed(File.ReadAllBytes(jpg), A1111.BuildParams(rec));
            var outPng = Path.Combine(work, "embedded.png");
            File.WriteAllBytes(outPng, png);
            var m = ImageMetadataReader.Read(outPng);
            Check("embed: output is a PNG with a1111 metadata", m.Format == "a1111");
            Check("embed: positive prompt round-trips", m.Prompt.Contains("neon city"));
            Check("embed: params round-trip (steps/model)", m.Steps == 22 && m.Model == "probeModel_v1");
            W($"      embedded → format={m.Format} prompt[{m.Prompt.Length}] steps={m.Steps} model={m.Model}");

            // 2) StateStore dedupe round-trip.
            var stateFile = Path.Combine(work, "state.json");
            var st = new HarvestState();
            st.Harvested.Add(100); st.Harvested.Add(101);
            st.Skipped.Add(200);
            st.Rejected.Add(300); st.Skipped.Add(300); // rejected folds into skipped
            StateStore.Save(stateFile, st);
            var st2 = StateStore.Load(stateFile, Path.Combine(work, "nodataset.jsonl"), _ => { });
            Check("state: harvested persists", st2.Harvested.SetEquals(new[] { 100, 101 }));
            Check("state: rejected persists + folds into skipped", st2.Rejected.Contains(300) && st2.Seen(300));
            Check("state: Seen() true for harvested + skipped", st2.Seen(100) && st2.Seen(200) && !st2.Seen(999));

            // 3) A1111.BuildParams shape.
            var p = A1111.BuildParams(rec);
            Check("BuildParams has Negative + Steps lines", p.Contains("Negative prompt:") && p.Contains("Steps: 22"));

            // 4) Browse Civitai (T17b): thumbnail URL builder + embedded browse template.
            var thumb = CivitaiClient.ThumbUrl("https://image.civitai.com/abc/12345678-1234-1234-1234-123456789012/width=1024/image.jpeg", 450);
            Check("ThumbUrl builds a width-capped CDN url", thumb.Contains("/width=450/") && thumb.Contains("12345678-1234-1234-1234-123456789012"));
            var hasBrowse = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .Any(n => n.EndsWith("browse.template.html", StringComparison.OrdinalIgnoreCase));
            Check("browse.template.html is embedded", hasBrowse);

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-harvest-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-harvest-result.txt");
            return 2;
        }
    }

    /// <summary>
    /// T17c (headless): the MCP react JSON-RPC body has the exact "params" key (no serializer
    /// ambiguity) and correct shape, and an invalid reaction is rejected before any network call.
    /// </summary>
    private static int ReactSelfTest()
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — Civitai react payload (T17c) self-test");
            var p = CivitaiClient.BuildReactPayload(987654, "Heart");
            W($"  payload: {p}");
            Check("key is \"params\" (not @params)", p.Contains("\"params\"") && !p.Contains("@params"));

            using var doc = System.Text.Json.JsonDocument.Parse(p);
            var root = doc.RootElement;
            var prm = root.GetProperty("params");
            var argsEl = prm.GetProperty("arguments");
            Check("method = tools/call", root.GetProperty("method").GetString() == "tools/call");
            Check("params.name = react", prm.GetProperty("name").GetString() == "react");
            Check("arguments.entityType = image", argsEl.GetProperty("entityType").GetString() == "image");
            Check("arguments.entityId = 987654", argsEl.GetProperty("entityId").GetInt32() == 987654);
            Check("arguments.reaction = Heart", argsEl.GetProperty("reaction").GetString() == "Heart");

            // Invalid reaction is rejected before any network call (ArgumentException, no HTTP).
            bool threw = false;
            try { using var c = new CivitaiClient("dummy-key", _ => { }); c.ReactAsync(1, "Bogus").GetAwaiter().GetResult(); }
            catch (ArgumentException) { threw = true; }
            catch (Exception ex) { W($"  (unexpected exception type: {ex.GetType().Name})"); }
            Check("invalid reaction rejected before network", threw);

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-react-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-react-result.txt");
            return 2;
        }
    }

    /// <summary>
    /// Find Duplicates (v1.1): dHash basics on synthesized images (identical copy → distance 0,
    /// mirrored image → far), the scan→backfill→exact-group path, and the grouping engine's
    /// near-duplicate banding with controlled hashes (decoupled from image-synthesis fuzz).
    /// </summary>
    private static int DupesSelfTest()
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — Find Duplicates (perceptual hash) self-test");
            var work = Path.Combine(AppPaths.ExeDir, "selftest-dupes");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            byte[] PatternPng(Func<int, int, byte> f)
            {
                using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(64, 64);
                img.ProcessPixelRows(acc =>
                {
                    for (int y = 0; y < 64; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        for (int x = 0; x < 64; x++) { var v = f(x, y); row[x] = new SixLabors.ImageSharp.PixelFormats.Rgba32(v, v, v); }
                    }
                });
                using var ms = new MemoryStream();
                SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, ms);
                return ms.ToArray();
            }

            var basePng = Path.Combine(work, "base.png");
            File.WriteAllBytes(basePng, PatternPng((x, y) => (byte)((x * 13 + y * 7) % 256)));
            var copyPng = Path.Combine(work, "base_copy.png");
            File.Copy(basePng, copyPng);                       // byte-identical → identical dHash
            var diffPng = Path.Combine(work, "different.png");
            File.WriteAllBytes(diffPng, PatternPng((x, y) => (byte)(((63 - x) * 13 + y * 7) % 256))); // mirrored

            var hBase = PerceptualHash.Compute(basePng);
            var hCopy = PerceptualHash.Compute(copyPng);
            var hDiff = PerceptualHash.Compute(diffPng);
            Check("hashes computed", hBase is not null && hCopy is not null && hDiff is not null);
            Check("identical copy → Hamming 0", PerceptualHash.Hamming(hBase!.Value, hCopy!.Value) == 0);
            var dDiff = PerceptualHash.Hamming(hBase!.Value, hDiff!.Value);
            W($"      Hamming(base, mirrored) = {dDiff}");
            Check("mirrored image is far (> 8)", dDiff > 8);
            Check("Compute is deterministic", PerceptualHash.Compute(basePng) == hBase);

            // End-to-end: scan → backfill → exact grouping.
            using (var db = new LibraryDb(FreshDb("selftest-dupes.db")))
            {
                new LocalScanner(db).Scan(new[] { work });
                Check("scanned 3 images", db.ImageCount() == 3);
                Check("backfill hashed all 3", db.BackfillPhashes() == 3);
                Check("backfill idempotent (0 on re-run)", db.BackfillPhashes() == 0);
                var g0 = db.FindDuplicateGroups(0);
                Check("exact: exactly one duplicate group", g0.Count == 1);
                Check("exact: group is the identical pair (mirror excluded)", g0.Count == 1 && g0[0].Length == 2);
            }

            // Grouping engine with controlled hashes: a==b (exact), c is distance 3 (near),
            // d is distance 8, e is all-bits — only a,b,c should group under maxDistance 3.
            using (var db = new LibraryDb(FreshDb("selftest-dupes-engine.db")))
            {
                long Ins(string name)
                {
                    db.Upsert(new ImageRow { SourceRoot = "C:\\s", RelPath = name, AbsPath = name, FileName = name, Ext = ".png", SizeBytes = 1, MtimeTicks = 1, MetaFormat = "none", ScannedAt = "t" });
                    return db.FindIdByAbsPath(name)!.Value;
                }
                long a = Ins("ph_a"), b = Ins("ph_b"), c = Ins("ph_c"), d = Ins("ph_d"), e = Ins("ph_e");
                db.UpdatePhash(a, 0L); db.UpdatePhash(b, 0L); db.UpdatePhash(c, 0b111L); db.UpdatePhash(d, 0xFFL); db.UpdatePhash(e, -1L);

                var g0 = db.FindDuplicateGroups(0);
                Check("exact engine: {a,b} only", g0.Count == 1 && g0[0].Length == 2 && g0[0].Contains(a) && g0[0].Contains(b));

                var g3 = db.FindDuplicateGroups(3);
                var ga = g3.FirstOrDefault(grp => grp.Contains(a));
                Check("near(3): a groups with b and c (banding finds dist-3)", ga is not null && ga.Contains(b) && ga.Contains(c) && ga.Length == 3);
                Check("near(3): far hashes d,e excluded", ga is not null && !ga.Contains(d) && !ga.Contains(e));
            }

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-dupes-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-dupes-result.txt");
            return 2;
        }
    }

    /// <summary>
    /// T23 (v2.0): the v3 schema foundation + migration. Verifies (1) a fresh DB is born at v3 with
    /// the full additive delta (favorite column + four tables + triggers + indexes), (2) an existing
    /// v2 DB migrates to v3 idempotently with favorite added and every row defaulting 0, and (3) all
    /// v2.0 user state (favorite flag, note, user tag, collection membership) SURVIVES a simulated
    /// rescan (UpsertCore's full image_tags replace) and an archive, then cascades away on delete.
    /// </summary>
    private static int V3MigrateSelfTest()
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder();
        var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string label, bool cond) { if (!cond) ok = false; W($"  [{(cond ? "ok" : "FAIL")}] {label}"); }

        SqliteConnection Open(string p) { var c = new SqliteConnection($"Data Source={p}"); c.Open(); Exec(c, "PRAGMA foreign_keys=ON;"); return c; }
        bool Obj(SqliteConnection c, string type, string name) =>
            Convert.ToInt64(Scalar(c, $"SELECT COUNT(*) FROM sqlite_master WHERE type='{type}' AND name='{name}';")) > 0;
        bool HasCol(SqliteConnection c, string table, string col)
        {
            using var cmd = c.CreateCommand(); cmd.CommandText = $"PRAGMA table_info({table});";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) if (string.Equals(rd.GetString(1), col, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        long L(SqliteConnection c, string sql) => Convert.ToInt64(Scalar(c, sql) ?? 0L);

        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — v3 schema foundation + migration (T23) self-test");

            // ---- Part 1: a fresh DB is born at v3 with the whole additive delta ----
            var freshPath = FreshDb("selftest-v3-fresh.db");
            using (new LibraryDb(freshPath)) { }   // EnsureSchema + Migrate on a brand-new file
            using (var c = Open(freshPath))
            {
                Check("fresh: schema_version = 3", L(c, "SELECT v FROM meta WHERE k='schema_version';") == 3);
                Check("fresh: images.favorite column present", HasCol(c, "images", "favorite"));
                Check("fresh: image_notes table", Obj(c, "table", "image_notes"));
                Check("fresh: user_tags table", Obj(c, "table", "user_tags"));
                Check("fresh: user_tag_freq table", Obj(c, "table", "user_tag_freq"));
                Check("fresh: collections table", Obj(c, "table", "collections"));
                Check("fresh: collection_items table", Obj(c, "table", "collection_items"));
                Check("fresh: utags_ai + utags_ad triggers", Obj(c, "trigger", "utags_ai") && Obj(c, "trigger", "utags_ad"));
                Check("fresh: ix_images_favorite index", Obj(c, "index", "ix_images_favorite"));
                Check("fresh: ix_user_tags_token index", Obj(c, "index", "ix_user_tags_token"));
                Check("fresh: ix_collection_items_image index", Obj(c, "index", "ix_collection_items_image"));

                // user_tag_freq triggers mirror tag_freq: insert bumps df, delete clears the row at 0.
                Exec(c, "INSERT INTO images(source_root,rel_path,abs_path,file_name,ext,size_bytes,mtime_ticks,scanned_at) VALUES('r','t.png','t.png','t.png','.png',1,1,'t');");
                long imgId = L(c, "SELECT id FROM images WHERE abs_path='t.png';");
                Exec(c, $"INSERT INTO user_tags(image_id,token) VALUES ({imgId},'red dress');");
                Check("utags_ai: user_tag_freq df = 1", L(c, "SELECT df FROM user_tag_freq WHERE token='red dress';") == 1);
                Exec(c, $"DELETE FROM user_tags WHERE image_id={imgId} AND token='red dress';");
                Check("utags_ad: user_tag_freq row gone at df 0", L(c, "SELECT COUNT(*) FROM user_tag_freq WHERE token='red dress';") == 0);
            }

            // ---- Part 2: an existing v2 DB migrates to v3 (idempotently) ----
            var v2Path = FreshDb("selftest-v3-from-v2.db");
            using (var c = Open(v2Path))
            {
                // Synthesize the real v2 images shape (phash present, favorite ABSENT) + schema_version=2 + 3 rows.
                Exec(c, @"CREATE TABLE images (
                  id INTEGER PRIMARY KEY,
                  source_root TEXT NOT NULL, rel_path TEXT NOT NULL, abs_path TEXT NOT NULL,
                  file_name TEXT NOT NULL, ext TEXT NOT NULL,
                  size_bytes INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
                  width INTEGER, height INTEGER, meta_format TEXT, meta_source TEXT,
                  prompt TEXT NOT NULL DEFAULT '', negative TEXT NOT NULL DEFAULT '',
                  params_json TEXT, thumb_path TEXT,
                  original_state TEXT NOT NULL DEFAULT 'present',
                  archived INTEGER NOT NULL DEFAULT 0,
                  scanned_at TEXT NOT NULL, phash INTEGER,
                  UNIQUE(abs_path));");
                Exec(c, "CREATE TABLE meta ( k TEXT PRIMARY KEY, v TEXT );");
                Exec(c, "INSERT INTO meta(k,v) VALUES('schema_version','2');");
                for (int i = 1; i <= 3; i++)
                    Exec(c, $"INSERT INTO images(source_root,rel_path,abs_path,file_name,ext,size_bytes,mtime_ticks,scanned_at) VALUES('r','v{i}.png','v{i}.png','v{i}.png','.png',1,1,'t');");
            }
            using (new LibraryDb(v2Path)) { }   // opening migrates v2 → v3
            using (var c = Open(v2Path))
            {
                Check("v2→v3: schema_version now 3", L(c, "SELECT v FROM meta WHERE k='schema_version';") == 3);
                Check("v2→v3: favorite column added", HasCol(c, "images", "favorite"));
                Check("v2→v3: 3 rows preserved, all favorite=0",
                    L(c, "SELECT COUNT(*) FROM images;") == 3 && L(c, "SELECT COUNT(*) FROM images WHERE favorite=0;") == 3);
                Check("v2→v3: ix_images_favorite built", Obj(c, "index", "ix_images_favorite"));
                Check("v2→v3: four new tables present",
                    Obj(c, "table", "image_notes") && Obj(c, "table", "user_tags")
                    && Obj(c, "table", "collections") && Obj(c, "table", "collection_items"));
                Check("v2→v3: new tables empty",
                    L(c, "SELECT COUNT(*) FROM user_tags;") == 0 && L(c, "SELECT COUNT(*) FROM collection_items;") == 0
                    && L(c, "SELECT COUNT(*) FROM image_notes;") == 0);
            }
            using (new LibraryDb(v2Path)) { }   // re-open: must be a no-op
            using (var c = Open(v2Path))
                Check("v2→v3: re-open idempotent (still v3)",
                    L(c, "SELECT v FROM meta WHERE k='schema_version';") == 3 && HasCol(c, "images", "favorite"));

            // ---- Part 2b: an existing v1 DB (no phash, no favorite) migrates straight to v3 ----
            // Exercises BOTH the current<2 (phash) and current<3 (favorite) branches in one open, and
            // proves EnsureSchema no longer crashes building an index over a not-yet-added column.
            var v1Path = FreshDb("selftest-v3-from-v1.db");
            using (var c = Open(v1Path))
            {
                // Real v1 images shape: NO phash, NO favorite.
                Exec(c, @"CREATE TABLE images (
                  id INTEGER PRIMARY KEY,
                  source_root TEXT NOT NULL, rel_path TEXT NOT NULL, abs_path TEXT NOT NULL,
                  file_name TEXT NOT NULL, ext TEXT NOT NULL,
                  size_bytes INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
                  width INTEGER, height INTEGER, meta_format TEXT, meta_source TEXT,
                  prompt TEXT NOT NULL DEFAULT '', negative TEXT NOT NULL DEFAULT '',
                  params_json TEXT, thumb_path TEXT,
                  original_state TEXT NOT NULL DEFAULT 'present',
                  archived INTEGER NOT NULL DEFAULT 0,
                  scanned_at TEXT NOT NULL,
                  UNIQUE(abs_path));");
                Exec(c, "CREATE TABLE meta ( k TEXT PRIMARY KEY, v TEXT );");
                Exec(c, "INSERT INTO meta(k,v) VALUES('schema_version','1');");
                Exec(c, "INSERT INTO images(source_root,rel_path,abs_path,file_name,ext,size_bytes,mtime_ticks,scanned_at) VALUES('r','o.png','o.png','o.png','.png',1,1,'t');");
            }
            using (new LibraryDb(v1Path)) { }   // must NOT crash; runs current<2 + current<3 then stamps once
            using (var c = Open(v1Path))
            {
                Check("v1→v3: schema_version now 3", L(c, "SELECT v FROM meta WHERE k='schema_version';") == 3);
                Check("v1→v3: phash column added (current<2 branch)", HasCol(c, "images", "phash"));
                Check("v1→v3: favorite column added (current<3 branch)", HasCol(c, "images", "favorite"));
                Check("v1→v3: both column indexes built", Obj(c, "index", "ix_images_phash") && Obj(c, "index", "ix_images_favorite"));
                Check("v1→v3: existing row preserved + favorite=0", L(c, "SELECT COUNT(*) FROM images WHERE favorite=0;") == 1);
                Check("v1→v3: v3 tables present", Obj(c, "table", "user_tags") && Obj(c, "table", "collections"));
            }

            // ---- Part 3: user state survives rescan + archive; cascades on image delete ----
            var statePath = FreshDb("selftest-v3-state.db");
            using (var db = new LibraryDb(statePath))
            {
                ImageRow Row(string abs, params string[] tags) => new()
                {
                    SourceRoot = "C:\\src", RelPath = abs, AbsPath = abs, FileName = abs, Ext = ".png",
                    SizeBytes = 100, MtimeTicks = 1, MetaFormat = "a1111", MetaSource = "embedded",
                    Prompt = "p", Negative = "n", ScannedAt = "2026-06-24", Tags = tags.ToList()
                };
                db.Upsert(Row("X.png", "alpha", "beta"));
                long id = db.FindIdByAbsPath("X.png")!.Value;

                // Attach v2.0 user state directly (the feature setters land in T24–T28).
                using (var c = Open(statePath))
                {
                    Exec(c, $"UPDATE images SET favorite=1 WHERE id={id};");
                    Exec(c, $"INSERT INTO image_notes(image_id,body,updated_at) VALUES ({id},'my note','2026-06-24');");
                    Exec(c, $"INSERT INTO user_tags(image_id,token) VALUES ({id},'mine');");
                    Exec(c, "INSERT INTO collections(name) VALUES ('Faves');");
                    Exec(c, $"INSERT INTO collection_items(collection_id,image_id) SELECT id,{id} FROM collections WHERE name='Faves';");
                }

                // Simulated rescan: re-upsert the SAME file with a DIFFERENT tag set.
                db.Upsert(Row("X.png", "gamma", "delta"));
                using (var c = Open(statePath))
                {
                    Check("rescan: image_tags rebuilt (alpha gone, gamma in)",
                        L(c, $"SELECT COUNT(*) FROM image_tags WHERE image_id={id} AND token='alpha';") == 0
                        && L(c, $"SELECT COUNT(*) FROM image_tags WHERE image_id={id} AND token='gamma';") == 1);
                    Check("rescan: favorite SURVIVES", L(c, $"SELECT favorite FROM images WHERE id={id};") == 1);
                    Check("rescan: note SURVIVES", L(c, $"SELECT COUNT(*) FROM image_notes WHERE image_id={id};") == 1);
                    Check("rescan: user_tag SURVIVES", L(c, $"SELECT COUNT(*) FROM user_tags WHERE image_id={id} AND token='mine';") == 1);
                    Check("rescan: collection membership SURVIVES", L(c, $"SELECT COUNT(*) FROM collection_items WHERE image_id={id};") == 1);
                }

                // Archive: state still intact.
                db.SetArchived(id, "X-archived.png");
                using (var c = Open(statePath))
                    Check("archive: favorite/note/user_tag/collection all intact",
                        L(c, $"SELECT favorite FROM images WHERE id={id};") == 1
                        && L(c, $"SELECT COUNT(*) FROM image_notes WHERE image_id={id};") == 1
                        && L(c, $"SELECT COUNT(*) FROM user_tags WHERE image_id={id};") == 1
                        && L(c, $"SELECT COUNT(*) FROM collection_items WHERE image_id={id};") == 1);

                // Delete the image: ON DELETE CASCADE drops note/user_tag/membership (collection stays).
                db.Remove(id);
                using (var c = Open(statePath))
                {
                    Check("delete cascade: note removed", L(c, $"SELECT COUNT(*) FROM image_notes WHERE image_id={id};") == 0);
                    Check("delete cascade: user_tags removed", L(c, $"SELECT COUNT(*) FROM user_tags WHERE image_id={id};") == 0);
                    Check("delete cascade: collection_items removed", L(c, $"SELECT COUNT(*) FROM collection_items WHERE image_id={id};") == 0);
                    Check("delete cascade: collection itself remains", L(c, "SELECT COUNT(*) FROM collections WHERE name='Faves';") == 1);
                }
            }

            W(ok ? "RESULT: PASS" : "RESULT: FAIL");
            WriteResultNamed(log, "selftest-v3migrate-result.txt");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL (exception)");
            W(ex.ToString());
            WriteResultNamed(log, "selftest-v3migrate-result.txt");
            return 2;
        }
    }

    private static ImageRow TestRow(string name, params string[] tags) => new()
    {
        SourceRoot = "C:\\s", RelPath = name, AbsPath = name, FileName = name, Ext = ".png",
        SizeBytes = 1, MtimeTicks = 1, MetaFormat = "a1111", MetaSource = "embedded",
        Prompt = "p", Negative = "n", ScannedAt = "t", Tags = tags.ToList()
    };

    /// <summary>T24 Favorites: SetFavorite/FavoriteCount, ImageRow.Favorite mapping, the favoritesOnly
    /// query filter (page==total parity + AND-composition), rescan survival, and the bridge counts/page.</summary>
    private static int FavoritesSelfTest()
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder(); var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string l, bool c) { if (!c) ok = false; W($"  [{(c ? "ok" : "FAIL")}] {l}"); }
        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — Favorites (T24) self-test");
            using var db = new LibraryDb(FreshDb("selftest-fav.db"));
            db.Upsert(TestRow("A.png", "1girl", "solo")); db.Upsert(TestRow("B.png", "1girl")); db.Upsert(TestRow("C.png", "landscape"));
            long A = db.FindIdByAbsPath("A.png")!.Value, B = db.FindIdByAbsPath("B.png")!.Value;

            db.SetFavorite(A, true); db.SetFavorite(B, true);
            Check("FavoriteCount = 2", db.FavoriteCount() == 2);
            var fav = db.Query(SearchParser.Parse(""), 0, 100, includeArchived: true, favoritesOnly: true);
            Check("favoritesOnly total = 2", fav.Total == 2);
            Check("favoritesOnly page==total (no desync)", fav.Page.Count == fav.Total);
            Check("ImageRow.Favorite mapped by name", fav.Page.All(r => r.Favorite));
            Check("unfiltered total = 3", db.Query(SearchParser.Parse(""), 0, 100, true).Total == 3);
            Check("favorites AND '1girl' = 2", db.Query(SearchParser.Parse("1girl"), 0, 100, true, favoritesOnly: true).Total == 2);
            Check("favorites AND 'landscape' = 0", db.Query(SearchParser.Parse("landscape"), 0, 100, true, favoritesOnly: true).Total == 0);

            db.Upsert(TestRow("A.png", "sunset", "beach"));   // rescan with a new tag set
            Check("favorite SURVIVES rescan", db.FavoriteCount() == 2 && db.GetById(A)!.Favorite);
            db.SetFavorite(A, false);
            Check("unfavorite drops count to 1", db.FavoriteCount() == 1);

            var bridge = new GalleryBridge(db, r => $"https://full.local/{r.Id}");
            using var cd = System.Text.Json.JsonDocument.Parse(bridge.Handle("{\"type\":\"counts\"}")!);
            Check("counts reply favorites = 1", cd.RootElement.GetProperty("favorites").GetInt32() == 1);
            using var pd = System.Text.Json.JsonDocument.Parse(bridge.Handle("{\"type\":\"query\",\"raw\":\"\",\"page\":0,\"size\":60,\"favoritesOnly\":true}")!);
            Check("bridge favoritesOnly total = 1", pd.RootElement.GetProperty("total").GetInt32() == 1);
            Check("page item carries favorite flag", pd.RootElement.GetProperty("items")[0].GetProperty("favorite").GetBoolean());

            W(ok ? "RESULT: PASS" : "RESULT: FAIL"); WriteResultNamed(log, "selftest-favorites-result.txt"); return ok ? 0 : 1;
        }
        catch (Exception ex) { W("RESULT: FAIL (exception)"); W(ex.ToString()); WriteResultNamed(log, "selftest-favorites-result.txt"); return 2; }
    }

    /// <summary>T25 Notes: upsert semantics (one row), clear-on-empty, rescan + archive survival,
    /// cascade on image delete, and the note carried on the bridge inspect reply.</summary>
    private static int NotesSelfTest()
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder(); var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string l, bool c) { if (!c) ok = false; W($"  [{(c ? "ok" : "FAIL")}] {l}"); }
        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — Notes (T25) self-test");
            var path = FreshDb("selftest-notes.db");
            using var db = new LibraryDb(path);
            db.Upsert(TestRow("X.png", "a"));
            long X = db.FindIdByAbsPath("X.png")!.Value;

            db.SetNote(X, "hello hoard");
            var n1 = db.GetNote(X);
            Check("GetNote body round-trips", n1 is { } && n1.Value.Body == "hello hoard");
            Check("note has updatedAt", n1 is { } && !string.IsNullOrEmpty(n1.Value.UpdatedAt));
            db.SetNote(X, "updated body");   // upsert
            Check("upsert updates body", db.GetNote(X)?.Body == "updated body");
            using (var raw = new SqliteConnection($"Data Source={path}"))
            { raw.Open(); Check("upsert keeps exactly ONE row", Convert.ToInt64(Scalar(raw, $"SELECT COUNT(*) FROM image_notes WHERE image_id={X};")) == 1); }
            db.SetNote(X, "");
            Check("empty body clears to ''", db.GetNote(X)?.Body == "");

            db.SetNote(X, "keep me");
            db.Upsert(TestRow("X.png", "b"));   // rescan
            Check("note SURVIVES rescan", db.GetNote(X)?.Body == "keep me");
            db.SetArchived(X, "X-arch.png");
            Check("note SURVIVES archive", db.GetNote(X)?.Body == "keep me");

            var bridge = new GalleryBridge(db, r => $"https://full.local/{r.Id}");
            using (var doc = System.Text.Json.JsonDocument.Parse(bridge.Handle($"{{\"type\":\"inspect\",\"id\":{X}}}")!))
                Check("inspect reply carries note", doc.RootElement.GetProperty("note").GetString() == "keep me");

            db.Remove(X);
            Check("note CASCADES on image delete", db.GetNote(X) is null);

            W(ok ? "RESULT: PASS" : "RESULT: FAIL"); WriteResultNamed(log, "selftest-notes-result.txt"); return ok ? 0 : 1;
        }
        catch (Exception ex) { W("RESULT: FAIL (exception)"); W(ex.ToString()); WriteResultNamed(log, "selftest-notes-result.txt"); return 2; }
    }

    /// <summary>T26 Manual tags: TokenSet normalization, UNION search (manual tag matches though the
    /// prompt lacked it), comma-AND arity, UNION dedupe, rescan-safety, autocomplete blend, and the
    /// tagadd/tagdel bridge replies + inspect promptTags/userTags.</summary>
    private static int UserTagsSelfTest()
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder(); var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string l, bool c) { if (!c) ok = false; W($"  [{(c ? "ok" : "FAIL")}] {l}"); }
        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — Manual tags (T26) self-test");
            using var db = new LibraryDb(FreshDb("selftest-usertags.db"));
            db.Upsert(TestRow("img.png", "1girl", "solo"));
            long id = db.FindIdByAbsPath("img.png")!.Value;

            var added = db.AddUserTags(id, "Red Dress");
            Check("AddUserTags normalizes 'Red Dress' → 'red dress'", added.Contains("red dress") && db.UserTagsFor(id).Contains("red dress"));
            Check("PromptTagsFor reads image_tags", db.PromptTagsFor(id).Contains("1girl"));
            Check("search 'red dress' matches via UNION (prompt lacked it)", db.Query(SearchParser.Parse("red dress"), 0, 100, true).Total == 1);
            Check("comma-AND '1girl, red dress' = 1 (arity)", db.Query(SearchParser.Parse("1girl, red dress"), 0, 100, true).Total == 1);

            db.AddUserTags(id, "1girl");   // duplicate of a prompt tag
            Check("UNION dedupe: '1girl' still total 1 (not double)", db.Query(SearchParser.Parse("1girl"), 0, 100, true).Total == 1);
            var q = db.Query(SearchParser.Parse("red dress"), 0, 100, true);
            Check("UNION query page==total parity", q.Page.Count == q.Total);
            Check("autocomplete blends manual tags ('red' → 'red dress')", db.TopTags("red", 8).Any(t => t.Token == "red dress"));

            db.Upsert(TestRow("img.png", "landscape"));   // rescan with new prompt tags
            Check("user_tags SURVIVE rescan", db.UserTagsFor(id).Contains("red dress"));
            Check("prompt tags rebuilt (1girl gone, landscape in)", !db.PromptTagsFor(id).Contains("1girl") && db.PromptTagsFor(id).Contains("landscape"));

            db.RemoveUserTag(id, "red dress");
            Check("RemoveUserTag drops it", !db.UserTagsFor(id).Contains("red dress"));

            var bridge = new GalleryBridge(db, r => $"https://full.local/{r.Id}");
            using (var ta = System.Text.Json.JsonDocument.Parse(bridge.Handle($"{{\"type\":\"tagadd\",\"id\":{id},\"text\":\"blue sky\"}}")!))
                Check("tagadd reply type=tags + user has 'blue sky'",
                    ta.RootElement.GetProperty("type").GetString() == "tags"
                    && ta.RootElement.GetProperty("user").EnumerateArray().Any(x => x.GetString() == "blue sky"));
            using (var td = System.Text.Json.JsonDocument.Parse(bridge.Handle($"{{\"type\":\"tagdel\",\"id\":{id},\"token\":\"blue sky\"}}")!))
                Check("tagdel reply removes it", !td.RootElement.GetProperty("user").EnumerateArray().Any(x => x.GetString() == "blue sky"));

            db.AddUserTags(id, "keepme");
            using (var ins = System.Text.Json.JsonDocument.Parse(bridge.Handle($"{{\"type\":\"inspect\",\"id\":{id}}}")!))
                Check("inspect carries userTags", ins.RootElement.GetProperty("userTags").EnumerateArray().Any(x => x.GetString() == "keepme"));

            W(ok ? "RESULT: PASS" : "RESULT: FAIL"); WriteResultNamed(log, "selftest-usertags-result.txt"); return ok ? 0 : 1;
        }
        catch (Exception ex) { W("RESULT: FAIL (exception)"); W(ex.ToString()); WriteResultNamed(log, "selftest-usertags-result.txt"); return 2; }
    }

    /// <summary>T28 Collections: create + UNIQUE-NOCASE, idempotent add, filter==membership (page==total),
    /// AND-composition, remove, rename, name-sort, and cascade on both image delete and collection delete.</summary>
    private static int CollectionsSelfTest()
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder(); var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string l, bool c) { if (!c) ok = false; W($"  [{(c ? "ok" : "FAIL")}] {l}"); }
        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — Collections (T28) self-test");
            var path = FreshDb("selftest-collections.db");
            using var db = new LibraryDb(path);
            db.Upsert(TestRow("A.png", "x")); db.Upsert(TestRow("B.png", "x")); db.Upsert(TestRow("C.png", "y"));
            long A = db.FindIdByAbsPath("A.png")!.Value, B = db.FindIdByAbsPath("B.png")!.Value, C = db.FindIdByAbsPath("C.png")!.Value;

            long fav = db.CreateCollection("Faves");
            Check("CreateCollection returns id", fav > 0);
            Check("duplicate name (NOCASE) rejected → -1", db.CreateCollection("faves") == -1);
            db.AddToCollection(fav, new[] { A, B });
            db.AddToCollection(fav, new[] { A });   // idempotent re-add
            Check("ListCollections membership count = 2 (idempotent)", db.ListCollections().Single().Count == 2);

            var q = db.Query(SearchParser.Parse(""), 0, 100, true, collectionId: fav);
            Check("collection filter total = 2", q.Total == 2);
            Check("collection page==total parity", q.Page.Count == q.Total);
            Check("collection AND 'x' = 2", db.Query(SearchParser.Parse("x"), 0, 100, true, collectionId: fav).Total == 2);
            Check("collection AND 'y' = 0", db.Query(SearchParser.Parse("y"), 0, 100, true, collectionId: fav).Total == 0);

            db.RemoveFromCollection(fav, new[] { A });
            Check("RemoveFromCollection → membership 1", db.ListCollections().Single().Count == 1);
            db.Remove(B);
            Check("image delete CASCADES membership (0 left)", db.ListCollections().Single().Count == 0);
            Check("RenameCollection", db.RenameCollection(fav, "Best") && db.ListCollections().Single().Name == "Best");

            db.CreateCollection("Zeta"); db.CreateCollection("Alpha");
            Check("ListCollections name-sorted (NOCASE)",
                db.ListCollections().Select(c => c.Name).SequenceEqual(new[] { "Alpha", "Best", "Zeta" }));

            db.AddToCollection(fav, new[] { C });
            db.DeleteCollection(fav);
            Check("collection delete removes it from the list", db.ListCollections().All(c => c.Name != "Best"));
            using (var raw = new SqliteConnection($"Data Source={path}"))
            { raw.Open(); Check("collection delete CASCADES its items (no orphans)", Convert.ToInt64(Scalar(raw, $"SELECT COUNT(*) FROM collection_items WHERE collection_id={fav};")) == 0); }

            W(ok ? "RESULT: PASS" : "RESULT: FAIL"); WriteResultNamed(log, "selftest-collections-result.txt"); return ok ? 0 : 1;
        }
        catch (Exception ex) { W("RESULT: FAIL (exception)"); W(ex.ToString()); WriteResultNamed(log, "selftest-collections-result.txt"); return 2; }
    }

    /// <summary>T27 Auto-Tag (suggest-only KNN): neighbors must be near AND have user tags; vote-rank;
    /// exclude tokens already on the target; suggest-only (no writes); null-hash guard; reply shape + gen.</summary>
    private static int AutotagSelfTest()
    {
        Native.TryAttachParentConsole();
        var log = new StringBuilder(); var ok = true;
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        void Check(string l, bool c) { if (!c) ok = false; W($"  [{(c ? "ok" : "FAIL")}] {l}"); }
        try
        {
            W($"{AppInfo.Name} v{AppInfo.Version} — Auto-Tag (T27) self-test");
            using var db = new LibraryDb(FreshDb("selftest-autotag.db"));
            long Ins(string n) { db.Upsert(TestRow(n)); return db.FindIdByAbsPath(n)!.Value; }
            long T = Ins("T.png"), N1 = Ins("N1.png"), N2 = Ins("N2.png"), F = Ins("F.png"), NB = Ins("NB.png");
            // Controlled hashes: T=0; N1 dist 1; N2 dist 2; NB dist 3 (near, but NO user tags); F dist 64 (far).
            db.UpdatePhash(T, 0L); db.UpdatePhash(N1, 0b1L); db.UpdatePhash(N2, 0b11L); db.UpdatePhash(NB, 0b111L); db.UpdatePhash(F, -1L);
            db.AddUserTags(N1, "red dress"); db.AddUserTags(N1, "1girl");
            db.AddUserTags(N2, "red dress");
            db.AddUserTags(F, "far tag");          // far → must never surface

            var res = db.SuggestTagsByPhash(T, 3, 20);
            Check("neighbors = N1,N2 (near AND have user tags; NB/F excluded)",
                res.Neighbors.Select(n => n.Id).OrderBy(x => x).SequenceEqual(new[] { N1, N2 }.OrderBy(x => x)));
            Check("top suggestion 'red dress' votes = 2", res.Suggestions.Count > 0 && res.Suggestions[0].Token == "red dress" && res.Suggestions[0].Votes == 2);
            Check("'1girl' suggested votes = 1", res.Suggestions.Any(s => s.Token == "1girl" && s.Votes == 1));
            Check("far image's tag excluded", !res.Suggestions.Any(s => s.Token == "far tag"));

            db.AddUserTags(T, "red dress");        // already on target now
            var res2 = db.SuggestTagsByPhash(T, 3, 20);
            Check("token already on target excluded", !res2.Suggestions.Any(s => s.Token == "red dress"));
            Check("'1girl' still suggested", res2.Suggestions.Any(s => s.Token == "1girl"));

            var before = db.UserTagsFor(T).OrderBy(x => x).ToList();
            db.SuggestTagsByPhash(T, 3, 20);
            Check("suggest-only: target user_tags byte-for-byte unchanged", db.UserTagsFor(T).OrderBy(x => x).SequenceEqual(before));

            long U = Ins("U.png");                 // no phash assigned
            Check("null target hash → empty (caller backfills at runtime)", db.SuggestTagsByPhash(U, 3, 20).Suggestions.Count == 0);

            var reply = GalleryBridge.AutotagReply(db, r => $"https://full.local/{r.Id}", T, db.SuggestTagsByPhash(T, 3, 20), 7);
            using var doc = System.Text.Json.JsonDocument.Parse(reply);
            Check("AutotagReply type=autotag + gen echoed", doc.RootElement.GetProperty("type").GetString() == "autotag" && doc.RootElement.GetProperty("gen").GetInt32() == 7);
            Check("reply has suggestions + 2 neighbors", doc.RootElement.GetProperty("suggestions").GetArrayLength() > 0 && doc.RootElement.GetProperty("neighbors").GetArrayLength() == 2);
            var nb0 = doc.RootElement.GetProperty("neighbors")[0];
            Check("neighbor carries url + distance", (nb0.GetProperty("url").GetString() ?? "").StartsWith("https://full.local/") && nb0.TryGetProperty("distance", out _));

            W(ok ? "RESULT: PASS" : "RESULT: FAIL"); WriteResultNamed(log, "selftest-autotag-result.txt"); return ok ? 0 : 1;
        }
        catch (Exception ex) { W("RESULT: FAIL (exception)"); W(ex.ToString()); WriteResultNamed(log, "selftest-autotag-result.txt"); return 2; }
    }

    private static string FreshDb(string name)
    {
        var p = Path.Combine(AppPaths.ExeDir, name);
        foreach (var f in new[] { p, p + "-wal", p + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return p;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static void WriteResultNamed(StringBuilder log, string file)
    {
        try { File.WriteAllText(Path.Combine(AppPaths.ExeDir, file), log.ToString()); } catch { }
    }

    private static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void WriteResult(StringBuilder log)
    {
        try { File.WriteAllText(Path.Combine(AppPaths.ExeDir, "selftest-result.txt"), log.ToString()); }
        catch { /* best-effort */ }
    }
}
