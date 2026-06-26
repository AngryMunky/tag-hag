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

    /// <summary>The Tag Hag-managed library store (v2.1, architecture "model shift"). Optimizing an
    /// image resamples it into here — mirroring its source rel-path under a per-root slug — and
    /// recycles the original. Portable (beside the exe) and itself a SCANNED managed root, so its
    /// contents stay indexed. See LibraryDb.MarkOptimized + LocalScanner.</summary>
    public static string LibraryStoreDir => Path.Combine(ExeDir, "library-store");

    /// <summary>Create the managed store directory if absent; returns its path.</summary>
    public static string EnsureLibraryStore()
    {
        Directory.CreateDirectory(LibraryStoreDir);
        return LibraryStoreDir;
    }

    /// <summary>Civitai harvest output (images\ + dataset.jsonl + state.json). Add as a source root to index.</summary>
    public static string CivitaiDir => Path.Combine(ExeDir, "civitai");
}
