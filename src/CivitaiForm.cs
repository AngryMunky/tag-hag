namespace TheTagHag;

/// <summary>
/// T17 — Civitai harvest (secondary, retain-only mode). Runs the ported <see cref="Harvester"/>
/// against the public feed and/or named collections, streaming the live log + progress. Harvested
/// PNGs (with embedded A1111 metadata) land in AppPaths.CivitaiDir; MainForm offers to index that
/// folder into the library afterward. Dark Magic Pro styling.
/// </summary>
public sealed class CivitaiForm : Form
{
    private static readonly Color Bg = Color.FromArgb(0x14, 0x10, 0x18);
    private static readonly Color Panel = Color.FromArgb(0x1B, 0x16, 0x22);
    private static readonly Color Bone = Color.FromArgb(0xD8, 0xD0, 0xBF);
    private static readonly Color Mut = Color.FromArgb(0x8A, 0x84, 0x95);
    private static readonly Color Acid = Color.FromArgb(0xA4, 0xFF, 0x6A);
    private static readonly Color Red = Color.FromArgb(0xE0, 0x55, 0x40);
    private static readonly Color Border = Color.FromArgb(0x3A, 0x37, 0x44);

    private readonly AppSettings _s;
    private readonly NumericUpDown _maxNew = new();
    private readonly NumericUpDown _likesMin = new();
    private readonly ComboBox _nsfw = new();
    private readonly CheckBox _feed = new();
    private readonly CheckBox _collections = new();
    private readonly TextBox _colNames = new();
    private readonly CheckBox _dryRun = new();
    private readonly Button _harvest, _stop;
    private readonly ProgressBar _bar = new();
    private readonly Label _status = new();
    private readonly TextBox _log = new();

    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>True if a LIVE harvest actually saved images (so MainForm offers to index them).</summary>
    public bool DidHarvest { get; private set; }

    public CivitaiForm(AppSettings settings)
    {
        _s = settings;
        Text = "Civitai harvest";
        AppIcon.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(560, 520);
        BackColor = Bg; ForeColor = Bone; Font = new Font("Segoe UI", 9f);

        var title = new Label { Text = "Civitai harvest", ForeColor = Acid, AutoSize = true, Location = new Point(16, 12), Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
        var hasKey = !string.IsNullOrWhiteSpace(_s.ApiKey);
        var keyStatus = new Label
        {
            Text = hasKey ? "API key: set ✓" : "No API key — set it in Settings first.",
            ForeColor = hasKey ? Mut : Red, AutoSize = true, Location = new Point(16, 40)
        };

        var maxLabel = new Label { Text = "Max new:", AutoSize = true, Location = new Point(16, 76) };
        _maxNew.Minimum = 1; _maxNew.Maximum = 5000; _maxNew.Value = Math.Clamp(_s.Harvest.MaxNew, 1, 5000);
        _maxNew.Location = new Point(96, 73); _maxNew.Width = 80; Style(_maxNew);
        var likesLabel = new Label { Text = "Min likes:", AutoSize = true, Location = new Point(200, 76) };
        _likesMin.Minimum = 0; _likesMin.Maximum = 100000; _likesMin.Value = Math.Clamp(_s.Harvest.LikesMin, 0, 100000);
        _likesMin.Location = new Point(280, 73); _likesMin.Width = 90; Style(_likesMin);
        var nsfwLabel = new Label { Text = "NSFW:", AutoSize = true, Location = new Point(394, 76) };
        _nsfw.DropDownStyle = ComboBoxStyle.DropDownList; _nsfw.Items.AddRange(new object[] { "None", "Soft", "Mature", "X" });
        _nsfw.SelectedItem = new[] { "None", "Soft", "Mature", "X" }.Contains(_s.Harvest.Nsfw) ? _s.Harvest.Nsfw : "X";
        _nsfw.Location = new Point(444, 73); _nsfw.Width = 90; Style(_nsfw);

        _feed.Text = "Harvest the public feed"; _feed.ForeColor = Bone; _feed.AutoSize = true; _feed.Checked = true; _feed.Location = new Point(16, 112);

        _collections.Text = "Harvest collections:"; _collections.ForeColor = Bone; _collections.AutoSize = true;
        _collections.Checked = !string.IsNullOrWhiteSpace(_s.Harvest.CollectionNames); _collections.Location = new Point(16, 140);
        _colNames.Text = _s.Harvest.CollectionNames; _colNames.Location = new Point(170, 138); _colNames.Width = 364; Style(_colNames);
        _colNames.PlaceholderText = "comma,separated,collection,names";

        _dryRun.Text = "Dry run (preview only — no downloads)"; _dryRun.ForeColor = Acid; _dryRun.AutoSize = true; _dryRun.Checked = true; _dryRun.Location = new Point(16, 172);

        _harvest = new Button { Text = "Harvest", Location = new Point(16, 204), Width = 100, FlatStyle = FlatStyle.Flat, ForeColor = Bg, BackColor = Acid };
        _harvest.FlatAppearance.BorderSize = 0; _harvest.Click += async (_, _) => await RunHarvest();
        _stop = new Button { Text = "Stop", Location = new Point(124, 204), Width = 84, FlatStyle = FlatStyle.Flat, ForeColor = Bone, BackColor = Panel, Enabled = false };
        _stop.FlatAppearance.BorderColor = Border; _stop.Click += (_, _) => _cts?.Cancel();
        var close = new Button { Text = "Close", Location = new Point(450, 204), Width = 84, FlatStyle = FlatStyle.Flat, ForeColor = Bone, BackColor = Panel };
        close.FlatAppearance.BorderColor = Border; close.Click += (_, _) => { if (!_busy) Close(); };

        _bar.Location = new Point(16, 244); _bar.Size = new Size(518, 14); _bar.Style = ProgressBarStyle.Continuous;
        _status.Text = "Ready."; _status.ForeColor = Mut; _status.AutoSize = true; _status.Location = new Point(16, 264);

        _log.Location = new Point(16, 288); _log.Size = new Size(518, 214); _log.Multiline = true; _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical; _log.BackColor = Color.FromArgb(0x0E, 0x0B, 0x13); _log.ForeColor = Color.FromArgb(0x9F, 0xE6, 0xC5);
        _log.BorderStyle = BorderStyle.FixedSingle; _log.Font = new Font("Consolas", 8.5f); _log.WordWrap = false;

        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; } };

        Controls.AddRange(new Control[] { title, keyStatus, maxLabel, _maxNew, likesLabel, _likesMin, nsfwLabel, _nsfw,
            _feed, _collections, _colNames, _dryRun, _harvest, _stop, close, _bar, _status, _log });
    }

    private static void Style(Control c) { c.BackColor = Panel; c.ForeColor = Bone; }

    private async Task RunHarvest()
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_s.ApiKey))
        {
            MessageBox.Show(this, "No Civitai API key set. Open Settings and paste your key first.", "Civitai", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_feed.Checked && !_collections.Checked)
        {
            MessageBox.Show(this, "Choose at least one source: the public feed and/or collections.", "Civitai", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Persist the chosen options.
        _s.Harvest.MaxNew = (int)_maxNew.Value;
        _s.Harvest.LikesMin = (int)_likesMin.Value;
        _s.Harvest.Nsfw = _nsfw.SelectedItem?.ToString() ?? "X";
        _s.Harvest.CollectionNames = _collections.Checked ? _colNames.Text.Trim() : "";
        SettingsStore.Save(_s);

        var o = _s.Harvest;
        o.DryRun = _dryRun.Checked;
        o.SkipFeed = !_feed.Checked;
        o.Collections = _collections.Checked && !string.IsNullOrWhiteSpace(_colNames.Text);

        _busy = true; _harvest.Enabled = false; _stop.Enabled = true;
        _bar.Style = ProgressBarStyle.Marquee;
        _cts = new CancellationTokenSource();
        try
        {
            var (feed, cols) = await new Harvester(_s, AppendLog).Run(o, _cts.Token, OnProgress);
            if (!o.DryRun && (feed + cols) > 0) DidHarvest = true;
            _status.Text = o.DryRun ? $"Dry run complete — {feed + cols} would harvest." : $"Done — {feed} feed + {cols} collections.";
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; AppendLog(new("INFO", "Harvest stopped by user.")); }
        catch (Exception ex) { _status.Text = "Failed."; AppendLog(new("ERROR", ex.Message)); }
        finally { _busy = false; _harvest.Enabled = true; _stop.Enabled = false; _bar.Style = ProgressBarStyle.Continuous; }
    }

    private void OnProgress(HarvestProgress p)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (p.Total > 0) { _bar.Style = ProgressBarStyle.Continuous; _bar.Maximum = p.Total; _bar.Value = Math.Min(Math.Max(p.Current, 0), p.Total); }
                else _bar.Style = ProgressBarStyle.Marquee;
                _status.Text = $"{p.Phase}: {p.Current}/{p.Total}";
            });
        }
        catch { }
    }

    private void AppendLog(HarvestEvent ev)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) { BeginInvoke(() => AppendLog(ev)); return; }
            _log.AppendText(ev + "\r\n");
        }
        catch { }
    }
}
