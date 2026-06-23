using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using ISImage = SixLabors.ImageSharp.Image;

namespace TheTagHag;

/// <summary>
/// Minimal PNG chunk writer — the inverse of <see cref="PngChunkReader"/>. Splices arbitrary
/// text chunks into an already-encoded PNG, immediately before IEND, so generation metadata
/// survives a re-encode. Latin-1-safe text is written as tEXt (what A1111/ComfyUI emit, so the
/// wider ecosystem keeps reading it); anything outside Latin-1 falls back to UTF-8 iTXt so no
/// character is lost. NOT ImageSharp — same reasoning as the reader: full control over arbitrary
/// keywords and large ComfyUI graphs. Used by <see cref="ImageOptimizer"/> (T14).
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    /// <summary>
    /// Return a new PNG with the given (keyword, text) chunks inserted before IEND. If
    /// <paramref name="png"/> is not a parseable PNG, it is returned unchanged (caller still gets
    /// a valid resized image, just without re-injected metadata).
    /// </summary>
    public static byte[] WithTextChunks(byte[] png, IEnumerable<(string Keyword, string Text)> chunks)
    {
        int iend = FindIendStart(png);
        if (iend < 0) return png;

        using var ms = new MemoryStream(png.Length + 512);
        ms.Write(png, 0, iend);                  // signature + all chunks up to (not incl.) IEND
        foreach (var (kw, text) in chunks)
            if (!string.IsNullOrEmpty(kw)) WriteTextChunk(ms, kw, text ?? "");
        ms.Write(png, iend, png.Length - iend);  // the IEND chunk itself (12 bytes)
        return ms.ToArray();
    }

    /// <summary>Transcode a JPEG (Civitai download) to PNG and splice in the A1111 "parameters"
    /// chunk — so harvested images carry the same embedded metadata the local scanner reads (T17).</summary>
    public static byte[] TranscodeAndEmbed(byte[] jpeg, string parameters)
    {
        using var img = ISImage.Load(jpeg);
        using var ms = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, ms);
        return WithTextChunks(ms.ToArray(), new[] { ("parameters", parameters) });
    }

    /// <summary>Byte offset of the IEND chunk's length field, or -1 if the stream isn't a PNG.</summary>
    private static int FindIendStart(byte[] png)
    {
        if (png.Length < 8 + 12 || !png.AsSpan(0, 8).SequenceEqual(Sig)) return -1;
        int pos = 8;
        while (pos + 8 <= png.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IEND") return pos;
            long next = (long)pos + 12 + len;    // length(4) + type(4) + data + crc(4)
            if (next <= pos || next > png.Length) return -1;
            pos = (int)next;
        }
        return -1;
    }

    private static void WriteTextChunk(Stream s, string keyword, string text)
    {
        // PNG keywords are 1–79 Latin-1 chars; clamp defensively.
        if (keyword.Length > 79) keyword = keyword[..79];
        var kw = Latin1.GetBytes(keyword);
        bool latin1Safe = text.All(c => c <= 0xFF);

        string type;
        byte[] data;
        if (latin1Safe)
        {
            // tEXt: keyword \0 textLatin1
            type = "tEXt";
            var tx = Latin1.GetBytes(text);
            data = new byte[kw.Length + 1 + tx.Length];
            kw.CopyTo(data, 0);
            data[kw.Length] = 0;
            tx.CopyTo(data, kw.Length + 1);
        }
        else
        {
            // iTXt: keyword \0 compFlag(0) compMethod(0) lang \0 transKw \0 textUtf8
            type = "iTXt";
            var tx = Encoding.UTF8.GetBytes(text);
            data = new byte[kw.Length + 5 + tx.Length];
            int p = 0;
            kw.CopyTo(data, p); p += kw.Length;
            data[p++] = 0; // keyword separator
            data[p++] = 0; // compression flag = uncompressed
            data[p++] = 0; // compression method
            data[p++] = 0; // empty language tag + terminator
            data[p++] = 0; // empty translated keyword + terminator
            tx.CopyTo(data, p);
        }

        var typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> be = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(be, (uint)data.Length);
        s.Write(be);
        s.Write(typeBytes);
        s.Write(data);

        // PNG CRC-32 (ISO-HDLC) over type + data. System.IO.Hashing emits the value little-endian;
        // PNG stores it big-endian.
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        uint crcVal = BinaryPrimitives.ReadUInt32LittleEndian(crc.GetCurrentHash());
        BinaryPrimitives.WriteUInt32BigEndian(be, crcVal);
        s.Write(be);
    }
}
