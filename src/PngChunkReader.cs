using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TheTagHag;

/// <summary>
/// Raw PNG chunk walker — the inverse of PngWriter's iTXt framing, generalized to read
/// tEXt / zTXt / iTXt text chunks plus IHDR dimensions. NOT ImageSharp (unreliable for
/// arbitrary keys + huge ComfyUI chunks). Strict bounds + a per-chunk size cap so a
/// malformed length can't OOM; any error degrades to "return what we have" (R5).
/// </summary>
public sealed class PngTextChunks
{
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>keyword → decoded text (e.g. "parameters", "prompt", "workflow").</summary>
    public Dictionary<string, string> Text { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class PngChunkReader
{
    private const long MaxChunk = 4L * 1024 * 1024; // per-chunk cap
    private static readonly byte[] Sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    /// <summary>Returns null if the file isn't a PNG; otherwise the text chunks + dims found.</summary>
    public static PngTextChunks? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> sig = stackalloc byte[8];
            if (!ReadExact(fs, sig) || !sig.SequenceEqual(Sig)) return null;

            var result = new PngTextChunks();
            Span<byte> hdr = stackalloc byte[8]; // length(4) + type(4)
            while (ReadExact(fs, hdr))
            {
                uint len = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
                var type = Encoding.ASCII.GetString(hdr[4..8]);

                if (type == "IEND") break;

                if (type == "IHDR")
                {
                    var ihdr = new byte[Math.Min(len, 8)];
                    if (!ReadExact(fs, ihdr)) break;
                    if (ihdr.Length >= 8)
                    {
                        result.Width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(0, 4));
                        result.Height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4, 4));
                    }
                    Skip(fs, (long)len - ihdr.Length + 4); // rest of IHDR data + CRC
                    continue;
                }

                bool isText = type is "tEXt" or "zTXt" or "iTXt";
                if (isText && len <= MaxChunk)
                {
                    var data = new byte[len];
                    if (!ReadExact(fs, data)) break;
                    try { ParseTextChunk(type, data, result.Text); } catch { /* skip bad chunk */ }
                    Skip(fs, 4); // CRC
                }
                else
                {
                    Skip(fs, (long)len + 4); // skip data + CRC (oversized or non-text)
                }
            }
            return result;
        }
        catch
        {
            return null; // unreadable/truncated → caller treats as no embedded metadata
        }
    }

    private static void ParseTextChunk(string type, byte[] data, Dictionary<string, string> into)
    {
        int nul = Array.IndexOf(data, (byte)0);
        if (nul <= 0) return;
        var keyword = Latin1.GetString(data, 0, nul);

        switch (type)
        {
            case "tEXt":
            {
                var text = Latin1.GetString(data, nul + 1, data.Length - nul - 1);
                into[keyword] = text;
                break;
            }
            case "zTXt":
            {
                // keyword \0 method(1) compressedText
                if (nul + 2 > data.Length) return;
                var comp = data.AsSpan(nul + 2).ToArray();
                into[keyword] = Inflate(comp, Latin1);
                break;
            }
            case "iTXt":
            {
                // keyword \0 compFlag(1) compMethod(1) lang \0 transKw \0 text
                int p = nul + 1;
                if (p + 2 > data.Length) return;
                byte compFlag = data[p]; p += 2; // skip compFlag + compMethod
                int langEnd = Array.IndexOf(data, (byte)0, p); if (langEnd < 0) return;
                int trEnd = Array.IndexOf(data, (byte)0, langEnd + 1); if (trEnd < 0) return;
                var textBytes = data.AsSpan(trEnd + 1).ToArray();
                into[keyword] = compFlag == 1 ? Inflate(textBytes, Encoding.UTF8) : Encoding.UTF8.GetString(textBytes);
                break;
            }
        }
    }

    private static string Inflate(byte[] zlib, Encoding enc)
    {
        using var input = new MemoryStream(zlib);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        return enc.GetString(outMs.ToArray());
    }

    private static bool ReadExact(Stream s, Span<byte> buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf[read..]);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static void Skip(Stream s, long n)
    {
        if (n <= 0) return;
        if (s.CanSeek) s.Seek(n, SeekOrigin.Current);
        else { var buf = new byte[Math.Min(n, 65536)]; while (n > 0) { int r = s.Read(buf, 0, (int)Math.Min(n, buf.Length)); if (r == 0) break; n -= r; } }
    }
}
