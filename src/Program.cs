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
