using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace TheTagHag;

/// <summary>Outcome of one optimize operation, for the batch tally in MainForm.</summary>
public enum OptimizeOutcome { Resized, SkippedSmall, Failed }

/// <summary>
/// T14 — metadata-preserving downsample. Shrink-only (never upscales): an image already within
/// <c>maxDim</c> is left as-is. Generation metadata MUST survive: for PNG the original
/// parameters/prompt/workflow text chunks are re-spliced via <see cref="PngWriter"/> after the
/// ImageSharp re-encode; for JPEG/WebP the EXIF profile is carried forward by ImageSharp.
/// Confirmed product decision: export COPIES by default; in-place overwrite only behind a strong
/// confirmation (enforced by the caller).
/// </summary>
public static class ImageOptimizer
{
    public const int DefaultMaxDim = 1024;

    /// <summary>The PNG text chunks worth preserving across a re-encode.</summary>
    private static readonly string[] PreservePngKeys = { "parameters", "prompt", "workflow" };

    /// <summary>
    /// Write a downsampled copy of <paramref name="src"/> to the NEW path <paramref name="dest"/>.
    /// If the source is already within <paramref name="maxDim"/> it is byte-copied unchanged
    /// (metadata trivially preserved) and reported as <see cref="OptimizeOutcome.SkippedSmall"/>.
    /// </summary>
    public static OptimizeOutcome DownsampleToCopy(string src, string dest, int maxDim)
    {
        try
        {
            var (w, h) = ReadDimensions(src);
            if (w > 0 && w <= maxDim && h <= maxDim)
            {
                File.Copy(src, dest, overwrite: false);
                return OptimizeOutcome.SkippedSmall;
            }
            var bytes = ResizePreserving(src, maxDim);
            if (bytes is null) return OptimizeOutcome.Failed;
            File.WriteAllBytes(dest, bytes);
            return OptimizeOutcome.Resized;
        }
        catch { return OptimizeOutcome.Failed; }
    }

    /// <summary>
    /// Downsample <paramref name="path"/> by overwriting it in place — only ever invoked behind an
    /// explicit, not-recoverable confirmation. Already-small images are left completely untouched.
    /// The write goes to a temp file first, then atomically replaces the original.
    /// </summary>
    public static OptimizeOutcome DownsampleInPlace(string path, int maxDim)
    {
        try
        {
            var (w, h) = ReadDimensions(path);
            if (w == 0) return OptimizeOutcome.Failed;          // couldn't read → don't touch it
            if (w <= maxDim && h <= maxDim) return OptimizeOutcome.SkippedSmall;

            var bytes = ResizePreserving(path, maxDim);
            if (bytes is null) return OptimizeOutcome.Failed;

            var tmp = path + ".optimizing.tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return OptimizeOutcome.Resized;
        }
        catch { return OptimizeOutcome.Failed; }
    }

    /// <summary>Pixel dimensions of an image, or (0,0) if it can't be identified.</summary>
    public static (int Width, int Height) ReadDimensions(string path)
    {
        try { var info = ISImage.Identify(path); return (info.Width, info.Height); }
        catch { return (0, 0); }
    }

    /// <summary>
    /// Shrink to fit within maxDim×maxDim (aspect-preserving) and return the re-encoded bytes in
    /// the SAME format, with generation metadata preserved. Returns null on failure.
    /// </summary>
    private static byte[]? ResizePreserving(string src, int maxDim)
    {
        var ext = Path.GetExtension(src).ToLowerInvariant();
        using var img = ISImage.Load(src);
        // ResizeMode.Max fits within the box preserving aspect; we only get here when a dimension
        // exceeds maxDim, so the controlling edge shrinks — it never upscales.
        img.Mutate(x => x.Resize(new ResizeOptions { Size = new(maxDim, maxDim), Mode = ResizeMode.Max }));

        using var ms = new MemoryStream();
        switch (ext)
        {
            case ".png":
                // Clear any text chunks ImageSharp parsed in, so we don't emit duplicates — the
                // originals are re-spliced explicitly below, exactly once each.
                img.Metadata.GetPngMetadata().TextData.Clear();
                img.SaveAsPng(ms, new PngEncoder());
                var png = ms.ToArray();

                var original = PngChunkReader.Read(src);
                if (original is not null)
                {
                    var keep = new List<(string, string)>();
                    foreach (var kw in PreservePngKeys)
                        if (original.Text.TryGetValue(kw, out var v) && !string.IsNullOrEmpty(v))
                            keep.Add((kw, v));
                    if (keep.Count > 0) png = PngWriter.WithTextChunks(png, keep);
                }
                return png;

            case ".jpg":
            case ".jpeg":
                // ImageSharp carries img.Metadata.ExifProfile (UserComment/ImageDescription) through save.
                img.SaveAsJpeg(ms, new JpegEncoder { Quality = 92 });
                return ms.ToArray();

            case ".webp":
                img.SaveAsWebp(ms, new WebpEncoder { Quality = 90 });
                return ms.ToArray();

            default:
                return null;
        }
    }
}
