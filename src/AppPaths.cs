namespace TheTagHag;

/// <summary>
/// Resolves portable paths relative to the .exe itself, so everything — settings, log,
/// the SQLite library, the export tree, and the thumbnail cache — lives beside the
/// executable (portable, single-user requirement). Forked from CivitaiHarvesterApp's
/// AppPaths and extended with the Tag Hag library/export/thumbs helpers (architecture AD1).
/// </summary>
public static class AppPaths
{
    /// <summary>Directory containing the running .exe (works for single-file publish).</summary>
    public static string ExeDir
    {
        get
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p))
            {
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            return AppContext.BaseDirectory;
        }
    }

    public static string SettingsFile => Path.Combine(ExeDir, "settings.json");
    public static string LogFile => Path.Combine(ExeDir, "taghag.log");

    // --- Tag Hag additions ---
    /// <summary>The SQLite live store (WAL).</summary>
    public static string LibraryDbFile => Path.Combine(ExeDir, "library.db");
    /// <summary>Default export root (Copy/Move/Archive land under here).</summary>
    public static string ExportDir => Path.Combine(ExeDir, "export");
    /// <summary>Thumbnail cache (lazy + pre-warmed WebP thumbnails).</summary>
    public static string ThumbsDir => Path.Combine(ExeDir, "thumbs");

    private static string? _storeDir;

    /// <summary>T37: Inject the user-configured store dir once at startup (from AppSettings.StoreDir).
    /// Pass null to restore the beside-exe default. Must be called before any use of LibraryStoreDir.</summary>
    public static void SetStoreDir(string? dir) => _storeDir = dir;

    /// <summary>The Tag Hag-managed library store (v2.1, architecture "model shift"). Returns the
    /// user-configured dir (T37) when set, else the portable beside-exe default. Optimized images are
    /// resampled (or moved) into here under a per-root slug. Itself a scanned managed root.
    /// See LibraryDb.MarkOptimized + LocalScanner.</summary>
    public static string LibraryStoreDir => _storeDir ?? Path.Combine(ExeDir, "library-store");

    /// <summary>Create the managed store directory if absent; returns its path.</summary>
    public static string EnsureLibraryStore()
    {
        Directory.CreateDirectory(LibraryStoreDir);
        return LibraryStoreDir;
    }

    /// <summary>Civitai harvest output (images\ + dataset.jsonl + state.json). Add as a source root to index.</summary>
    public static string CivitaiDir => Path.Combine(ExeDir, "civitai");
}
