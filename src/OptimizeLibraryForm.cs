namespace TheTagHag;

/// <summary>
/// T30 / F20 / T44 — the "Optimize / Consolidate Library" dialog. Confirms a whole-library run.
/// T39/F27c: Mode radio (Downsample | MoveOnly). T44/F31: Organize-by radio (Source folders | Collections)
/// with a live stats line, [Review ties…] button, and [Skip uncollected] checkbox.
/// Built in code; Dark Magic Pro palette.
/// </summary>
public sealed class OptimizeLibraryForm : Form
{
    private static readonly Color Bg     = Color.FromArgb(0x14, 0x10, 0x18);
    private static readonly Color Panel  = Color.FromArgb(0x1B, 0x16, 0x22);
    private static readonly Color Bone   = Color.FromArgb(0xD8, 0xD0, 0xBF);
    private static readonly Color Mut    = Color.FromArgb(0x8A, 0x84, 0x95);
    private static readonly Color Acid   = Color.FromArgb(0xA4, 0xFF, 0x6A);
    private static readonly Color Gold   = Color.FromArgb(0xC7, 0xA2, 0x52);
    private static readonly Color Border = Color.FromArgb(0x3A, 0x37, 0x44);

    private readonly NumericUpDown _dim       = new();
    private readonly RadioButton _modeDownsample = new();
    private readonly RadioButton _modeMoveOnly   = new();
    private readonly RadioButton _orgSource      = new();
    private readonly RadioButton _orgCollections = new();
    private readonly Label _dimLabel     = new();
    private readonly Label _destLabel    = new();
    private readonly Label _collStats    = new();
    private readonly Button _reviewTies;
    private readonly CheckBox _skipUncollected = new();
    private readonly Label _warn = new();
    private readonly Button _ok;

    private Dictionary<long, long> _tieOverrides = new();
    private readonly IReadOnlyList<TieCandidate> _tieCandidates;
    private readonly IReadOnlyList<CollectionNode> _collectionTree;
    private readonly int _inCollectionCount;
    private readonly int _uncollectedCount;
    private readonly int _tieCount;

    // ── Public outputs ──────────────────────────────────────────────────────

    /// <summary>Chosen longest-edge limit in pixels (Downsample mode only).</summary>
    public int MaxDim => (int)_dim.Value;

    /// <summary>T39: Chosen consolidation mode.</summary>
    public OptimizeMode Mode =>
        _modeMoveOnly.Checked ? OptimizeMode.MoveOnly : OptimizeMode.Downsample;

    /// <summary>T44: Chosen organize-by axis.</summary>
    public OrganizeBy OrganizeBy =>
        _orgCollections.Checked ? OrganizeBy.Collections : OrganizeBy.SourceFolders;

    /// <summary>T44: Whether uncollected images should be skipped entirely.</summary>
    public bool SkipUncollected => _skipUncollected.Checked;

    /// <summary>T44: imageId → chosen collection_id overrides from [Review ties…].</summary>
    public IReadOnlyDictionary<long, long> TieOverrides => _tieOverrides;

    // ── Constructor ─────────────────────────────────────────────────────────

    public OptimizeLibraryForm(
        int count, long estBytes,
        int defaultMaxDim = ImageOptimizer.DefaultMaxDim,
        OptimizeMode defaultMode = OptimizeMode.Downsample,
        OrganizeBy defaultOrganizeBy = OrganizeBy.SourceFolders,
        int inCollectionCount = 0, int uncollectedCount = 0,
        IReadOnlyList<TieCandidate>? tieCandidates = null,
        IReadOnlyList<CollectionNode>? collectionTree = null)
    {
        _inCollectionCount = inCollectionCount;
        _uncollectedCount  = uncollectedCount;
        _tieCandidates     = tieCandidates ?? Array.Empty<TieCandidate>();
        _collectionTree    = collectionTree ?? Array.Empty<CollectionNode>();
        _tieCount          = _tieCandidates.Count;

        Text = "Optimize Library";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize  = new Size(486, 420);
        BackColor = Bg; ForeColor = Bone;
        Font = new Font("Segoe UI", 9f);

        // Title
        var title = new Label
        {
            Text = "Optimize the library",
            ForeColor = Acid,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = true, Location = new Point(16, 14)
        };

        // Summary
        var summary = new Label
        {
            Text = $"{count:n0} image(s) eligible · {HumanBytes(estBytes)} currently on disk.",
            ForeColor = Bone, AutoSize = true, Location = new Point(16, 46)
        };

        // ── Mode radios ────────────────────────────────────────────────────
        var modeLabel = new Label { Text = "Mode:", AutoSize = true, Location = new Point(16, 80) };
        _modeDownsample.Text = "Downsample"; _modeDownsample.AutoSize = true;
        _modeDownsample.ForeColor = Bone; _modeDownsample.BackColor = Bg;
        _modeDownsample.Location = new Point(72, 78);
        _modeMoveOnly.Text = "Move only"; _modeMoveOnly.AutoSize = true;
        _modeMoveOnly.ForeColor = Bone; _modeMoveOnly.BackColor = Bg;
        _modeMoveOnly.Location = new Point(188, 78);
        _modeDownsample.Checked = defaultMode == OptimizeMode.Downsample;
        _modeMoveOnly.Checked   = defaultMode == OptimizeMode.MoveOnly;
        _modeDownsample.CheckedChanged += (_, _) => ApplyMode();
        _modeMoveOnly.CheckedChanged   += (_, _) => ApplyMode();

        // MaxDim (Downsample only)
        _dimLabel.Text = "Max longest edge (px):"; _dimLabel.AutoSize = true;
        _dimLabel.Location = new Point(16, 108);
        _dim.Minimum = 256; _dim.Maximum = 8192; _dim.Increment = 128;
        _dim.Value = Math.Clamp(defaultMaxDim, 256, 8192);
        _dim.Location = new Point(168, 105); _dim.Width = 90;
        _dim.BackColor = Panel; _dim.ForeColor = Bone; _dim.BorderStyle = BorderStyle.FixedSingle;

        // Dest label (MoveOnly)
        _destLabel.Text = "→ " + AppPaths.LibraryStoreDir;
        _destLabel.ForeColor = Mut; _destLabel.AutoSize = true; _destLabel.Location = new Point(16, 108);

        // ── Organize-by radios ─────────────────────────────────────────────
        var orgLabel = new Label { Text = "Organize by:", AutoSize = true, Location = new Point(16, 136) };
        _orgSource.Text = "Source folders"; _orgSource.AutoSize = true;
        _orgSource.ForeColor = Bone; _orgSource.BackColor = Bg;
        _orgSource.Location = new Point(100, 134);
        _orgCollections.Text = "Collections"; _orgCollections.AutoSize = true;
        _orgCollections.ForeColor = Bone; _orgCollections.BackColor = Bg;
        _orgCollections.Location = new Point(224, 134);
        _orgSource.Checked      = defaultOrganizeBy == OrganizeBy.SourceFolders;
        _orgCollections.Checked = defaultOrganizeBy == OrganizeBy.Collections;
        _orgSource.CheckedChanged      += (_, _) => ApplyOrg();
        _orgCollections.CheckedChanged += (_, _) => ApplyOrg();

        // ── Collections stats line ─────────────────────────────────────────
        _collStats.Text = BuildStatsText();
        _collStats.ForeColor = Mut; _collStats.AutoSize = true; _collStats.Location = new Point(16, 164);

        _reviewTies = new Button
        {
            Text = _tieCount == 0 ? "Review ties…" : $"Review {_tieCount} tie(s)…",
            Location = new Point(16, 186), Width = 130,
            FlatStyle = FlatStyle.Flat, ForeColor = Bone, BackColor = Panel,
            Enabled = _tieCount > 0
        };
        _reviewTies.FlatAppearance.BorderColor = Border;
        _reviewTies.Click += OnReviewTies;

        _skipUncollected.Text = "Skip uncollected"; _skipUncollected.AutoSize = true;
        _skipUncollected.ForeColor = Bone; _skipUncollected.BackColor = Bg;
        _skipUncollected.Location = new Point(158, 188);
        _skipUncollected.Checked = false;

        // ── Hint ───────────────────────────────────────────────────────────
        var hint = new Label
        {
            Text = "Downsample: resampled copies move into Tag Hag's managed library (same format — metadata preserved).\n" +
                   "Move only: full-resolution files are relocated into the managed library — no resample, no data lost.\n" +
                   "Collections: store mirrors your collection tree (<store>/collection/subcollection/file).",
            ForeColor = Mut, Size = new Size(454, 56), Location = new Point(16, 224)
        };

        // ── Warning ────────────────────────────────────────────────────────
        _warn.Text = "⚠  Downsample sends each original to the Recycle Bin after its copy is safely written.\n" +
                     "      Move only does not recycle — the file is relocated, not duplicated.";
        _warn.ForeColor = Gold; _warn.Size = new Size(454, 40); _warn.Location = new Point(16, 292);

        // ── Buttons ────────────────────────────────────────────────────────
        _ok = new Button
        {
            Text = "Optimize", DialogResult = DialogResult.OK,
            Location = new Point(294, 374), Width = 84,
            FlatStyle = FlatStyle.Flat, ForeColor = Bg, BackColor = Acid
        };
        _ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Location = new Point(386, 374), Width = 84,
            FlatStyle = FlatStyle.Flat, ForeColor = Bone, BackColor = Panel
        };
        cancel.FlatAppearance.BorderColor = Border;

        AcceptButton = _ok; CancelButton = cancel;
        Controls.AddRange(new Control[]
        {
            title, summary,
            modeLabel, _modeDownsample, _modeMoveOnly,
            _dimLabel, _dim, _destLabel,
            orgLabel, _orgSource, _orgCollections,
            _collStats, _reviewTies, _skipUncollected,
            hint, _warn, _ok, cancel
        });

        ApplyMode();
        ApplyOrg();
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void ApplyMode()
    {
        bool isMove = _modeMoveOnly.Checked;
        _dimLabel.Visible  = !isMove;
        _dim.Visible       = !isMove;
        _destLabel.Visible = isMove;
        Text    = isMove ? "Consolidate Library" : "Optimize Library";
        _ok.Text = isMove ? "Consolidate" : "Optimize";
        _warn.Visible = true;
    }

    private void ApplyOrg()
    {
        bool isColl = _orgCollections.Checked;
        _collStats.Visible       = isColl;
        _reviewTies.Visible      = isColl;
        _skipUncollected.Visible = isColl;
    }

    private void OnReviewTies(object? sender, EventArgs e)
    {
        if (_tieCandidates.Count == 0) return;
        using var dlg = new ReviewTiesForm(_tieCandidates, _collectionTree, _tieOverrides);
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _tieOverrides = dlg.Overrides;
    }

    private string BuildStatsText()
    {
        var parts = new List<string>();
        parts.Add($"{_inCollectionCount:n0} in collections");
        parts.Add($"{_uncollectedCount:n0} uncollected");
        if (_tieCount > 0) parts.Add($"{_tieCount} auto-resolved tie(s)");
        return string.Join(" · ", parts);
    }

    private static string HumanBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}
