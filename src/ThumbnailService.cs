using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace TheTagHag;

/// <summary>
/// Lazy thumbnail cache (architecture O1). Produces a ~512px WebP per image into thumbs\,
/// keyed by hash(abs_path + mtime) so edits invalidate. Generation is on-demand (the WebView2
/// resource interceptor calls GetOrCreate only for thumbnails actually scrolled into view) and
/// cached forever after — so the grid never decodes full-resolution images. Uses its OWN
/// LibraryDb connection (WAL) with a small lock around the lookup; the decode/resize runs
/// outside the lock so generation parallelizes.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private const int MaxDim = 512;
    private readonly LibraryDb _db;
    private readonly string _dir;
    private readonly object _lookupLock = new();

    public ThumbnailService(string dbPath, string thumbsDir)
    {
        _db = new LibraryDb(dbPath);
        _dir = thumbsDir;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Return the cached thumbnail path for an image id, generating it if needed; null on failure.</summary>
    public string? GetOrCreate(long id)
    {
        ImageRow? row;
        lock (_lookupLock) { row = _db.GetById(id); }
        if (row is null || !File.Exists(row.AbsPath)) return null;

        var key = Hash(row.AbsPath + "|" + row.MtimeTicks);
        var outPath = Path.Combine(_dir, key + ".webp");
        if (File.Exists(outPath)) return outPath;

        try
        {
            using var img = ISImage.Load(row.AbsPath);
            img.Mutate(x => x.Resize(new ResizeOptions { Size = new(MaxDim, MaxDim), Mode = ResizeMode.Max }));
            var tmp = outPath + ".tmp";
            SixLabors.ImageSharp.ImageExtensions.SaveAsWebp(img, tmp, new WebpEncoder { Quality = 80 });
            File.Move(tmp, outPath, true); // atomic-ish so a half-written file is never served
            return outPath;
        }
        catch { return null; }
    }

    /// <summary>Original file path for an image id (served full-res in the lightbox). Null if gone.</summary>
    public string? GetOriginalPath(long id)
    {
        ImageRow? row;
        lock (_lookupLock) { row = _db.GetById(id); }
        return row is not null && File.Exists(row.AbsPath) ? row.AbsPath : null;
    }

    private static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..16];
    }

    public void Dispose() => _db.Dispose();
}
