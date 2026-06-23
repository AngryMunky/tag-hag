using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using ISImage = SixLabors.ImageSharp.Image;

namespace TheTagHag;

/// <summary>
/// Reads one image's generation metadata, in resolution order (architecture §5):
///   1. Embedded PNG chunks (PngChunkReader) — "parameters" → A1111; "prompt"/"workflow" → ComfyUI (T5).
///   2. JPEG/WebP EXIF — UserComment (0x9286) / ImageDescription → A1111.
///   3. Sidecar &lt;basename&gt;.txt → A1111.
/// Width/Height always come from the actual file (IHDR or ImageSharp identify), not the
/// generation "Size" field. Never throws — a bad file yields Format='none'.
/// </summary>
public static class ImageMetadataReader
{
    public static ParsedMeta Read(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        int w = 0, h = 0;
        var meta = new ParsedMeta();

        try
        {
            if (ext == ".png") meta = FromPng(path, ref w, ref h);
            else if (ext is ".jpg" or ".jpeg" or ".webp") meta = FromExif(path, ref w, ref h);
        }
        catch { meta = new ParsedMeta(); }

        // Fall back to a sidecar .txt only when no embedded/exif metadata was found.
        if (meta.Format == "none")
        {
            try
            {
                var side = FromSidecar(path);
                if (side.Format != "none") meta = side;
            }
            catch { /* ignore */ }
        }

        if (meta.Width == 0) meta.Width = w;
        if (meta.Height == 0) meta.Height = h;
        return meta;
    }

    private static ParsedMeta FromPng(string path, ref int w, ref int h)
    {
        var chunks = PngChunkReader.Read(path);
        if (chunks is null) return new ParsedMeta();
        w = chunks.Width; h = chunks.Height;

        if (chunks.Text.TryGetValue("parameters", out var paramsText) && !string.IsNullOrWhiteSpace(paramsText))
            return A1111Parser.Parse(paramsText, "embedded");

        // ComfyUI graph — parse the API "prompt" map (preferred); keep "workflow" for the raw view.
        if (chunks.Text.TryGetValue("prompt", out var promptJson) && !string.IsNullOrWhiteSpace(promptJson))
            return ComfyGraphParser.Parse(promptJson, chunks.Text.GetValueOrDefault("workflow"));
        if (chunks.Text.TryGetValue("workflow", out var wf) && !string.IsNullOrWhiteSpace(wf))
            return new ParsedMeta { Format = "comfyui", Source = "embedded", RawJson = wf };

        return new ParsedMeta();
    }

    private static ParsedMeta FromExif(string path, ref int w, ref int h)
    {
        var info = ISImage.Identify(path);
        w = info.Width; h = info.Height;

        var exif = info.Metadata.ExifProfile;
        if (exif is null) return new ParsedMeta();

        string? text = null;
        if (exif.TryGetValue(ExifTag.UserComment, out var uc) && uc?.Value is { } enc)
        {
            var s = enc.ToString();
            if (!string.IsNullOrWhiteSpace(s)) text = s;
        }
        if (string.IsNullOrWhiteSpace(text) &&
            exif.TryGetValue(ExifTag.ImageDescription, out var desc) && !string.IsNullOrWhiteSpace(desc?.Value))
            text = desc!.Value;

        return string.IsNullOrWhiteSpace(text) ? new ParsedMeta() : A1111Parser.Parse(text!, "exif");
    }

    private static ParsedMeta FromSidecar(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is null) return new ParsedMeta();
        var txt = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".txt");
        if (!File.Exists(txt)) return new ParsedMeta();
        return A1111Parser.Parse(File.ReadAllText(txt), "sidecar-txt");
    }
}
