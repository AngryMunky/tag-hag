using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Numerics;

namespace TheTagHag;

/// <summary>
/// Perceptual image hashing for the Find Duplicates feature. Uses <b>dHash</b> (difference hash):
/// the image is reduced to a 9×8 grayscale thumbnail and each of the 64 output bits records whether
/// a pixel is brighter than its right-hand neighbour. This is robust to re-encoding, resizing, and
/// minor edits — visually-identical images produce the same (or a low-Hamming-distance) hash —
/// while staying cheap to compute. The 64-bit result is stored as a signed long per image
/// (the sign bit is just data; Hamming distance is sign-agnostic).
/// </summary>
public static class PerceptualHash
{
    /// <summary>Compute the 64-bit dHash of an image file, or null if it can't be decoded.</summary>
    public static long? Compute(string path)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<L8>(path); // decode straight to 8-bit grayscale
            img.Mutate(c => c.Resize(9, 8, KnownResamplers.Triangle));
            ulong hash = 0; int bit = 0;
            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < 8; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < 8; x++)
                    {
                        if (row[x].PackedValue < row[x + 1].PackedValue) hash |= 1UL << bit;
                        bit++;
                    }
                }
            });
            return unchecked((long)hash);
        }
        catch { return null; }
    }

    /// <summary>Hamming distance (number of differing bits, 0..64) between two hashes.</summary>
    public static int Hamming(long a, long b) => BitOperations.PopCount((ulong)(a ^ b));
}
