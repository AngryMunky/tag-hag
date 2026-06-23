namespace TheTagHag;

/// <summary>
/// T16 — Settings dialog. Manage source folders (add/remove), the export folder, the default
/// downsample size, and the Civitai API key (masked; persisted DPAPI-encrypted via AppSettings).
/// Code-built to match the app's Dark Magic Pro styling. MainForm reads the result properties on OK
/// and prunes the library for any source folder that was removed.
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
    private readonly NumericUpDown _maxDim = new();
    private readonly TextBox _apiKey = new();

    public List<string> SourceRoots => _roots.Items.Cast<string>().ToList();
    public string? ExportDir => string.IsNullOrWhiteSpace(_export.Text) ? null : _export.Text.Trim();
    public int MaxDim => (int)_maxDim.Value;
    public string ApiKey => _apiKey.Text;

    public SettingsForm(AppSettings s)
    {
        Text = "Settings";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(520, 398);
        BackColor = Bg; ForeColor = Bone;
        Font = new Font("Segoe UI", 9f);

        var title = new Label { Text = "Settings", ForeColor = Acid, AutoSize = true, Location = new Point(16, 12), Font = new Font("Segoe UI", 11f, FontStyle.Bold) };

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

        var exportLabel = new Label { Text = "Export folder (copies / archives go here):", AutoSize = true, Location = new Point(16, 190) };
        _export.Location = new Point(16, 212); _export.Size = new Size(376, 24);
        _export.ReadOnly = true; _export.BackColor = Panel; _export.ForeColor = Bone; _export.BorderStyle = BorderStyle.FixedSingle;
        _export.Text = s.ExportDir ?? "";
        var browseBtn = MakeBtn("Browse…", new Point(400, 211));
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Choose the export folder" };
            if (!string.IsNullOrEmpty(_export.Text) && Directory.Exists(_export.Text)) dlg.SelectedPath = _export.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK) _export.Text = dlg.SelectedPath;
        };

        var dimLabel = new Label { Text = "Default downsample size (max px):", AutoSize = true, Location = new Point(16, 250) };
        _maxDim.Minimum = 256; _maxDim.Maximum = 8192; _maxDim.Increment = 128;
        _maxDim.Value = Math.Clamp(s.MaxDim, 256, 8192);
        _maxDim.Location = new Point(232, 247); _maxDim.Width = 90;
        _maxDim.BackColor = Panel; _maxDim.ForeColor = Bone; _maxDim.BorderStyle = BorderStyle.FixedSingle;

        var keyLabel = new Label { Text = "Civitai API key (for harvest mode):", AutoSize = true, Location = new Point(16, 286) };
        _apiKey.Location = new Point(16, 308); _apiKey.Size = new Size(376, 24);
        _apiKey.UseSystemPasswordChar = true; _apiKey.BackColor = Panel; _apiKey.ForeColor = Bone; _apiKey.BorderStyle = BorderStyle.FixedSingle;
        _apiKey.Text = s.ApiKey;
        var showKey = new CheckBox { Text = "Show", AutoSize = true, ForeColor = Mut, Location = new Point(400, 310) };
        showKey.CheckedChanged += (_, _) => _apiKey.UseSystemPasswordChar = !showKey.Checked;

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(328, 358), Width = 84, FlatStyle = FlatStyle.Flat, ForeColor = Bg, BackColor = Acid };
        save.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(420, 358), Width = 84, FlatStyle = FlatStyle.Flat, ForeColor = Bone, BackColor = Panel };
        cancel.FlatAppearance.BorderColor = Border;

        AcceptButton = save; CancelButton = cancel;
        Controls.AddRange(new Control[] { title, rootsLabel, _roots, addBtn, removeBtn, exportLabel, _export, browseBtn, dimLabel, _maxDim, keyLabel, _apiKey, showKey, save, cancel });
    }

    private static Button MakeBtn(string text, Point at)
    {
        var b = new Button { Text = text, Location = at, Width = 100, FlatStyle = FlatStyle.Flat, ForeColor = Bone, BackColor = Panel };
        b.FlatAppearance.BorderColor = Border;
        return b;
    }
}
