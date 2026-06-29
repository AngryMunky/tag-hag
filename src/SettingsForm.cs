namespace TheTagHag;

/// <summary>
/// T16 — Settings dialog. Manage source folders (add/remove), the export folder, the Civitai API key
/// (masked; persisted DPAPI-encrypted via AppSettings). T37 adds a Library section: configurable store
/// location, default consolidation mode (T39), and max downsample size. Code-built to match the app's
/// Dark Magic Pro styling. MainForm reads the result properties on OK.
/// </summary>
public sealed class SettingsForm : Form
{
    private static readonly Color Bg = Color.FromArgb(0x14, 0x10, 0x18);
    private static readonly Color Panel = Color.FromArgb(0x1B, 0x16, 0x22);
    private static readonly Color Bone = Color.FromArgb(0xD8, 0xD0, 0xBF);
    private static readonly Color Mut = Color.FromArgb(0x8A, 0x84, 0x95);
    private static readonly Color Acid = Color.FromArgb(0xA4, 0xFF, 0x6A);
    private static readonly Color Border = Color.FromArgb(0x3A, 0x37, 0x44);

    private readonly ListBox _roots = new();
    private readonly TextBox _export = new();
    private readonly TextBox _storeDir = new();
    private readonly RadioButton _modeDownsample = new();
    private readonly RadioButton _modeMoveOnly = new();
    private readonly NumericUpDown _maxDim = new();
    private readonly TextBox _apiKey = new();
    private readonly TextBox _wdModelPath = new();
    private readonly NumericUpDown _wdThreshold = new();

    public List<string> SourceRoots => _roots.Items.Cast<string>().ToList();
    public string? ExportDir => string.IsNullOrWhiteSpace(_export.Text) ? null : _export.Text.Trim();
    public string? StoreDir => string.IsNullOrWhiteSpace(_storeDir.Text) ? null : _storeDir.Text.Trim();
    public OptimizeMode DefaultMode => _modeMoveOnly.Checked ? OptimizeMode.MoveOnly : OptimizeMode.Downsample;
    public int MaxDim => (int)_maxDim.Value;
    public string ApiKey => _apiKey.Text;
    public string? WdModelPath => string.IsNullOrWhiteSpace(_wdModelPath.Text) ? null : _wdModelPath.Text.Trim();
    public float WdThreshold => (float)_wdThreshold.Value;

    public SettingsForm(AppSettings s)
    {
        Text = "Settings";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(520, 630);
        BackColor = Bg; ForeColor = Bone;
        Font = new Font("Segoe UI", 9f);

        var title = new Label { Text = "Settings", ForeColor = Acid, AutoSize = true, Location = new Point(16, 12), Font = new Font("Segoe UI", 11f, FontStyle.Bold) };

        // Source folders
        var rootsLabel = new Label { Text = "Source folders (scanned recursively):", AutoSize = true, Location = new Point(16, 46) };
        _roots.Location = new Point(16, 68); _roots.Size = new Size(376, 112);
        _roots.BackColor = Panel; _roots.ForeColor = Bone; _roots.BorderStyle = BorderStyle.FixedSingle;
        _roots.HorizontalScrollbar = true;
        _roots.Items.AddRange(s.SourceRoots.Cast<object>().ToArray());

        var addBtn = MakeBtn("Add…", new Point(400, 68));
        addBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Add a source folder" };
            if (dlg.ShowDialog(this) == DialogResult.OK &&
                !_roots.Items.Cast<string>().Any(r => string.Equals(r, dlg.SelectedPath, StringComparison.OrdinalIgnoreCase)))
                _roots.Items.Add(dlg.SelectedPath);
        };
        var removeBtn = MakeBtn("Remove", new Point(400, 102));
        removeBtn.Click += (_, _) => { if (_roots.SelectedIndex >= 0) _roots.Items.RemoveAt(_roots.SelectedIndex); };

        // Export folder
        var exportLabel = new Label { Text = "Export folder (copies / archives go here):", AutoSize = true, Location = new Point(16, 190) };
        _export.Location = new Point(16, 212); _export.Size = new Size(376, 24);
        _export.ReadOnly = true; _export.BackColor = Panel; _export.ForeColor = Bone; _export.BorderStyle = BorderStyle.FixedSingle;
        _export.Text = s.ExportDir ?? "";
        var browseExportBtn = MakeBtn("Browse…", new Point(400, 211));
        browseExportBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Choose the export folder" };
            if (!string.IsNullOrEmpty(_export.Text) && Directory.Exists(_export.Text)) dlg.SelectedPath = _export.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK) _export.Text = dlg.SelectedPath;
        };

        // Library section (T37)
        var libraryHeader = new Label { Text = "Library", ForeColor = Acid, AutoSize = true, Location = new Point(16, 250), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        var storeDirLabel = new Label { Text = "Store location:", AutoSize = true, Location = new Point(16, 270) };
        _storeDir.Location = new Point(16, 288); _storeDir.Size = new Size(376, 24);
        _storeDir.ReadOnly = true; _storeDir.BackColor = Panel; _storeDir.ForeColor = Bone; _storeDir.BorderStyle = BorderStyle.FixedSingle;
        _storeDir.Text = s.StoreDir ?? "";
        var browseStoreBtn = MakeBtn("Browse…", new Point(400, 287));
        browseStoreBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Choose the library store folder" };
            if (!string.IsNullOrEmpty(_storeDir.Text) && Directory.Exists(_storeDir.Text)) dlg.SelectedPath = _storeDir.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK) _storeDir.Text = dlg.SelectedPath;
        };
        var clearStoreBtn = MakeBtn("Clear (default)", new Point(400, 319));
        clearStoreBtn.Width = 112;
        clearStoreBtn.Click += (_, _) => _storeDir.Text = "";

        var modeLabel = new Label { Text = "Default mode:", AutoSize = true, Location = new Point(16, 323) };
        _modeDownsample.Text = "Downsample"; _modeDownsample.AutoSize = true; _modeDownsample.ForeColor = Bone; _modeDownsample.BackColor = Bg;
        _modeDownsample.Location = new Point(120, 321);
        _modeMoveOnly.Text = "Move only"; _modeMoveOnly.AutoSize = true; _modeMoveOnly.ForeColor = Bone; _modeMoveOnly.BackColor = Bg;
        _modeMoveOnly.Location = new Point(228, 321);
        _modeDownsample.Checked = s.DefaultMode == OptimizeMode.Downsample;
        _modeMoveOnly.Checked = s.DefaultMode == OptimizeMode.MoveOnly;

        var dimLabel = new Label { Text = "Max longest edge (px):", AutoSize = true, Location = new Point(16, 349) };
        _maxDim.Minimum = 256; _maxDim.Maximum = 8192; _maxDim.Increment = 128;
        _maxDim.Value = Math.Clamp(s.MaxDim, 256, 8192);
        _maxDim.Location = new Point(232, 346); _maxDim.Width = 90;
        _maxDim.BackColor = Panel; _maxDim.ForeColor = Bone; _maxDim.BorderStyle = BorderStyle.FixedSingle;

        // Civitai API key
        var keyLabel = new Label { Text = "Civitai API key (for harvest mode):", AutoSize = true, Location = new Point(16, 382) };
        _apiKey.Location = new Point(16, 404); _apiKey.Size = new Size(376, 24);
        _apiKey.UseSystemPasswordChar = true; _apiKey.BackColor = Panel; _apiKey.ForeColor = Bone; _apiKey.BorderStyle = BorderStyle.FixedSingle;
        _apiKey.Text = s.ApiKey;
        var showKey = new CheckBox { Text = "Show", AutoSize = true, ForeColor = Mut, Location = new Point(400, 406) };
        showKey.CheckedChanged += (_, _) => _apiKey.UseSystemPasswordChar = !showKey.Checked;

        // WD14 Tagger section (T50/F39)
        var wdHeader = new Label { Text = "WD14 Tagger", ForeColor = Acid, AutoSize = true, Location = new Point(16, 442), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        var wdModelLabel = new Label { Text = "Model file (.onnx):", AutoSize = true, Location = new Point(16, 462) };
        _wdModelPath.Location = new Point(16, 478); _wdModelPath.Size = new Size(272, 24);
        _wdModelPath.ReadOnly = true; _wdModelPath.BackColor = Panel; _wdModelPath.ForeColor = Bone; _wdModelPath.BorderStyle = BorderStyle.FixedSingle;
        _wdModelPath.Text = s.WdModelPath ?? "";
        var wdBrowseBtn = MakeBtn("Browse…", new Point(296, 477)); wdBrowseBtn.Width = 80;
        wdBrowseBtn.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Title = "Select WD14 ONNX model", Filter = "ONNX model (*.onnx)|*.onnx" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (Wd14Tagger.FindCsv(dlg.FileName) == null)
            {
                MessageBox.Show(this, "No tags CSV found beside the selected .onnx file.\nPlace selected_tags.csv in the same folder as the model.", "WD14 Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _wdModelPath.Text = dlg.FileName;
        };
        var wdFindBtn = MakeBtn("Find auto", new Point(382, 477)); wdFindBtn.Width = 88;
        wdFindBtn.Click += (_, _) =>
        {
            var found = Wd14Tagger.FindAutomatic();
            if (found is not null) _wdModelPath.Text = found;
            else MessageBox.Show(this, "No WD14 model found in known locations.\n(A1111 interrogate, ComfyUI clip_vision, exe directory)", "WD14 Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        var wdDownload = new LinkLabel { Text = "Download WD14 model (HuggingFace)", AutoSize = true, Location = new Point(16, 510), ForeColor = Color.FromArgb(0x8A, 0xB4, 0xF8) };
        wdDownload.LinkColor = wdDownload.ForeColor;
        wdDownload.LinkClicked += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://huggingface.co/SmilingWolf/wd-vit-large-tagger-v3") { UseShellExecute = true });
        var wdThreshLabel = new Label { Text = "Score threshold:", AutoSize = true, Location = new Point(16, 533) };
        _wdThreshold.Minimum = 0.01m; _wdThreshold.Maximum = 1.00m; _wdThreshold.Increment = 0.01m; _wdThreshold.DecimalPlaces = 2;
        _wdThreshold.Value = Math.Clamp((decimal)s.WdThreshold, 0.01m, 1.00m);
        _wdThreshold.Location = new Point(232, 530); _wdThreshold.Width = 90;
        _wdThreshold.BackColor = Panel; _wdThreshold.ForeColor = Bone; _wdThreshold.BorderStyle = BorderStyle.FixedSingle;
        var wdHint = new Label { Text = "(tags scoring at or above this threshold are applied; default 0.35)", AutoSize = true, Location = new Point(16, 554), ForeColor = Mut, Font = new Font("Segoe UI", 8f) };

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(328, 588), Width = 84, FlatStyle = FlatStyle.Flat, ForeColor = Bg, BackColor = Acid };
        save.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(420, 588), Width = 84, FlatStyle = FlatStyle.Flat, ForeColor = Bone, BackColor = Panel };
        cancel.FlatAppearance.BorderColor = Border;

        AcceptButton = save; CancelButton = cancel;
        Controls.AddRange(new Control[] {
            title, rootsLabel, _roots, addBtn, removeBtn,
            exportLabel, _export, browseExportBtn,
            libraryHeader, storeDirLabel, _storeDir, browseStoreBtn, clearStoreBtn,
            modeLabel, _modeDownsample, _modeMoveOnly,
            dimLabel, _maxDim,
            keyLabel, _apiKey, showKey,
            wdHeader, wdModelLabel, _wdModelPath, wdBrowseBtn, wdFindBtn, wdDownload,
            wdThreshLabel, _wdThreshold, wdHint,
            save, cancel });
    }

    private static Button MakeBtn(string text, Point at)
    {
        var b = new Button { Text = text, Location = at, Width = 100, FlatStyle = FlatStyle.Flat, ForeColor = Bone, BackColor = Panel };
        b.FlatAppearance.BorderColor = Border;
        return b;
    }
}
