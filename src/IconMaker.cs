using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TheTagHag;

/// <summary>
/// Generates the Tag Hag app icon as a multi-resolution .ico — no external art tool.
/// The mark is the Dark Magic Pro palette: a dark-purple rounded square, a slightly
/// crooked witch hat (near-black with an acid-green band), and one glowing acid-green
/// eye. Rendered as a 1024px supersampled master, then down-scaled to each icon size
/// (16…256) so small sizes get clean anti-aliasing. Each entry is stored as PNG, which
/// Vista+ .ico supports. Run once via `--makeicon <path>`; the result is committed and
/// baked into the exe via &lt;ApplicationIcon&gt;.
/// </summary>
internal static class IconMaker
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static void Write(string icoPath)
    {
        using var master = RenderMaster(1024);
        WriteIco(master, icoPath);
    }

    /// <summary>Build the .ico from a source PNG (the real hag-tag mark), composited onto a dark
    /// rounded-square tile so it reads well at small sizes and on any desktop background.</summary>
    public static void WriteFromImage(string srcPng, string icoPath)
    {
        using var src = SixLabors.ImageSharp.Image.Load<Rgba32>(srcPng);
        using var master = RenderMasterFromMark(src, 1024);
        WriteIco(master, icoPath);
    }

    private static void WriteIco(Image<Rgba32> master, string icoPath)
    {
        // Sizes ≤128 → uncompressed 32-bit BMP/DIB frames (the titlebar/taskbar sizes, and
        // the only ones legacy GDI+ Form.Icon reliably decodes). 256 → PNG (keeps the file small;
        // the modern shell decodes it).
        var frames = new List<(int size, byte[] data)>(Sizes.Length);
        foreach (var s in Sizes)
        {
            using var im = master.Clone(c => c.Resize(s, s, KnownResamplers.Lanczos3));
            if (s >= 256)
            {
                using var ms = new MemoryStream();
                SixLabors.ImageSharp.ImageExtensions.SaveAsPng(im, ms);
                frames.Add((s, ms.ToArray()));
            }
            else frames.Add((s, Dib(im)));
        }

        using var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        // ICONDIR
        bw.Write((short)0);             // reserved
        bw.Write((short)1);             // type = icon
        bw.Write((short)frames.Count);  // image count
        int offset = 6 + 16 * frames.Count;
        foreach (var (s, data) in frames)
        {
            bw.Write((byte)(s >= 256 ? 0 : s)); // width  (0 = 256)
            bw.Write((byte)(s >= 256 ? 0 : s)); // height (0 = 256)
            bw.Write((byte)0);                  // palette count
            bw.Write((byte)0);                  // reserved
            bw.Write((short)1);                 // color planes
            bw.Write((short)32);                // bits per pixel
            bw.Write(data.Length);              // bytes in resource
            bw.Write(offset);                   // offset from file start
            offset += data.Length;
        }
        foreach (var (_, data) in frames) bw.Write(data);
    }

    /// <summary>Encode a 32-bit BGRA icon frame as an uncompressed DIB (BITMAPINFOHEADER +
    /// bottom-up XOR pixels + 1-bpp AND mask), the classic .ico frame GDI+ and Explorer both read.</summary>
    private static byte[] Dib(Image<Rgba32> im)
    {
        int w = im.Width, h = im.Height;
        int andRow = ((w + 31) / 32) * 4; // 1-bpp row padded to a 4-byte boundary
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        // BITMAPINFOHEADER — height doubled to cover XOR + AND masks.
        bw.Write(40); bw.Write(w); bw.Write(h * 2);
        bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(0);          // BI_RGB, sizeImage
        bw.Write(0); bw.Write(0);          // ppm x/y
        bw.Write(0); bw.Write(0);          // clrUsed, clrImportant
        // XOR bitmap, bottom-up, BGRA.
        for (int y = h - 1; y >= 0; y--)
            for (int x = 0; x < w; x++)
            {
                var p = im[x, y];
                bw.Write(p.B); bw.Write(p.G); bw.Write(p.R); bw.Write(p.A);
            }
        // AND mask, bottom-up, 1 = transparent.
        for (int y = h - 1; y >= 0; y--)
        {
            var row = new byte[andRow];
            for (int x = 0; x < w; x++)
                if (im[x, y].A < 128) row[x / 8] |= (byte)(0x80 >> (x % 8));
            bw.Write(row);
        }
        return ms.ToArray();
    }

    /// <summary>Dark rounded-square tile (vertical purple gradient) with the transparent mark PNG
    /// scaled to ~0.82 of the tile and centered, alpha-composited on top.</summary>
    private static Image<Rgba32> RenderMasterFromMark(Image<Rgba32> mark, int S)
    {
        var img = new Image<Rgba32>(S, S, new Rgba32(0, 0, 0, 0));
        var top = new Rgba32(0x26, 0x1d, 0x33);
        var bot = new Rgba32(0x12, 0x0d, 0x18);
        float m = S * 0.045f, r = S * 0.22f, lo = m, hi = S - m;
        for (int y = 0; y < S; y++)
        {
            var bg = Lerp(top, bot, (float)y / S);
            for (int x = 0; x < S; x++)
                if (InRoundRect(x + 0.5f, y + 0.5f, lo, lo, hi, hi, r)) img[x, y] = bg;
        }
        float target = S * 0.82f;
        float scale = MathF.Min(target / mark.Width, target / mark.Height);
        int mw = Math.Max(1, (int)(mark.Width * scale)), mh = Math.Max(1, (int)(mark.Height * scale));
        using var rm = mark.Clone(c => c.Resize(mw, mh, KnownResamplers.Lanczos3));
        int ox = (S - mw) / 2, oy = (S - mh) / 2;
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                var p = rm[x, y];
                if (p.A == 0) continue;
                int X = ox + x, Y = oy + y;
                if (X < 0 || Y < 0 || X >= S || Y >= S) continue;
                if (!InRoundRect(X + 0.5f, Y + 0.5f, lo, lo, hi, hi, r)) continue;
                img[X, Y] = Over(img[X, Y], p.R, p.G, p.B, p.A);
            }
        return img;
    }

    private static Rgba32 Lerp(Rgba32 a, Rgba32 b, float t) => new Rgba32(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t),
        255);

    private static Image<Rgba32> RenderMaster(int S)
    {
        var img = new Image<Rgba32>(S, S, new Rgba32(0, 0, 0, 0));

        // Dark Magic Pro palette
        var bg = new Rgba32(0x34, 0x25, 0x4d);   // deep purple field
        var edge = new Rgba32(0x5B, 0x3B, 0x8C); // purple rim
        var hat = new Rgba32(0x15, 0x11, 0x1c);  // near-black hat
        byte ar = 0xA4, ag = 0xFF, ab = 0x6A;    // acid green

        float F(float n) => n * S; // normalized → pixels
        float m = F(0.055f), r = F(0.23f), lo = m, hi = S - m, rimW = F(0.012f);

        // Witch-hat geometry (apex pulled left for a crooked silhouette).
        float ax = F(0.455f), ay = F(0.13f);
        float blx = F(0.305f), bly = F(0.515f), brx = F(0.665f), bry = F(0.515f);
        // Brim ellipse.
        float bcx = F(0.50f), bcy = F(0.515f), brrx = F(0.335f), brry = F(0.066f);
        // Hat band (acid) — a horizontal slab clipped to the cone.
        float bandLo = F(0.435f), bandHi = F(0.485f);
        // Glowing eye.
        float ex = F(0.52f), ey = F(0.68f), glow = F(0.125f), core = F(0.058f);

        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                if (!InRoundRect(px, py, lo, lo, hi, hi, r)) continue;

                var c = bg;
                // subtle inner rim
                if (!InRoundRect(px, py, lo + rimW, lo + rimW, hi - rimW, hi - rimW, r - rimW))
                    c = edge;

                bool inCone = InTri(px, py, ax, ay, blx, bly, brx, bry);
                bool inBrim = Ellipse(px, py, bcx, bcy, brrx, brry);
                if (inBrim || inCone) c = hat;
                if (inCone && py >= bandLo && py <= bandHi) c = new Rgba32(ar, ag, ab);

                // glowing eye: soft halo + bright core
                float d = MathF.Sqrt((px - ex) * (px - ex) + (py - ey) * (py - ey));
                if (d < glow)
                {
                    float t = 1f - d / glow;          // 0 at edge → 1 at center
                    byte a = (byte)(t * t * 200);     // quadratic falloff halo
                    c = Over(c, ar, ag, ab, a);
                }
                if (d < core) c = new Rgba32(ar, ag, ab);

                img[x, y] = c;
            }
        }
        return img;
    }

    private static Rgba32 Over(Rgba32 dst, byte r, byte g, byte b, byte a)
    {
        float af = a / 255f, ia = 1f - af;
        return new Rgba32(
            (byte)(r * af + dst.R * ia),
            (byte)(g * af + dst.G * ia),
            (byte)(b * af + dst.B * ia),
            255);
    }

    private static bool InRoundRect(float x, float y, float x0, float y0, float x1, float y1, float r)
    {
        if (x < x0 || x > x1 || y < y0 || y > y1) return false;
        bool cornerX = x < x0 + r || x > x1 - r;
        bool cornerY = y < y0 + r || y > y1 - r;
        if (!(cornerX && cornerY)) return true;
        float cx = x < x0 + r ? x0 + r : x1 - r;
        float cy = y < y0 + r ? y0 + r : y1 - r;
        float dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    private static bool Ellipse(float x, float y, float cx, float cy, float rx, float ry)
    {
        float nx = (x - cx) / rx, ny = (y - cy) / ry;
        return nx * nx + ny * ny <= 1f;
    }

    private static float Edge(float ax, float ay, float bx, float by, float px, float py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    private static bool InTri(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        float d1 = Edge(ax, ay, bx, by, px, py);
        float d2 = Edge(bx, by, cx, cy, px, py);
        float d3 = Edge(cx, cy, ax, ay, px, py);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }
}
