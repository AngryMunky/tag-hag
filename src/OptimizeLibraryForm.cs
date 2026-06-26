namespace TheTagHag;

/// <summary>
/// T30 / F20 — the "Optimize Library" dialog (top-bar entry). Confirms a whole-library optimize:
/// resample images to a max longest-edge, move the resampled copies into Tag Hag's managed store,
/// and send the originals to the Recycle Bin. Distinct from the per-selection <see cref="OptimizeForm"/>
/// (T14, copy/in-place) — this is the v2.1 managed-store model (originals are recycled, recoverable).
/// Built in code to match MainForm's style. Dark Magic Pro.
/// </summary>
public sealed class OptimizeLibraryForm : Form
{
    private static readonly Color Bg = Color.FromArgb(0x14, 0x10, 0x18);
    private static readonly Color Panel = Color.FromArgb(0x1B, 0x16, 0x22);
    private static readonly Color Bone = Color.FromArgb(0xD8, 0xD0, 0xBF);
    private static readonly Color Mut = Color.FromArgb(0x8A, 0x84, 0x95);
    private static readonly Color Acid = Color.FromArgb(0xA4, 0xFF, 0x6A);
    private static readonly Color Gold = Color.FromArgb(0xC7, 0xA2, 0x52);

    private readonly NumericUpDown _dim = new();

    /// <summary>Chosen longest-edge limit in pixels.</summary>
    public int MaxDim => (int)_dim.Value;

    public OptimizeLibraryForm(int count, long estBytes, int defaultMaxDim = ImageOptimizer.DefaultMaxDim)
    {
        Text = "Optimize Library";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(486, 268);
        BackColor = Bg; ForeColor = Bone;
        Font = new Font("Segoe UI", 9f);

        var title = new Label
        {
            Text = "Optimize the library",
            ForeColor = Acid,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 14)
        };

        var summary = new Label
        {
            Text = $"{count:n0} image(s) eligible · {HumanBytes(estBytes)} currently on disk.",
            ForeColor = Bone, AutoSize = true, Location = new Point(16, 46)
        };

        var dimLabel = new Label { Text = "Max longest edge (px):", AutoSize = true, Location = new Point(16, 84) };
        _dim.Minimum = 256; _dim.Maximum = 8192; _dim.Increment = 128;
        _dim.Value = Math.Clamp(defaultMaxDim, 256, 8192);
        _dim.Location = new Point(168, 81); _dim.Width = 90;
        _dim.BackColor = Panel; _dim.ForeColor = Bone; _dim.BorderStyle = BorderStyle.FixedSingle;

        var hint = new Label
        {
            Text = "Resampled copies move into Tag Hag's managed library (same format — metadata preserved).\n" +
                   "Images already within this size are skipped (left where they are).",
            ForeColor = Mut, AutoSize = true, Location = new Point(16, 114)
        };

        var warn = new Label
        {
            Text = "⚠  Each original is sent to the Recycle Bin after its resampled copy is safely written.\n" +
                   "      You can restore originals from the Recycle Bin.",
            ForeColor = Gold, AutoSize = true, Location = new Point(16, 162)
        };

        var ok = new Button
        {
            Text = "Optimize", DialogResult = DialogResult.OK,
            Location = new Point(294, 222), Width = 84,
            FlatStyle = FlatStyle.Flat, ForeColor = Bg, BackColor = Acid
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Location = new Point(386, 222), Width = 84,
            FlatStyle = FlatStyle.Flat, ForeColor = Bone, BackColor = Panel
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(0x3A, 0x37, 0x44);

        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange(new Control[] { title, summary, dimLabel, _dim, hint, warn, ok, cancel });
    }

    private static string HumanBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}
