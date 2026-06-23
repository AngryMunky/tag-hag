namespace TheTagHag;

/// <summary>
/// T14 — the Optimize (downsample) dialog. Picks the max longest-edge dimension and the mode:
/// save downsampled COPIES to a folder (default, safe) or OVERWRITE ORIGINALS in place. The
/// in-place option is the confirmed "behind a strong confirmation" path — it's red-accented and
/// reveals a not-recoverable warning here; MainForm shows a final modal confirm before any
/// original is touched. Built in code (no designer) to match MainForm's style. Dark Magic Pro.
/// </summary>
public sealed class OptimizeForm : Form
{
    private static readonly Color Bg = Color.FromArgb(0x14, 0x10, 0x18);
    private static readonly Color Panel = Color.FromArgb(0x1B, 0x16, 0x22);
    private static readonly Color Bone = Color.FromArgb(0xD8, 0xD0, 0xBF);
    private static readonly Color Mut = Color.FromArgb(0x8A, 0x84, 0x95);
    private static readonly Color Acid = Color.FromArgb(0xA4, 0xFF, 0x6A);
    private static readonly Color Red = Color.FromArgb(0xE0, 0x55, 0x40);

    private readonly NumericUpDown _dim = new();
    private readonly RadioButton _copy = new();
    private readonly RadioButton _inPlace = new();
    private readonly Label _warn = new();

    /// <summary>Chosen longest-edge limit in pixels.</summary>
    public int MaxDim => (int)_dim.Value;
    /// <summary>True if the user chose to overwrite originals in place.</summary>
    public bool InPlace => _inPlace.Checked;

    public OptimizeForm(int count, int defaultMaxDim = ImageOptimizer.DefaultMaxDim)
    {
        Text = "Optimize images";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(470, 282);
        BackColor = Bg; ForeColor = Bone;
        Font = new Font("Segoe UI", 9f);

        var title = new Label
        {
            Text = $"Downsample {count} selected image(s)",
            ForeColor = Acid,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 14)
        };

        var dimLabel = new Label { Text = "Max longest edge (px):", AutoSize = true, Location = new Point(16, 54) };
        _dim.Minimum = 256; _dim.Maximum = 8192; _dim.Increment = 128;
        _dim.Value = Math.Clamp(defaultMaxDim, 256, 8192);
        _dim.Location = new Point(168, 51); _dim.Width = 90;
        _dim.BackColor = Panel; _dim.ForeColor = Bone; _dim.BorderStyle = BorderStyle.FixedSingle;

        var hint = new Label
        {
            Text = "Images already within this size are skipped (copied unchanged).",
            ForeColor = Mut, AutoSize = true, Location = new Point(16, 82)
        };

        _copy.Text = "Save downsampled copies to a folder  —  originals untouched";
        _copy.ForeColor = Bone; _copy.AutoSize = true; _copy.Checked = true;
        _copy.Location = new Point(16, 116);

        _inPlace.Text = "Overwrite the originals in place";
        _inPlace.ForeColor = Red; _inPlace.AutoSize = true;
        _inPlace.Location = new Point(16, 144);

        _warn.Text = "⚠  This permanently replaces your original files and cannot be undone.\n" +
                     "      They do NOT go to the Recycle Bin.";
        _warn.ForeColor = Red; _warn.AutoSize = true; _warn.Visible = false;
        _warn.Location = new Point(38, 170);

        void Sync(object? _, EventArgs __) => _warn.Visible = _inPlace.Checked;
        _copy.CheckedChanged += Sync;
        _inPlace.CheckedChanged += Sync;

        var ok = new Button
        {
            Text = "Optimize", DialogResult = DialogResult.OK,
            Location = new Point(278, 236), Width = 84,
            FlatStyle = FlatStyle.Flat, ForeColor = Bg, BackColor = Acid
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Location = new Point(370, 236), Width = 84,
            FlatStyle = FlatStyle.Flat, ForeColor = Bone, BackColor = Panel
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(0x3A, 0x37, 0x44);

        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange(new Control[] { title, dimLabel, _dim, hint, _copy, _inPlace, _warn, ok, cancel });
    }
}
