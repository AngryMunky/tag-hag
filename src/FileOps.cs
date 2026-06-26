using System.Runtime.InteropServices;

namespace TheTagHag;

public readonly record struct OpResult(int Done, int Skipped, int Failed);

/// <summary>
/// File-operation primitives for the curation actions (T13). Pure file helpers; the DB row
/// updates live in MainForm's op runner (single-writer). Honors the confirmed decisions:
/// Delete → Recycle Bin (recoverable); collisions get a " (2)" suffix.
/// </summary>
public static class FileOps
{
    /// <summary>A non-colliding path in <paramref name="dir"/> — appends " (2)", " (3)", … as needed.</summary>
    public static string UniqueDestination(string dir, string fileName)
    {
        var dest = Path.Combine(dir, fileName);
        if (!File.Exists(dest)) return dest;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            dest = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(dest)) return dest;
        }
    }

    /// <summary>
    /// Send to the Windows Recycle Bin (recoverable) via the Win32 shell — the canonical,
    /// reliable method (FOF_ALLOWUNDO). More dependable than the Microsoft.VisualBasic wrapper,
    /// which can silently hard-delete under .NET 8 / single-file. If the file's DRIVE has the
    /// Recycle Bin disabled ("remove immediately"), Windows still hard-deletes — that's an OS
    /// per-drive setting we can't override.
    /// </summary>
    public static void RecycleDelete(string path)
    {
        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0", // double-null terminated list
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
        };
        int rc = SHFileOperationW(ref op);
        if (rc != 0 || op.fAnyOperationsAborted)
            throw new IOException($"Recycle delete failed (code 0x{rc:X}) for {path}");
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>Move across volumes safely (File.Move falls back to copy+delete when needed).</summary>
    public static void Move(string src, string dest)
    {
        try { File.Move(src, dest); }
        catch (IOException) { File.Copy(src, dest, false); File.Delete(src); }
    }

    /// <summary>Rename/move a whole directory (T33/F24 in-app folder rename). Throws if the
    /// destination already exists (the caller surfaces the collision — no data loss, no merge).
    /// Same-volume renames are atomic via Directory.Move; a cross-volume move falls back to a
    /// recursive copy + delete. The DB row repath is done separately by LibraryDb.RepathFolder.</summary>
    public static void MoveFolder(string src, string dst)
    {
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
            return; // no-op (e.g. case-only rename on a case-insensitive FS) — let the DB repath proceed
        if (Directory.Exists(dst)) throw new IOException($"A folder named \"{Path.GetFileName(dst)}\" already exists here.");
        try { Directory.Move(src, dst); }
        catch (IOException) { CopyDir(src, dst); Directory.Delete(src, recursive: true); }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), overwrite: false);
    }
}
