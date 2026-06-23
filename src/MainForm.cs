using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TheTagHag;

/// <summary>
/// T10 gallery shell: WebView2-hosted Dark Magic Pro gallery wired to the engine via
/// GalleryBridge. Toolbar adds source folders + scans; images are served per-root through
/// WebView2 virtual hosts (src0.local, src1.local, …). Lightbox/inspector (T11), multi-select
/// (T12) and the thumbnail cache (T15a) layer on next.
/// </summary>
public sealed class MainForm : Form
{
    private AppSettings _settings = SettingsStore.Load();
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly ToolStrip _tb = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripStatusLabel _statusLabel = new("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly StatusStrip _status = new() { SizingGrip = false };

    private LibraryDb _db = null!;
    private GalleryBridge _bridge = null!;
    private ThumbnailService _thumbs = null!;
    private bool _busy;

    public MainForm()
    {
        Text = $"{AppInfo.Name} v{AppInfo.Version}";
        AppIcon.Apply(this);
        BackColor = Color.FromArgb(0x14, 0x10, 0x18);
        RestoreWindowBounds();

        BuildToolbar();
        _status.BackColor = Color.FromArgb(0x1B, 0x16, 0x22);
        _status.ForeColor = Color.FromArgb(0xD8, 0xD0, 0xBF);
        _status.Items.Add(_statusLabel);

        Controls.Add(_web);
        Controls.Add(_status);
        Controls.Add(_tb);

        Load += async (_, _) => await InitAsync();
        FormClosing += (_, _) => { SaveWindowBounds(); _thumbs?.Dispose(); _db?.Dispose(); };
    }

    private void BuildToolbar()
    {
        _tb.BackColor = Color.FromArgb(0x1B, 0x16, 0x22);
        _tb.ForeColor = Color.FromArgb(0xD8, 0xD0, 0xBF);
        _tb.Items.Add(Btn("Add source folder", AddSourceFolder));
        _tb.Items.Add(Btn("Scan", async () => await DoScan()));
        _tb.Items.Add(new ToolStripSeparator());
        _tb.Items.Add(Btn("Civitai", OpenCivitai));
        _tb.Items.Add(Btn("Settings", async () => await OpenSettings()));
        _tb.Items.Add(Btn("About", () => MessageBox.Show($"{AppInfo.Name} v{AppInfo.Version}\n{AppInfo.Tagline}", AppInfo.Name)));
    }

    private static ToolStripButton Btn(string text, Action onClick)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += (_, _) => onClick();
        return b;
    }

    private async Task InitAsync()
    {
        _db = new LibraryDb(AppPaths.LibraryDbFile);
        _bridge = new GalleryBridge(_db, UrlFor);

        await _web.EnsureCoreWebView2Async(null);
        _web.CoreWebView2.WebMessageReceived += OnWebMessage;
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; // use our own right-click menu
        _web.CoreWebView2.Settings.IsZoomControlEnabled = false;

        // Images are served BY ID through interceptors — root-count independent, no per-root hosts:
        //   thumb.local/{id} → lazy 512px WebP (grid)    full.local/{id} → original bytes (lightbox)
        _thumbs = new ThumbnailService(AppPaths.LibraryDbFile, AppPaths.ThumbsDir);
        _web.CoreWebView2.AddWebResourceRequestedFilter("https://thumb.local/*", CoreWebView2WebResourceContext.Image);
        _web.CoreWebView2.AddWebResourceRequestedFilter("https://full.local/*", CoreWebView2WebResourceContext.Image);
        _web.CoreWebView2.WebResourceRequested += OnImageRequested;

        // Serve the app page from its OWN virtual host (https://app.local) so the document has a
        // real origin — otherwise (NavigateToString = null origin) WebView2 blocks the
        // https://srcN.local image requests and every thumbnail comes back blank.
        var uiDir = Path.Combine(AppPaths.ExeDir, "ui");
        Directory.CreateDirectory(uiDir);
        File.WriteAllText(Path.Combine(uiDir, "index.html"), LoadTemplate());
        WriteEmbeddedAsset("logo-lockup.png", Path.Combine(uiDir, "logo-lockup.png")); // brand lockup for the sidebar
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", uiDir, CoreWebView2HostResourceAccessKind.Allow);

        _web.CoreWebView2.Navigate("https://app.local/index.html");
        SetStatus(_settings.SourceRoots.Count == 0
            ? "No folders indexed yet — add a source folder, then Scan."
            : $"{_db.ImageCount(includeArchived: false):n0} images indexed.");
    }

    /// <summary>Every image is addressed by id and served via the full.local interceptor — no
    /// per-root virtual hosts, so any number of source folders works and paths can't desync.</summary>
    private static string UrlFor(ImageRow r) => $"https://full.local/{r.Id}";

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString() ?? ""; } catch { return; }

        // Clipboard copy + file ops are handled host-side (dialogs + STA clipboard) — no bridge reply.
        try
        {
            using var d = JsonDocument.Parse(msg);
            var type = d.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "copy")
            {
                var text = d.RootElement.TryGetProperty("text", out var x) ? x.GetString() : null;
                if (!string.IsNullOrEmpty(text)) { try { Clipboard.SetText(text); SetStatus("Copied to clipboard."); } catch { } }
                return;
            }
            if (type == "op")
            {
                var op = d.RootElement.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
                var ids = d.RootElement.TryGetProperty("ids", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(x => x.GetInt64()).ToArray() : Array.Empty<long>();
                _ = HandleOp(op, ids);
                return;
            }
            if (type == "openlocation")
            {
                if (d.RootElement.TryGetProperty("id", out var idEl))
                {
                    var row = _db.GetById(idEl.GetInt64());
                    if (row is not null && File.Exists(row.AbsPath))
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{row.AbsPath}\"") { UseShellExecute = true }); }
                        catch { }
                    else SetStatus("File not found on disk.");
                }
                return;
            }
            if (type == "dupes")
            {
                var near = d.RootElement.TryGetProperty("near", out var nr) && nr.ValueKind == JsonValueKind.True;
                var gen = d.RootElement.TryGetProperty("gen", out var gg) && gg.ValueKind == JsonValueKind.Number ? gg.GetInt32() : 0;
                _ = HandleDupes(near, gen);
                return;
            }
        }
        catch { /* not JSON / no type → fall through to bridge */ }

        var reply = _bridge.Handle(msg);
        if (reply is not null) _web.CoreWebView2.PostWebMessageAsString(reply);
    }

    private void OnImageRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = e.Request.Uri;
        bool thumb = uri.StartsWith("https://thumb.local/", StringComparison.OrdinalIgnoreCase);
        bool full = uri.StartsWith("https://full.local/", StringComparison.OrdinalIgnoreCase);
        if (!thumb && !full) return;

        var deferral = e.GetDeferral();
        var idPart = uri[(uri.IndexOf(".local/", StringComparison.Ordinal) + 7)..];
        var q = idPart.IndexOf('?'); if (q >= 0) idPart = idPart[..q];

        _ = Task.Run(() =>
        {
            byte[]? bytes = null; string ct = "image/webp";
            try
            {
                if (long.TryParse(idPart, out var id))
                {
                    if (thumb) { var p = _thumbs.GetOrCreate(id); if (p is not null) bytes = File.ReadAllBytes(p); }
                    else { var p = _thumbs.GetOriginalPath(id); if (p is not null) { bytes = File.ReadAllBytes(p); ct = ContentType(p); } }
                }
            }
            catch { /* serve 404 below */ }

            try
            {
                BeginInvoke(() =>
                {
                    try
                    {
                        e.Response = bytes is not null
                            ? _web.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", "Content-Type: " + ct)
                            : _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                    }
                    catch { /* form closing / webview gone */ }
                    finally { deferral.Complete(); }
                });
            }
            catch { deferral.Complete(); }
        });
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    // ---------------- Civitai browse mode (T17b — pick & import from the live feed) ----------------
    private void OpenCivitai()
    {
        using var dlg = new CivitaiBrowseForm(_settings);
        dlg.ShowDialog(this);
        if (dlg.DidImport)
        {
            SetStatus($"{_db.ImageCount(includeArchived: false):n0} images.");
            _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}"); // show the newly imported images
        }
    }

    // ---------------- settings (T16) ----------------
    private async Task OpenSettings()
    {
        var before = _settings.SourceRoots.ToList();
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var removed = before.Where(r => !dlg.SourceRoots.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        var added = dlg.SourceRoots.Where(r => !before.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();

        _settings.SourceRoots = dlg.SourceRoots;
        _settings.ExportDir = dlg.ExportDir;
        _settings.MaxDim = dlg.MaxDim;
        _settings.ApiKey = dlg.ApiKey;   // encrypted via DPAPI on assignment
        SettingsStore.Save(_settings);

        if (removed.Count > 0)
        {
            SetStatus("Removing folders from the library…");
            int n = 0;
            await Task.Run(() => { using var opDb = new LibraryDb(AppPaths.LibraryDbFile); foreach (var r in removed) n += opDb.RemoveBySourceRoot(r); });
            SetStatus($"Removed {removed.Count} folder(s) ({n:n0} images). {_db.ImageCount(includeArchived: false):n0} images.");
        }

        _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");

        if (added.Count > 0)
        {
            var r = MessageBox.Show(this, $"{added.Count} new folder(s) added. Scan now?", "Scan", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) await DoScan();
        }
    }

    private void AddSourceFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Add a source folder to scan" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (!_settings.SourceRoots.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            _settings.SourceRoots.Add(dlg.SelectedPath);
            SettingsStore.Save(_settings);
            SetStatus($"Added {dlg.SelectedPath}. Click Scan to index it.");
        }
    }

    private async Task DoScan()
    {
        if (_busy) return;
        if (_settings.SourceRoots.Count == 0) { AddSourceFolder(); if (_settings.SourceRoots.Count == 0) return; }
        _busy = true;
        SetStatus("Scanning…");
        var roots = _settings.SourceRoots.ToList();
        ScanResult r = default;
        try
        {
            await Task.Run(() =>
            {
                using var scanDb = new LibraryDb(AppPaths.LibraryDbFile); // own writer connection (WAL)
                var scanner = new LocalScanner(scanDb);
                r = scanner.Scan(roots, default, new Progress<HarvestProgress>(p =>
                    BeginInvoke(() => SetStatus($"{p.Phase}… {p.Current}/{p.Total}"))));
            });
            SetStatus($"Scan complete — +{r.Added} added, {r.Updated} updated, {r.Unchanged} unchanged, {r.Removed} removed, {r.Failed} failed. " +
                      $"{_db.ImageCount(includeArchived: false):n0} images.");
            _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
        }
        catch (Exception ex) { SetStatus("Scan failed: " + ex.Message); }
        finally { _busy = false; }
    }

    // ---------------- curation (T13) ----------------
    private async Task HandleOp(string op, long[] ids)
    {
        if (_busy || ids.Length == 0) return;
        switch (op)
        {
            case "delete":
            {
                var r = MessageBox.Show(this,
                    $"Send {ids.Length} file(s) to the Recycle Bin? You can restore them from there.",
                    "Delete", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.OK) return;
                await RunOp(ids, "Deleted", (db, row) => { FileOps.RecycleDelete(row.AbsPath); db.Remove(row.Id); });
                break;
            }
            case "copy":
            case "move":
            {
                var dest = PickExportFolder($"Choose a destination folder to {op} {ids.Length} file(s) into");
                if (dest is null) return;
                if (op == "copy")
                    await RunOp(ids, "Copied", (db, row) => File.Copy(row.AbsPath, FileOps.UniqueDestination(dest, row.FileName)));
                else
                    await RunOp(ids, "Moved", (db, row) => { FileOps.Move(row.AbsPath, FileOps.UniqueDestination(dest, row.FileName)); db.Remove(row.Id); });
                break;
            }
            case "archive":
            {
                if (!EnsureExportDir()) return;
                var archiveBase = Path.Combine(_settings.ExportDir!, "-Archive");
                var r = MessageBox.Show(this,
                    $"Move {ids.Length} file(s) to the Bog?\n\nThey'll be moved to:\n{archiveBase}\n(hidden from the library unless \"Show the Bog\" is on).",
                    "Archive", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;
                await RunOp(ids, "Archived", (db, row) =>
                {
                    var relDir = Path.GetDirectoryName(row.RelPath) ?? "";
                    var destDir = Path.Combine(archiveBase, relDir);
                    Directory.CreateDirectory(destDir);
                    var dest = FileOps.UniqueDestination(destDir, row.FileName);
                    FileOps.Move(row.AbsPath, dest);
                    db.SetArchived(row.Id, dest);
                });
                break;
            }
            case "optimize":
                await HandleOptimize(ids);
                break;
        }
    }

    // ---------------- optimize / downsample (T14) ----------------
    private async Task HandleOptimize(long[] ids)
    {
        using var dlg = new OptimizeForm(ids.Length, _settings.MaxDim);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int maxDim = dlg.MaxDim;

        string? dest = null;
        if (dlg.InPlace)
        {
            // The confirmed in-place path: a final, explicit, not-recoverable confirmation.
            var r = MessageBox.Show(this,
                $"Overwrite {ids.Length} ORIGINAL file(s) in place, downsampled to {maxDim}px?\n\n" +
                "This permanently replaces your originals and CANNOT be undone — they do NOT go to " +
                "the Recycle Bin.\n\nImages already within the limit are left untouched.",
                "Overwrite originals — cannot be undone",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.OK) return;
        }
        else
        {
            dest = PickExportFolder($"Choose a folder for the downsampled copy/copies of {ids.Length} image(s)");
            if (dest is null) return;
        }

        await RunOptimize(ids, maxDim, dlg.InPlace, dest);
    }

    /// <summary>Batch downsample on a background writer connection, then reload. Reports
    /// downsampled / already-small / failed. In-place rewrites refresh the row's signature so the
    /// thumbnail cache invalidates and dimensions stay correct.</summary>
    private async Task RunOptimize(long[] ids, int maxDim, bool inPlace, string? dest)
    {
        _busy = true;
        SetStatus(inPlace ? "Optimizing originals in place…" : "Saving downsampled copies…");
        int resized = 0, skipped = 0, failed = 0;
        try
        {
            await Task.Run(() =>
            {
                using var opDb = new LibraryDb(AppPaths.LibraryDbFile);
                foreach (var id in ids)
                {
                    try
                    {
                        var row = opDb.GetById(id);
                        if (row is null || !File.Exists(row.AbsPath)) { failed++; continue; }

                        OptimizeOutcome outcome;
                        if (inPlace)
                        {
                            outcome = ImageOptimizer.DownsampleInPlace(row.AbsPath, maxDim);
                            if (outcome == OptimizeOutcome.Resized)
                            {
                                var fi = new FileInfo(row.AbsPath);
                                var (w, h) = ImageOptimizer.ReadDimensions(row.AbsPath);
                                opDb.UpdateFileSig(id, fi.Length, fi.LastWriteTimeUtc.Ticks, w, h);
                            }
                        }
                        else
                        {
                            var destPath = FileOps.UniqueDestination(dest!, row.FileName);
                            outcome = ImageOptimizer.DownsampleToCopy(row.AbsPath, destPath, maxDim);
                        }

                        if (outcome == OptimizeOutcome.Failed) failed++;
                        else if (outcome == OptimizeOutcome.SkippedSmall) skipped++;
                        else resized++;
                    }
                    catch { failed++; }
                }
            });
            SetStatus($"Optimize done — {resized} downsampled, {skipped} already small" +
                      (failed > 0 ? $", {failed} failed" : "") +
                      $". {_db.ImageCount(includeArchived: false):n0} images.");
            _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
        }
        catch (Exception ex) { SetStatus("Optimize failed: " + ex.Message); }
        finally { _busy = false; }
    }

    /// <summary>Run a per-item op on a background thread with its OWN writer connection (WAL), then reload.</summary>
    private async Task RunOp(long[] ids, string verb, Action<LibraryDb, ImageRow> perItem)
    {
        _busy = true;
        SetStatus($"{verb}…");
        int done = 0, failed = 0;
        try
        {
            await Task.Run(() =>
            {
                using var opDb = new LibraryDb(AppPaths.LibraryDbFile);
                foreach (var id in ids)
                {
                    try { var row = opDb.GetById(id); if (row is null) { failed++; continue; } perItem(opDb, row); done++; }
                    catch { failed++; }
                }
            });
            SetStatus($"{verb} {done}" + (failed > 0 ? $", {failed} failed" : "") + $". {_db.ImageCount(includeArchived: false):n0} images.");
            _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
        }
        catch (Exception ex) { SetStatus($"{verb} failed: {ex.Message}"); }
        finally { _busy = false; }
    }

    // ---------------- Find Duplicates (perceptual dedup) ----------------
    /// <summary>Backfill any missing perceptual hashes, group duplicates, and post the grouped set
    /// to the gallery. Runs on a background writer connection (like file ops); progress marshals to
    /// the status bar via a UI-thread Progress.</summary>
    private async Task HandleDupes(bool near, int gen)
    {
        if (_busy) return;
        _busy = true;
        SetStatus(near ? "Scanning for duplicates + near-duplicates…" : "Scanning for duplicates…");
        var prog = new Progress<HarvestProgress>(p => SetStatus($"{p.Phase}… {p.Current:n0}/{p.Total:n0}"));
        try
        {
            string reply = await Task.Run(() =>
            {
                using var opDb = new LibraryDb(AppPaths.LibraryDbFile);
                opDb.BackfillPhashes(progress: prog);
                var groups = opDb.FindDuplicateGroups(near ? 3 : 0);
                return GalleryBridge.DupesReply(opDb, UrlFor, groups, gen);
            });
            _web.CoreWebView2?.PostWebMessageAsString(reply);
            SetStatus($"{_db.ImageCount(includeArchived: false):n0} images.");
        }
        catch (Exception ex) { SetStatus("Duplicate scan failed: " + ex.Message); }
        finally { _busy = false; }
    }

    private bool EnsureExportDir()
    {
        if (!string.IsNullOrEmpty(_settings.ExportDir) && Directory.Exists(_settings.ExportDir)) return true;
        var d = PickExportRoot("Choose your export folder (archives + copies go here)");
        if (d is null) return false;
        _settings.ExportDir = d; SettingsStore.Save(_settings);
        return true;
    }

    /// <summary>Pick a destination confined to the export tree. The export ROOT is established
    /// first (free pick / Settings); the per-op choice is then a subfolder within it.</summary>
    private string? PickExportFolder(string description)
    {
        if (!EnsureExportDir()) return null;
        using var dlg = new ExportPickerForm(_settings.ExportDir!, description);
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    /// <summary>One-time free pick of the export ROOT (any location). Per-op destinations are then
    /// confined to within it via <see cref="ExportPickerForm"/>.</summary>
    private string? PickExportRoot(string description)
    {
        using var dlg = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        if (!string.IsNullOrEmpty(_settings.ExportDir) && Directory.Exists(_settings.ExportDir)) dlg.SelectedPath = _settings.ExportDir;
        if (dlg.ShowDialog(this) != DialogResult.OK) return null;
        return dlg.SelectedPath;
    }

    /// <summary>Copy an embedded resource (matched by filename suffix) to a path on disk. Used to
    /// place the brand lockup beside the app page in the app.local virtual-host folder.</summary>
    private static void WriteEmbeddedAsset(string resourceSuffix, string destPath)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (name is null) return;
            using var s = asm.GetManifestResourceStream(name)!;
            using var fs = File.Create(destPath);
            s.CopyTo(fs);
        }
        catch { /* non-fatal — sidebar just shows the text brand */ }
    }

    private static string LoadTemplate()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("taghag.template.html", StringComparison.OrdinalIgnoreCase));
        if (name is null) return "<html><body style='background:#141018;color:#D8D0BF'>template missing</body></html>";
        using var s = asm.GetManifestResourceStream(name)!;
        using var rd = new StreamReader(s);
        return rd.ReadToEnd().Replace("__VERSION__", AppInfo.Version);
    }

    private void SetStatus(string s) => _statusLabel.Text = s;

    private void RestoreWindowBounds()
    {
        StartPosition = FormStartPosition.Manual;
        Width = _settings.WinW > 200 ? _settings.WinW : 1200;
        Height = _settings.WinH > 200 ? _settings.WinH : 800;
        if (_settings.WinX != int.MinValue && _settings.WinY != int.MinValue)
            Location = new Point(_settings.WinX, _settings.WinY);
        else StartPosition = FormStartPosition.CenterScreen;
        if (_settings.WinMaximized) WindowState = FormWindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        _settings.WinMaximized = WindowState == FormWindowState.Maximized;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WinW = b.Width; _settings.WinH = b.Height; _settings.WinX = b.X; _settings.WinY = b.Y;
        SettingsStore.Save(_settings);
    }
}
