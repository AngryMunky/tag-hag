namespace TheTagHag;

/// <summary>
/// T44/F31 — modal dialog that lets the user resolve collection-membership ties. For each tied image a
/// row is shown: filename + a ComboBox listing the tied collections so the user can pick one. The caller
/// reads <see cref="Overrides"/> after DialogResult.OK to get the imageId → collectionId map.
/// Built in code; Dark Magic Pro palette.
/// </summary>
public sealed class ReviewTiesForm : Form
{
    private static readonly Color Bg     = Color.FromArgb(0x14, 0x10, 0x18);
    private static readonly Color Panel  = Color.FromArgb(0x1B, 0x16, 0x22);
    private static readonly Color Bone   = Color.FromArgb(0xD8, 0xD0, 0xBF);
    private static readonly Color Mut    = Color.FromArgb(0x8A, 0x84, 0x95);
    private static readonly Color Acid   = Color.FromArgb(0xA4, 0xFF, 0x6A);
    private static readonly Color Border = Color.FromArgb(0x3A, 0x37, 0x44);

    private readonly IReadOnlyList<TieCandidate> _candidates;
    private readonly List<ComboBox> _combos = new();

    /// <summary>After DialogResult.OK: imageId → user-chosen collectionId override.</summary>
    public Dictionary<long, long> Overrides { get; } = new();

    public ReviewTiesForm(
        IReadOnlyList<TieCandidate> candidates,
        IReadOnlyList<CollectionNode> _collectionTree,
        Dictionary<long, long> existingOverrides)
    {
        _candidates = candidates;

        Text = $"Review {candidates.Count} tie(s)";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        BackColor = Bg; ForeColor = Bone;
        Font = new Font("Segoe UI", 9f);

        // ── Scrollable panel ────────────────────────────────────────────────
        var scroll = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.None,
            Location = new Point(0, 0),
            BackColor = Bg
        };

        int rowY = 8;
        const int rowH = 30;
        const int labelW = 220;
        const int comboW = 220;
        const int marginX = 12;

        var header = new Label
        {
            Text = "For each tied image, choose which collection it should live under.",
            ForeColor = Mut, AutoSize = false,
            Size = new Size(460, 20),
            Location = new Point(marginX, rowY)
        };
        scroll.Controls.Add(header);
        rowY += 28;

        foreach (var cand in candidates)
        {
            var lbl = new Label
            {
                Text = cand.FileName,
                ForeColor = Bone,
                AutoSize = false,
                Size = new Size(labelW, 20),
                Location = new Point(marginX, rowY + 4),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Panel, ForeColor = Bone,
                Location = new Point(marginX + labelW + 8, rowY),
                Width = comboW
            };
            foreach (var (collId, name, depth) in cand.Tied)
                combo.Items.Add(new CollEntry(collId, name, depth));
            // Pre-select the existing override if present; otherwise the first item (lowest id = auto-resolved home).
            int preselect = 0;
            if (existingOverrides.TryGetValue(cand.ImageId, out var ov))
            {
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    if (((CollEntry)combo.Items[i]!).CollId == ov) { preselect = i; break; }
                }
            }
            combo.SelectedIndex = preselect;
            combo.Tag = cand.ImageId;

            scroll.Controls.Add(lbl);
            scroll.Controls.Add(combo);
            _combos.Add(combo);
            rowY += rowH;
        }

        int scrollH = Math.Min(rowY + 16, 440);
        scroll.Size = new Size(484, scrollH);

        // ── Buttons ────────────────────────────────────────────────────────
        var ok = new Button
        {
            Text = "Apply", DialogResult = DialogResult.OK,
            Location = new Point(294, scrollH + 10), Width = 84,
            FlatStyle = FlatStyle.Flat, ForeColor = Bg, BackColor = Acid
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += OnApply;

        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Location = new Point(386, scrollH + 10), Width = 84,
            FlatStyle = FlatStyle.Flat, ForeColor = Bone, BackColor = Panel
        };
        cancel.FlatAppearance.BorderColor = Border;

        ClientSize  = new Size(484, scrollH + 52);
        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange(new Control[] { scroll, ok, cancel });
    }

    private void OnApply(object? sender, EventArgs e)
    {
        Overrides.Clear();
        foreach (var combo in _combos)
        {
            if (combo.SelectedItem is CollEntry entry && combo.Tag is long imgId)
                Overrides[imgId] = entry.CollId;
        }
    }

    private sealed class CollEntry(long collId, string name, int depth)
    {
        public long CollId { get; } = collId;
        public override string ToString() =>
            depth > 0 ? $"{new string(' ', depth * 2)}{name}" : name;
    }
}
