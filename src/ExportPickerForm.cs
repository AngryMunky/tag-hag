using System.Drawing;
using System.Windows.Forms;

namespace TheTagHag;

/// <summary>
/// T18 — the confined destination picker. Instead of a free FolderBrowserDialog (which can land
/// copies/moves anywhere on disk), this shows a TreeView rooted at the export folder so every
/// destination stays inside the export tree. Subfolders load lazily; "New folder" creates one
/// under the selected node. The export ROOT itself is chosen once via Settings / first-run.
/// Dark Magic Pro styling, code-built to match the other dialogs.
/// </summary>
public sealed class ExportPickerForm : Form
{
    private static readonly Color Bg = Color.FromArgb(0x14, 0x10, 0x18);
    private static readonly Color Panel = Color.FromArgb(0x1B, 0x16, 0x22);
    private static readonly Color Bone = Color.FromArgb(0xD8, 0xD0, 0xBF);
    private static readonly Color Mut = Color.FromArgb(0x8A, 0x84, 0x95);
    private static readonly Color Acid = Color.FromArgb(0xA4, 0xFF, 0x6A);
    private static readonly Color Border = Color.FromArgb(0x3A, 0x37, 0x44);

    private readonly TreeView _tree = new();
    private readonly string _root;

    /// <summary>The chosen destination folder (always within the export root). Null until OK.</summary>
    public string? SelectedPath { get; private set; }

    public ExportPickerForm(string exportRoot, string description)
    {
        _root = exportRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Text = "Choose destination";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = true; ShowInTaskbar = false;
        ClientSize = new Size(470, 470);
        MinimumSize = new Size(380, 360);
        BackColor = Bg; ForeColor = Bone; Font = new Font("Segoe UI", 9f);

        var desc = new Label
        {
            Text = description, ForeColor = Bone, AutoSize = false,
            Location = new Point(16, 14), Size = new Size(438, 34)
        };
        var rootLabel = new Label
        {
            Text = "Within: " + _root, ForeColor = Mut, AutoSize = false,
            Location = new Point(16, 48), Size = new Size(438, 18),
            Font = new Font("Segoe UI", 8.25f)
        };

        _tree.SetBounds(16, 72, 438, 320);
        _tree.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _tree.BackColor = Panel; _tree.ForeColor = Bone;
        _tree.BorderStyle = BorderStyle.FixedSingle;
        _tree.HideSelection = false; _tree.FullRowSelect = true; _tree.ShowLines = true;
        _tree.PathSeparator = "\\";
        _tree.BeforeExpand += (_, e) => { if (e.Node is not null) LoadChildren(e.Node); };
        _tree.NodeMouseDoubleClick += (_, _) => AcceptSelection();

        var newBtn = MakeButton("New folder…", 16, 404, 110, accent: false);
        newBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        newBtn.Click += (_, _) => CreateFolder();

        var ok = MakeButton("Use folder", 264, 404, 90, accent: true);
        ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        ok.Click += (_, _) => AcceptSelection();

        var cancel = MakeButton("Cancel", 364, 404, 90, accent: false);
        cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange(new Control[] { desc, rootLabel, _tree, newBtn, ok, cancel });
        AcceptButton = ok; CancelButton = cancel;

        PopulateRoot();
    }

    private Button MakeButton(string text, int x, int y, int w, bool accent)
    {
        var b = new Button { Text = text };
        b.SetBounds(x, y, w, 30);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = accent ? Acid : Border;
        b.BackColor = accent ? Acid : Panel;
        b.ForeColor = accent ? Color.FromArgb(0x17, 0x34, 0x04) : Bone;
        if (accent) b.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        return b;
    }

    private void PopulateRoot()
    {
        Directory.CreateDirectory(_root); // ensure it exists
        var rootNode = new TreeNode(Path.GetFileName(_root) + "  (export root)") { Tag = _root };
        _tree.Nodes.Add(rootNode);
        LoadChildren(rootNode);
        rootNode.Expand();
        _tree.SelectedNode = rootNode;
    }

    /// <summary>Replace a node's lazy placeholder with its real subfolders (one level).</summary>
    private void LoadChildren(TreeNode node)
    {
        if (node.Tag is not string dir) return;
        // Already loaded for real? (a non-placeholder child exists)
        if (node.Nodes.Count > 0 && node.Nodes[0].Tag is string) return;
        node.Nodes.Clear();
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var child = new TreeNode(Path.GetFileName(sub)) { Tag = sub };
                // add a placeholder so the expand arrow shows when it has its own subfolders
                if (HasSubdirs(sub)) child.Nodes.Add(new TreeNode("…"));
                node.Nodes.Add(child);
            }
        }
        catch { /* unreadable dir — leave empty */ }
    }

    private static bool HasSubdirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).Any(); }
        catch { return false; }
    }

    private void CreateFolder()
    {
        var parent = _tree.SelectedNode ?? _tree.Nodes[0];
        if (parent.Tag is not string parentDir) return;
        var name = PromptName.Show(this, "New folder name:", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "That name contains characters not allowed in a folder name.",
                "New folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var full = Path.Combine(parentDir, name);
            Directory.CreateDirectory(full);
            LoadChildren(parent); // refresh
            parent.Expand();
            var made = parent.Nodes.Cast<TreeNode>()
                .FirstOrDefault(n => string.Equals(n.Tag as string, full, StringComparison.OrdinalIgnoreCase));
            if (made is not null) _tree.SelectedNode = made;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't create the folder:\n" + ex.Message,
                "New folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AcceptSelection()
    {
        var node = _tree.SelectedNode ?? _tree.Nodes[0];
        SelectedPath = node.Tag as string ?? _root;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Tiny modal text prompt (WinForms has no built-in InputBox without VisualBasic).</summary>
internal static class PromptName
{
    public static string? Show(IWin32Window owner, string label, string title)
    {
        var bg = Color.FromArgb(0x14, 0x10, 0x18);
        var bone = Color.FromArgb(0xD8, 0xD0, 0xBF);
        var acid = Color.FromArgb(0xA4, 0xFF, 0x6A);
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            ClientSize = new Size(320, 116), BackColor = bg, ForeColor = bone,
            Font = new Font("Segoe UI", 9f)
        };
        AppIcon.Apply(f);
        var lbl = new Label { Text = label, AutoSize = true, Location = new Point(14, 14) };
        var tb = new TextBox
        {
            Location = new Point(14, 38), Width = 292,
            BackColor = Color.FromArgb(0x22, 0x1D, 0x2B), ForeColor = bone, BorderStyle = BorderStyle.FixedSingle
        };
        var ok = new Button { Text = "Create", DialogResult = DialogResult.OK };
        ok.SetBounds(150, 76, 76, 28);
        ok.FlatStyle = FlatStyle.Flat; ok.BackColor = acid; ok.ForeColor = Color.FromArgb(0x17, 0x34, 0x04);
        ok.FlatAppearance.BorderColor = acid; ok.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(232, 76, 74, 28);
        cancel.FlatStyle = FlatStyle.Flat; cancel.BackColor = Color.FromArgb(0x1B, 0x16, 0x22); cancel.ForeColor = bone;
        cancel.FlatAppearance.BorderColor = Color.FromArgb(0x3A, 0x37, 0x44);
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(tb.Text) ? tb.Text : null;
    }
}
