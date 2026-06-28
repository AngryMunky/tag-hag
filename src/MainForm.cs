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
    private readonly bool _isFreshInstall;   // T37: true when no settings file existed before first Load
    private AppSettings _settings;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly ToolStrip _tb = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripStatusLabel _statusLabel = new("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly StatusStrip _status = new() { SizingGrip = false };

    private LibraryDb _db = null!;
    private GalleryBridge _bridge = null!;
    private ThumbnailService _thumbs = null!;
    private bool _busy;
    private int _optGen;                          // T30: generation token for the optimize job (drop stale replies)
    private CancellationTokenSource? _optCts;     // T30: cancels the running optimize job
    private CancellationTokenSource? _scanCts;    // T36/F26: cancels the running scan/import

    public MainForm()
    {
        // T37: capture fresh-install flag before Load creates the settings file, then inject store dir.
        _isFreshInstall = !File.Exists(AppPaths.SettingsFile);
        _settings = SettingsStore.Load();
        AppPaths.SetStoreDir(_settings.StoreDir);

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
        _tb.Items.Add(Btn("Optimize Library…", OpenOptimizeLibrary));   // T30 / F20
        _tb.Items.Add(new ToolStripSeparator());
        _tb.Items.Add(Btn("Civitai", OpenCivitai));
        _tb.Items.Add(Btn("Settings", async () => await OpenSettings()));
        _tb.Items.Add(Btn("About", ShowAbout));
    }

    private static ToolStripButton Btn(string text, Action onClick)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>About dialog: name / version / tagline + a clickable link to the public repo.</summary>
    private void ShowAbout()
    {
        using var dlg = new Form
        {
            Text = $"About {AppInfo.Name}",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(400, 180),
            BackColor = Color.FromArgb(0x14, 0x10, 0x18),
            ForeColor = Color.FromArgb(0xD8, 0xD0, 0xBF),
        };
        AppIcon.Apply(dlg);

        var title = new Label
        {
            Text = AppInfo.Name,
            AutoSize = true,
            Location = new Point(20, 18),
            ForeColor = Color.FromArgb(0xA4, 0xFF, 0x6A),
            Font = new Font(dlg.Font.FontFamily, 15f, FontStyle.Bold),
        };
        var ver = new Label
        {
            Text = $"v{AppInfo.Version}",
            AutoSize = true,
            Location = new Point(22, 52),
            ForeColor = Color.FromArgb(0x9A, 0x90, 0x86),
        };
        var tag = new Label { Text = AppInfo.Tagline, AutoSize = true, Location = new Point(22, 78) };
        var link = new LinkLabel
        {
            Text = AppInfo.RepoUrl,
            AutoSize = true,
            Location = new Point(22, 108),
            LinkColor = Color.FromArgb(0xA4, 0xFF, 0x6A),
            ActiveLinkColor = Color.FromArgb(0xC7, 0xA2, 0x52),
            VisitedLinkColor = Color.FromArgb(0xA4, 0xFF, 0x6A),
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        link.LinkClicked += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.RepoUrl) { UseShellExecute = true }); }
            catch { /* ignore launch failures */ }
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 26),
            Location = new Point(dlg.ClientSize.Width - 100, dlg.ClientSize.Height - 40),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(0xD8, 0xD0, 0xBF),
            BackColor = Color.FromArgb(0x2B, 0x2A, 0x33),
        };
        ok.FlatAppearance.BorderColor = Color.FromArgb(0x5B, 0x3B, 0x8C);

        dlg.Controls.AddRange(new Control[] { title, ver, tag, link, ok });
        dlg.AcceptButton = ok;
        dlg.ShowDialog(this);
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

        // T37: launch validation — if a configured StoreDir is unavailable, warn before any scan.
        if (!string.IsNullOrEmpty(_settings.StoreDir) && !Directory.Exists(_settings.StoreDir))
            ShowLaunchValidation();

        // T37: first-run step — shown once on a fresh install (settings file didn't exist before Load).
        if (_isFreshInstall)
            ShowFirstRunStep();
    }

    // T37: warn when the configured store dir is missing/unavailable at startup.
    private void ShowLaunchValidation()
    {
        var pick = MessageBox.Show(this,
            $"The configured library store location is unavailable:\n  {_settings.StoreDir}\n\n" +
            "Click OK to pick a new location, or Cancel to use the default (beside the app) for now.",
            "Library Store Unavailable", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (pick == DialogResult.OK)
        {
            using var fd = new FolderBrowserDialog { Description = "Choose a new library store folder" };
            if (fd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(fd.SelectedPath))
            {
                _settings.StoreDir = fd.SelectedPath;
                AppPaths.SetStoreDir(_settings.StoreDir);
                SettingsStore.Save(_settings);
                return;
            }
        }
        // Use default for now; breadcrumb (StoreDir) preserved in settings — not cleared.
        AppPaths.SetStoreDir(null);
    }

    // T37: first-run step — skippable "Where should your library live?" dialog (greenfield only).
    private void ShowFirstRunStep()
    {
        using var dlg = new Form
        {
            Text = "Welcome to The Tag Hag",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            ClientSize = new Size(460, 180),
            BackColor = Color.FromArgb(0x14, 0x10, 0x18),
            ForeColor = Color.FromArgb(0xD8, 0xD0, 0xBF),
            Font = new Font("Segoe UI", 9f),
        };
        AppIcon.Apply(dlg);

        dlg.Controls.Add(new Label { Text = "Where should your library live?", ForeColor = Color.FromArgb(0xA4, 0xFF, 0x6A), Font = new Font("Segoe UI", 11f, FontStyle.Bold), AutoSize = true, Location = new Point(16, 14) });
        dlg.Controls.Add(new Label { Text = "Tag Hag stores optimized and consolidated images in a managed folder.\nThe default is beside the app (portable). You can change this later in Settings.", ForeColor = Color.FromArgb(0x8A, 0x84, 0x95), Size = new Size(428, 40), Location = new Point(16, 48) });

        var chooseBtn = new Button { Text = "Choose a folder…", Location = new Point(16, 132), Width = 130, FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(0xD8, 0xD0, 0xBF), BackColor = Color.FromArgb(0x1B, 0x16, 0x22) };
        chooseBtn.FlatAppearance.BorderColor = Color.FromArgb(0x5B, 0x3B, 0x8C);
        chooseBtn.Click += (_, _) =>
        {
            using var fd = new FolderBrowserDialog { Description = "Choose the library store folder" };
            if (fd.ShowDialog(dlg) == DialogResult.OK && !string.IsNullOrEmpty(fd.SelectedPath))
            {
                _settings.StoreDir = fd.SelectedPath;
                AppPaths.SetStoreDir(_settings.StoreDir);
            }
            dlg.Close();
        };
        var defaultBtn = new Button { Text = "Use default (beside app)", Location = new Point(162, 132), Width = 168, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(0x14, 0x10, 0x18), BackColor = Color.FromArgb(0xA4, 0xFF, 0x6A) };
        defaultBtn.FlatAppearance.BorderSize = 0;

        dlg.AcceptButton = defaultBtn;
        dlg.Controls.Add(chooseBtn); dlg.Controls.Add(defaultBtn);
        dlg.ShowDialog(this);
        // Always save — creates the settings file so this dialog never shows again.
        SettingsStore.Save(_settings);
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
            // ---- v2.0 host-side write intercepts (Favorites/Notes/Collections). Fast single-writer
            //      ops on the UI _db connection, then push reload/cols so the gallery refreshes.
            if (type == "fav")
            {
                if (d.RootElement.TryGetProperty("id", out var idEl))
                {
                    var on = d.RootElement.TryGetProperty("on", out var onEl) && onEl.ValueKind == JsonValueKind.True;
                    _db.SetFavorite(idEl.GetInt64(), on);
                    _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");   // refresh grid + favorites count
                }
                return;
            }
            if (type == "note")
            {
                if (d.RootElement.TryGetProperty("id", out var idEl))
                {
                    var body = d.RootElement.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                    _db.SetNote(idEl.GetInt64(), body);   // auto-save-on-blur — silent, no reload
                }
                return;
            }
            if (type == "colcreate")
            {
                var name = d.RootElement.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                long? parentId = d.RootElement.TryGetProperty("parentId", out var pi) && pi.ValueKind == System.Text.Json.JsonValueKind.Number ? pi.GetInt64() : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var res = _db.CreateCollection(name, parentId);
                    if (res == -1) SetStatus($"A collection named \"{name.Trim()}\" already exists.");
                    else if (res == -2) SetStatus("Parent collection not found.");
                }
                PushCols();
                return;
            }
            if (type == "colrename")
            {
                if (d.RootElement.TryGetProperty("id", out var idEl))
                {
                    var name = d.RootElement.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(name) && !_db.RenameCollection(idEl.GetInt64(), name))
                        SetStatus($"Couldn't rename \"{name.Trim()}\" — a collection with that name may already exist.");
                    PushCols();
                }
                return;
            }
            if (type == "coldelete")
            {
                if (d.RootElement.TryGetProperty("id", out var idEl))
                {
                    _db.DeleteCollection(idEl.GetInt64());          // collection_items cascade away
                    PushCols();
                    _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
                }
                return;
            }
            if (type == "coladd" || type == "colremove")
            {
                if (d.RootElement.TryGetProperty("collectionId", out var cidEl))
                {
                    var cid = cidEl.GetInt64();
                    var ids = d.RootElement.TryGetProperty("ids", out var a) && a.ValueKind == JsonValueKind.Array
                        ? a.EnumerateArray().Select(x => x.GetInt64()).ToArray() : Array.Empty<long>();
                    if (type == "coladd") _db.AddToCollection(cid, ids); else _db.RemoveFromCollection(cid, ids);
                    PushCols();
                    _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
                }
                return;
            }
            if (type == "autotag")
            {
                if (d.RootElement.TryGetProperty("id", out var idEl))
                {
                    var gen = d.RootElement.TryGetProperty("gen", out var gg) && gg.ValueKind == JsonValueKind.Number ? gg.GetInt32() : 0;
                    _ = HandleAutotag(idEl.GetInt64(), gen);   // heavy KNN → background (HandleDupes pattern)
                }
                return;
            }
            // T30 / F20 Library Optimization (the same bridge contract the top-bar dialog drives). T39: mode.
            if (type == "optimizelib")
            {
                var scope = d.RootElement.TryGetProperty("scope", out var sc) ? sc.GetString() ?? "all" : "all";
                var maxDim = d.RootElement.TryGetProperty("maxDim", out var md) && md.ValueKind == JsonValueKind.Number ? md.GetInt32() : _settings.MaxDim;
                var gen = d.RootElement.TryGetProperty("gen", out var gg) && gg.ValueKind == JsonValueKind.Number ? gg.GetInt32() : 0;
                var mode = d.RootElement.TryGetProperty("mode", out var mv) && mv.GetString() == "MoveOnly"
                    ? OptimizeMode.MoveOnly : OptimizeMode.Downsample;
                long[]? oids = d.RootElement.TryGetProperty("ids", out var oa) && oa.ValueKind == JsonValueKind.Array
                    ? oa.EnumerateArray().Select(x => x.GetInt64()).ToArray() : null;
                _ = HandleOptimizeLibrary(scope, oids, maxDim, mode, gen);
                return;
            }
            if (type == "optimizecancel") { _optCts?.Cancel(); return; }
            if (type == "scancancel") { _scanCts?.Cancel(); return; }   // T36/F26: cancel the running scan
            // T33 / F24 folder rename: physically rename the folder, then repath every indexed row
            // under it in one tx (ids + user-state preserved). Disk move runs first, so a collision/
            // error leaves the DB untouched (no data loss). Silent (pushes {type:'reload'}).
            if (type == "folderrename")
            {
                var froot = d.RootElement.TryGetProperty("root", out var rr) ? rr.GetString() : null;
                var oldRel = d.RootElement.TryGetProperty("path", out var pp) ? pp.GetString() : null;
                var newName = d.RootElement.TryGetProperty("name", out var nn) ? nn.GetString()?.Trim() : null;
                HandleFolderRename(froot, oldRel, newName);
                return;
            }
            // T34 / F23 file-manager verbs (action bar). All host-side (file ops + DB), reply via reload.
            if (type == "moveto")
            {
                var kind = d.RootElement.TryGetProperty("targetKind", out var tk) ? tk.GetString() : null;
                var mids = d.RootElement.TryGetProperty("ids", out var ma) && ma.ValueKind == JsonValueKind.Array
                    ? ma.EnumerateArray().Select(x => x.GetInt64()).ToArray() : Array.Empty<long>();
                if (kind == "collection")
                {
                    if (d.RootElement.TryGetProperty("collectionId", out var cidEl) && mids.Length > 0)
                    {
                        _db.AddToCollection(cidEl.GetInt64(), mids);
                        PushCols();
                        _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
                    }
                }
                else // 'folder'
                {
                    var mroot = d.RootElement.TryGetProperty("root", out var mr) ? mr.GetString() : null;
                    var mpath = d.RootElement.TryGetProperty("path", out var mp) ? mp.GetString() ?? "" : "";
                    _ = HandleMoveToFolder(mids, mroot, mpath);
                }
                return;
            }
            if (type == "rename")
            {
                if (d.RootElement.TryGetProperty("id", out var ridEl))
                {
                    var rn = d.RootElement.TryGetProperty("name", out var rnm) ? rnm.GetString() : null;
                    HandleRename(ridEl.GetInt64(), rn);
                }
                return;
            }
            if (type == "newfolder")
            {
                var nroot = d.RootElement.TryGetProperty("root", out var nr) ? nr.GetString() : null;
                var nparent = d.RootElement.TryGetProperty("parent", out var np) ? np.GetString() ?? "" : "";
                var nfn = d.RootElement.TryGetProperty("name", out var nfm) ? nfm.GetString()?.Trim() : null;
                HandleNewFolder(nroot, nparent, nfn);
                return;
            }
            if (type == "favbulk")
            {
                var fids = d.RootElement.TryGetProperty("ids", out var fa) && fa.ValueKind == JsonValueKind.Array
                    ? fa.EnumerateArray().Select(x => x.GetInt64()).ToArray() : Array.Empty<long>();
                var on = d.RootElement.TryGetProperty("on", out var onEl) && onEl.ValueKind == JsonValueKind.True;
                if (fids.Length > 0)
                {
                    _db.SetFavoriteBulk(fids, on);
                    _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
                }
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

    // ---------------- settings (T16 / T37 / T39) ----------------
    private async Task OpenSettings()
    {
        var before = _settings.SourceRoots.ToList();
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var removed = before.Where(r => !dlg.SourceRoots.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        var added = dlg.SourceRoots.Where(r => !before.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();

        // T37: handle store location change (before saving the new values).
        var oldStore = _settings.StoreDir;
        var newStore = dlg.StoreDir;
        bool storeChanged = !string.Equals(oldStore ?? "", newStore ?? "", StringComparison.OrdinalIgnoreCase);
        if (storeChanged)
        {
            var choice = ShowStoreMovePrompt(oldStore, newStore);
            if (choice == StoreMoveChoice.Cancel) return;

            if (choice == StoreMoveChoice.MoveFiles)
            {
                // T38/T44: full relocation not yet implemented (absorbed into T44 Consolidate Library).
                MessageBox.Show(this,
                    "File relocation will be available when you run 'Consolidate Library' (coming soon).\n\n" +
                    "Using 'Only new images' instead — existing optimized files stay in the old location and remain indexed.",
                    "Relocation Coming Soon", MessageBoxButtons.OK, MessageBoxIcon.Information);
                choice = StoreMoveChoice.OnlyNew;
            }

            if (choice == StoreMoveChoice.OnlyNew)
            {
                // Push the RESOLVED old store to LegacyStoreRoots so its files stay scanned/served.
                // oldStore may be null when the user was on the beside-exe default; resolve it via
                // AppPaths.LibraryStoreDir (which still reflects the old path — SetStoreDir(newStore)
                // is called below, after this block). BUG-T37T39-02 fix.
                var resolvedOld = oldStore ?? AppPaths.LibraryStoreDir;
                if (!_settings.LegacyStoreRoots.Contains(resolvedOld, StringComparer.OrdinalIgnoreCase))
                    _settings.LegacyStoreRoots.Add(resolvedOld);
            }
        }

        _settings.SourceRoots = dlg.SourceRoots;
        _settings.ExportDir = dlg.ExportDir;
        _settings.StoreDir = newStore;       // T37
        _settings.DefaultMode = dlg.DefaultMode;  // T39
        _settings.MaxDim = dlg.MaxDim;
        _settings.ApiKey = dlg.ApiKey;   // encrypted via DPAPI on assignment
        AppPaths.SetStoreDir(newStore);   // T37: propagate immediately
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

    private enum StoreMoveChoice { MoveFiles, OnlyNew, Cancel }

    private StoreMoveChoice ShowStoreMovePrompt(string? oldStore, string? newStore)
    {
        var result = StoreMoveChoice.Cancel;
        using var dlg = new Form
        {
            Text = "Library Location Changed",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            ClientSize = new Size(480, 210),
            BackColor = Color.FromArgb(0x14, 0x10, 0x18),
            ForeColor = Color.FromArgb(0xD8, 0xD0, 0xBF),
            Font = new Font("Segoe UI", 9f),
        };
        AppIcon.Apply(dlg);
        dlg.Controls.Add(new Label { Text = "Library Location Changed", ForeColor = Color.FromArgb(0xA4, 0xFF, 0x6A), Font = new Font("Segoe UI", 11f, FontStyle.Bold), AutoSize = true, Location = new Point(16, 12) });
        dlg.Controls.Add(new Label
        {
            Text = $"From: {oldStore ?? "(beside the app)"}\nTo:     {newStore ?? "(beside the app)"}\n\n" +
                   "What should happen to existing optimized images in the old location?",
            ForeColor = Color.FromArgb(0xD8, 0xD0, 0xBF), AutoSize = true, Location = new Point(16, 44)
        });

        Button Btn(string text, int x) { var b = new Button { Text = text, Location = new Point(x, 166), Width = 100, FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(0xD8, 0xD0, 0xBF), BackColor = Color.FromArgb(0x1B, 0x16, 0x22) }; b.FlatAppearance.BorderColor = Color.FromArgb(0x3A, 0x37, 0x44); return b; }
        var move = Btn("Move them", 16); move.Click += (_, _) => { result = StoreMoveChoice.MoveFiles; dlg.Close(); };
        var only = Btn("Only new images", 124); only.Width = 130; only.Click += (_, _) => { result = StoreMoveChoice.OnlyNew; dlg.Close(); };
        var cancel = Btn("Cancel", 370); cancel.Click += (_, _) => { result = StoreMoveChoice.Cancel; dlg.Close(); };

        dlg.Controls.Add(move); dlg.Controls.Add(only); dlg.Controls.Add(cancel);
        dlg.ShowDialog(this);
        return result;
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
        // v2.1: the Tag Hag-managed store is always a scanned (managed) root, so optimized images
        // moved into it stay indexed. It's implicit — not shown in Settings' source list — so it's
        // added here at scan time rather than persisted to SourceRoots. Record it in meta too.
        var storeRoot = AppPaths.EnsureLibraryStore();
        if (!roots.Contains(storeRoot, StringComparer.OrdinalIgnoreCase)) roots.Add(storeRoot);
        _db.SetLibraryStoreRoot(storeRoot);
        // T37: also scan any legacy store roots (files left behind by "Only new images" relocation).
        foreach (var leg in _settings.LegacyStoreRoots)
            if (!string.IsNullOrWhiteSpace(leg) && Directory.Exists(leg) &&
                !roots.Contains(leg, StringComparer.OrdinalIgnoreCase)) roots.Add(leg);
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        int lastRead = 0, lastTotal = 0;   // last determinate tick, for an honest "canceled at n/N"
        ScanResult r = default;
        // F26/T36: push real 0–100% progress to the centered overlay (Total==0 ⇒ indeterminate) AND keep
        // the compact status-bar echo. Progress<T> built on the UI thread marshals back to it.
        var prog = new Progress<HarvestProgress>(p =>
        {
            if (p.Total > 0) { lastRead = p.Current; lastTotal = p.Total; }
            _web.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "progress", phase = p.Phase, current = p.Current, total = p.Total }));
            SetStatus($"{p.Phase}… {p.Current:n0}" + (p.Total > 0 ? $"/{p.Total:n0}" : ""));
        });
        try
        {
            await Task.Run(() =>
            {
                using var scanDb = new LibraryDb(AppPaths.LibraryDbFile); // own writer connection (WAL)
                var scanner = new LocalScanner(scanDb);
                r = scanner.Scan(roots, ct, prog);
            }, ct);
            var relinkNote = r.ReLinked > 0 || r.Unmatched > 0
                ? $" ↺ {r.ReLinked} re-linked" + (r.Unmatched > 0 ? $", {r.Unmatched} ambiguous (kept as new)" : "") + "."
                : "";
            SetStatus($"Scan complete — +{r.Added} added, {r.Updated} updated, {r.Unchanged} unchanged, {r.Removed} removed, {r.Failed} failed.{relinkNote} " +
                      $"{_db.ImageCount(includeArchived: false):n0} images.");
            _web.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "scandone", added = r.Added, updated = r.Updated, removed = r.Removed, failed = r.Failed, reLinked = r.ReLinked, unmatched = r.Unmatched, canceled = false }));
            _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
        }
        catch (OperationCanceledException)
        {
            // The upsert is one atomic batch AFTER all reads, so a cancel leaves the library unchanged.
            SetStatus($"Scan canceled — stopped at {lastRead:n0}/{lastTotal:n0}; library unchanged.");
            _web.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "scandone", canceled = true, read = lastRead, total = lastTotal }));
        }
        catch (Exception ex)
        {
            SetStatus("Scan failed: " + ex.Message);
            _web.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "scandone", canceled = true, read = lastRead, total = lastTotal, error = true }));
        }
        finally { _busy = false; }
    }

    /// <summary>T33/F24: rename a physical folder (and re-path every indexed image under it). The disk
    /// rename runs FIRST (throws on a name collision → DB untouched, no data loss); then RepathFolder
    /// rewrites the rows' paths in one transaction, preserving ids + all user-state. Guarded by _busy so
    /// it never races a scan/optimize (both are writers). Runs on the main writer connection like the
    /// other host-side mutations (fav/note/collection).</summary>
    private void HandleFolderRename(string? root, string? oldRelDir, string? newName)
    {
        if (_busy) { SetStatus("Busy — finish the current operation first."); return; }
        if (string.IsNullOrEmpty(root) || oldRelDir is null || string.IsNullOrWhiteSpace(newName))
        { SetStatus("Folder rename: missing target."); return; }
        var oldRel = oldRelDir.TrimEnd('\\');
        if (oldRel.Length == 0) { SetStatus("Can't rename a source root here — change it in Settings."); return; }
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName is "." or "..")
        { SetStatus("Invalid folder name."); return; }

        var parent = Path.GetDirectoryName(oldRel) ?? "";          // rel parent ("" = top-level under the root)
        var newRel = parent.Length == 0 ? newName : Path.Combine(parent, newName);
        if (string.Equals(oldRel, newRel, StringComparison.Ordinal)) return; // no change
        var oldAbs = Path.GetFullPath(Path.Combine(root, oldRel));
        var newAbs = Path.GetFullPath(Path.Combine(root, newRel));
        try { FileOps.MoveFolder(oldAbs, newAbs); }               // disk first — throws on collision (no DB change)
        catch (Exception ex) { SetStatus("Folder rename failed: " + ex.Message); return; }
        try
        {
            var n = _db.RepathFolder(root, oldRel, newRel);        // then the index, one tx
            SetStatus($"Renamed folder to \"{newName}\" — {n:n0} image(s) re-pathed.");
            _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
        }
        catch (Exception ex)
        {
            // The disk rename already succeeded but the index update failed — undo the disk move so the
            // folder + index stay in lockstep. Otherwise the next scan would prune the old rows and re-add
            // the moved files as blank new rows, detaching their favorite/note/tag/collection state.
            try { FileOps.MoveFolder(newAbs, oldAbs); SetStatus("Folder rename failed (rolled back): " + ex.Message); }
            catch (Exception rb) { SetStatus($"Folder rename failed AND rollback failed — run Scan to re-link. ({ex.Message}; {rb.Message})"); }
        }
    }

    /// <summary>T34/F23: move the selected images into a folder under <paramref name="root"/> +
    /// <paramref name="relDir"/> — a physical move (FileOps.Move, collision-suffixed) + RepathRow, so each
    /// image keeps its id and all user-state. This is an IN-LIBRARY relocate, NOT the export-move that
    /// prunes. The destination dir is created if missing, so the picker's "＋ New folder…" works in one step.</summary>
    private async Task HandleMoveToFolder(long[] ids, string? root, string relDir)
    {
        if (_busy || ids.Length == 0) return;
        if (string.IsNullOrEmpty(root)) { SetStatus("Move: no target folder."); return; }
        var destDir = Path.GetFullPath(Path.Combine(root, relDir ?? ""));
        if (!IsUnderRoot(root, destDir)) { SetStatus("Move: invalid target folder."); return; }   // dest must stay under the root
        try { Directory.CreateDirectory(destDir); }
        catch (Exception ex) { SetStatus("Move failed: " + ex.Message); return; }
        await RunOp(ids, "Moved", (db, row) =>
        {
            if (string.Equals(Path.GetDirectoryName(row.AbsPath), destDir, StringComparison.OrdinalIgnoreCase)) return; // already there
            var dest = FileOps.UniqueDestination(destDir, row.FileName);
            FileOps.Move(row.AbsPath, dest);
            try { db.RepathRow(row.Id, dest, root); }   // keeps id → favorite/notes/tags/collections/optimized/archived survive
            catch { try { FileOps.Move(dest, row.AbsPath); } catch { } throw; }   // index update failed → undo the move (lockstep); RunOp records it as a failure
        });
    }

    /// <summary>T34/F23: rename one image's file in place (keeps its folder). Disk rename FIRST (collision
    /// → status, DB untouched), then RenameRow (file_name + path columns; id preserved → all user-state
    /// survives), with a best-effort rollback if the DB update throws.</summary>
    private void HandleRename(long id, string? newName)
    {
        if (_busy) { SetStatus("Busy — finish the current operation first."); return; }
        var row = _db.GetById(id);
        if (row is null) { SetStatus("Rename: image not found."); return; }
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) { SetStatus("Rename: empty name."); return; }
        if (Path.GetExtension(newName).Length == 0) newName += row.Ext;   // keep the original extension
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { SetStatus("Invalid file name."); return; }
        var dir = Path.GetDirectoryName(row.AbsPath) ?? row.SourceRoot;
        var newAbs = Path.GetFullPath(Path.Combine(dir, newName));
        if (string.Equals(newAbs, row.AbsPath, StringComparison.OrdinalIgnoreCase)) return; // no change
        if (File.Exists(newAbs)) { SetStatus($"A file named \"{newName}\" already exists here."); return; }
        try { FileOps.Move(row.AbsPath, newAbs); }
        catch (Exception ex) { SetStatus("Rename failed: " + ex.Message); return; }
        try
        {
            _db.RenameRow(id, newAbs, row.SourceRoot);
            SetStatus($"Renamed to \"{newName}\".");
            _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
        }
        catch (Exception ex)
        {
            try { FileOps.Move(newAbs, row.AbsPath); SetStatus("Rename failed (rolled back): " + ex.Message); }
            catch (Exception rb) { SetStatus($"Rename failed AND rollback failed — run Scan to re-link. ({ex.Message}; {rb.Message})"); }
        }
    }

    /// <summary>T34/F23: create an empty folder under <paramref name="root"/>/<paramref name="parentRel"/>
    /// (sidebar Folders ＋). The (image-derived) tree only shows folders that hold images, so a brand-new
    /// empty folder appears once images are moved into it — the Move-to picker's "＋ New folder…" does the
    /// create + move in one step.</summary>
    private void HandleNewFolder(string? root, string parentRel, string? name)
    {
        if (string.IsNullOrEmpty(root)) { SetStatus("New folder: scan a source folder first."); return; }
        name = (name ?? "").Trim();
        if (name.Length == 0) { SetStatus("New folder: empty name."); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..") { SetStatus("Invalid folder name."); return; }
        var dir = Path.GetFullPath(Path.Combine(root, parentRel ?? "", name));
        if (!IsUnderRoot(root, dir)) { SetStatus("Invalid folder location."); return; }   // stay under the root (defends a crafted parent path)
        try
        {
            if (Directory.Exists(dir)) { SetStatus($"A folder named \"{name}\" already exists there."); return; }
            Directory.CreateDirectory(dir);
            SetStatus($"Created folder \"{name}\". Move images into it — it appears in the tree once it holds images.");
        }
        catch (Exception ex) { SetStatus("New folder failed: " + ex.Message); }
    }

    /// <summary>True if <paramref name="dir"/> resolves to <paramref name="root"/> itself or a descendant
    /// — guards the folder paths that cross the JS bridge (move / new-folder targets) against traversal.</summary>
    private static bool IsUnderRoot(string root, string dir)
    {
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
        var d = Path.GetFullPath(dir).TrimEnd('\\', '/') + "\\";
        return d.StartsWith(r, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>T27 Auto-Tag: backfill hashes on demand, find similar neighbors that have user tags,
    /// and reply with vote-ranked suggestions (suggest-only — no tags are written here). Mirrors the
    /// HandleDupes background template; the gen token lets the client drop a superseded run.</summary>
    private async Task HandleAutotag(long id, int gen)
    {
        if (_busy) return;
        _busy = true;
        SetStatus("Finding visually similar images…");
        var prog = new Progress<HarvestProgress>(p => SetStatus($"{p.Phase}… {p.Current:n0}/{p.Total:n0}"));
        try
        {
            string reply = await Task.Run(() =>
            {
                using var opDb = new LibraryDb(AppPaths.LibraryDbFile);
                opDb.BackfillPhashes(progress: prog);                 // hash the target + any neighbors on demand
                var res = opDb.SuggestTagsByPhash(id, 3, 20);         // suggest-only KNN
                return GalleryBridge.AutotagReply(opDb, UrlFor, id, res, gen);
            });
            _web.CoreWebView2?.PostWebMessageAsString(reply);
            SetStatus($"{_db.ImageCount(includeArchived: false):n0} images.");
        }
        catch (Exception ex) { SetStatus("Auto-tag failed: " + ex.Message); }
        finally { _busy = false; }
    }

    // ---------------- Library Optimization (T30 / F20) ----------------

    /// <summary>Top-bar "Optimize / Consolidate Library…": preview the whole-library tally (both
    /// SourceFolders and Collections axes), confirm via the dialog, then run the job in the background.
    /// T39: mode defaults from Settings; T44: Organize-by radio + tie-resolution + skip-uncollected.</summary>
    private void OpenOptimizeLibrary()
    {
        if (_busy) { SetStatus("Busy — finish the current operation first."); return; }

        // SourceFolders preview (default mode summary line)
        var sfPrev = _db.OptimizePreview("all", _settings.MaxDim, null, _settings.DefaultMode);

        // Collections-mode eligible ids (includes already-optimized for potential relocation)
        var tree = _db.CollectionTree();
        var colEligIds = _db.OptimizeEligibleIds("all", _settings.MaxDim, null, _settings.DefaultMode, OrganizeBy.Collections);
        var colPrev   = _db.OptimizePreview("all", _settings.MaxDim, null, _settings.DefaultMode, OrganizeBy.Collections);

        if (sfPrev.Count == 0 && colEligIds.Count == 0)
        { SetStatus("Nothing to optimize — every image is already optimized or within the size target."); return; }

        // Build collection breakdown for the dialog stats line
        var depthMap   = LibraryOptimizer.BuildDepthMap(tree);
        var nodeMap    = LibraryOptimizer.BuildNodeMap(tree);
        var memberMap  = _db.GetCollectionMemberships(colEligIds);
        int inColl     = memberMap.Count;
        int uncoll     = colEligIds.Count - inColl;
        var tieCands   = BuildTieCandidates(colEligIds, memberMap, depthMap, nodeMap);

        int count   = sfPrev.Count > 0 ? sfPrev.Count : colEligIds.Count;
        long bytes  = sfPrev.Bytes  > 0 ? sfPrev.Bytes  : colPrev.Bytes;

        using var dlg = new OptimizeLibraryForm(count, bytes, _settings.MaxDim, _settings.DefaultMode,
            OrganizeBy.SourceFolders, inColl, uncoll, tieCands, tree);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _ = HandleOptimizeLibrary("all", null, dlg.MaxDim, dlg.Mode, ++_optGen,
            dlg.OrganizeBy, dlg.SkipUncollected, dlg.TieOverrides, tree);
    }

    /// <summary>Background optimize job: resample or relocate each eligible image into the managed store,
    /// MarkOptimized (keeps the id + all user-state), and (Downsample only) recycle the original after
    /// verification. T39: mode param. T44: organizeBy/skipUncollected/tieOverrides/collectionTree.</summary>
    private async Task HandleOptimizeLibrary(
        string scope, long[]? ids, int maxDim, OptimizeMode mode, int gen,
        OrganizeBy organizeBy = OrganizeBy.SourceFolders,
        bool skipUncollected = false,
        IReadOnlyDictionary<long, long>? tieOverrides = null,
        IReadOnlyList<CollectionNode>? collectionTree = null)
    {
        if (_busy) return;
        if (scope == "selection" && (ids is null || ids.Length == 0)) { SetStatus("No images selected to optimize."); return; }
        _busy = true;
        _optCts?.Dispose();
        _optCts = new CancellationTokenSource();
        var ct = _optCts.Token;
        bool isConsolidate = organizeBy == OrganizeBy.Collections || mode == OptimizeMode.MoveOnly;
        var verb = isConsolidate ? "Consolidating" : "Optimizing";
        SetStatus($"{verb} library…");
        var prog = new Progress<HarvestProgress>(p => SetStatus($"{p.Phase}… {p.Current:n0}/{p.Total:n0}"));
        try
        {
            var res = await Task.Run(() =>
            {
                using var opDb = new LibraryDb(AppPaths.LibraryDbFile);
                var eligible = opDb.OptimizeEligibleIds(scope, maxDim, ids, mode, organizeBy);
                return LibraryOptimizer.Run(opDb, eligible, maxDim, mode, organizeBy,
                    tieOverrides, skipUncollected, collectionTree, _thumbs, prog, ct);
            }, ct);
            _web.CoreWebView2?.PostWebMessageAsString(GalleryBridge.OptDoneReply(gen, res.Optimized, res.Skipped, res.Failed, res.FreedBytes, res.RecycleFailed));
            _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
            var pastTense = organizeBy == OrganizeBy.Collections ? "Consolidated" :
                            mode == OptimizeMode.MoveOnly ? "Moved" : "Optimized";
            SetStatus($"{pastTense} {res.Optimized:n0}, skipped {res.Skipped:n0}" + (res.Failed > 0 ? $", {res.Failed:n0} failed" : "") +
                      (res.RecycleFailed > 0 ? $", {res.RecycleFailed:n0} original(s) not recycled" : "") +
                      (res.FreedBytes > 0 ? $". Freed ~{HumanBytes(res.FreedBytes)}." : ".") +
                      $" {_db.ImageCount(includeArchived: false):n0} images.");
        }
        catch (OperationCanceledException)
        {
            SetStatus(isConsolidate ? "Consolidation cancelled." : "Optimize cancelled.");
            _web.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reload\"}");
        }
        catch (Exception ex) { SetStatus($"{verb} failed: " + ex.Message); }
        finally { _busy = false; }
    }

    /// <summary>T44: Find images in <paramref name="eligibleIds"/> whose top-depth collection memberships
    /// are tied (two or more collections at the same maximum depth). Used to pre-populate the
    /// [Review ties…] badge count and seed <see cref="ReviewTiesForm"/>.</summary>
    private List<TieCandidate> BuildTieCandidates(
        IReadOnlyList<long> eligibleIds,
        Dictionary<long, List<long>> memberMap,
        Dictionary<long, int> depthMap,
        Dictionary<long, CollectionNode> nodeMap)
    {
        var result = new List<TieCandidate>();
        foreach (var id in eligibleIds)
        {
            if (!memberMap.TryGetValue(id, out var memberOf) || memberOf.Count < 2) continue;
            int maxD = memberOf.Max(c => depthMap.TryGetValue(c, out var d) ? d : 0);
            var tied = memberOf.Where(c => (depthMap.TryGetValue(c, out var d2) ? d2 : 0) == maxD).ToList();
            if (tied.Count < 2) continue;
            result.Add(new TieCandidate
            {
                ImageId  = id,
                FileName = _db.GetById(id)?.FileName ?? $"id={id}",
                Tied     = tied.Select(c => (c, nodeMap.TryGetValue(c, out var n) ? n.Name : $"id={c}", maxD)).ToList()
            });
        }
        return result;
    }

    private static string HumanBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
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

    /// <summary>Re-send the collections list to the gallery (sidebar + action-bar picker) after a
    /// collection mutation. Reuses the bridge's read handler so the JSON shape stays in one place.</summary>
    private void PushCols()
    {
        var r = _bridge.Handle("{\"type\":\"cols\"}");
        if (r is not null) _web.CoreWebView2?.PostWebMessageAsString(r);
    }

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
