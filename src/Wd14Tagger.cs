using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;
using ISSize = SixLabors.ImageSharp.Size;

namespace TheTagHag;

/// <summary>
/// T50/F39 — WD14 ONNX inference pipeline.
/// Loads a SmilingWolf WD14 model (.onnx + sibling selected_tags.csv) and classifies images
/// into semantic tags. Tags land in user_tags via AddUserTagsForImage (LibraryDb).
///
/// Model contract (SmilingWolf WD14 v1–v4, v3 family):
///   Input:  first tensor — NHWC float32 [1, 448, 448, 3], values 0–255
///   Output: first tensor — [1, N] sigmoid scores (pre-applied by the model)
///   CSV:    tag_id, name, category, count  — skip header row; category 0=general, 4=character, 9=rating
/// </summary>
public sealed class Wd14Tagger : IDisposable
{
    private readonly InferenceSession _session;
    private readonly (string Name, int Category)[] _tags;  // index matches output tensor position
    private readonly float _threshold;
    private readonly string _inputName;

    public Wd14Tagger(string modelPath, float threshold)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("ONNX model not found.", modelPath);
        var csvPath = Path.Combine(Path.GetDirectoryName(modelPath)!, "selected_tags.csv");
        if (!File.Exists(csvPath))
            throw new FileNotFoundException("selected_tags.csv not found beside the model.", csvPath);

        _threshold = threshold;
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputNames[0];   // robust across model variants (A-v27-A)
        _tags = ParseCsv(csvPath);
    }

    /// <summary>
    /// Tag an image. Returns tags with score ≥ threshold from categories 0 (general) and 4 (character),
    /// sorted by score descending. Returns empty list (never throws) for missing or corrupt files.
    /// </summary>
    public IReadOnlyList<string> TagImage(string imagePath)
    {
        if (!File.Exists(imagePath)) return [];
        try
        {
            const int Dim = 448;
            using var img = ISImage.Load<Rgb24>(imagePath);
            img.Mutate(x => x.Resize(new ResizeOptions { Size = new ISSize(Dim, Dim), Mode = ResizeMode.Stretch }));

            var tensor = new DenseTensor<float>([1, Dim, Dim, 3]);
            for (int y = 0; y < Dim; y++)
                for (int x = 0; x < Dim; x++)
                {
                    var px = img[x, y];
                    tensor[0, y, x, 0] = px.R;
                    tensor[0, y, x, 1] = px.G;
                    tensor[0, y, x, 2] = px.B;
                }

            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
            var scores = results[0].AsEnumerable<float>().ToArray();

            return scores
                .Select((s, i) => (score: s, idx: i))
                .Where(t => t.score >= _threshold && t.idx < _tags.Length && _tags[t.idx].Category is 0 or 4)
                .OrderByDescending(t => t.score)
                .Select(t => _tags[t.idx].Name)
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// Search known WD14 install locations on disk. Returns the first .onnx that has a sibling
    /// selected_tags.csv, or null if none found.
    /// Checks: A1111 interrogate folder, ComfyUI clip_vision, webui interrogate, exe directory.
    /// </summary>
    public static string? FindAutomatic()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] dirs =
        [
            Path.Combine(home, @"stable-diffusion-webui\models\interrogate"),
            Path.Combine(home, @"ComfyUI\models\clip_vision"),
            Path.Combine(home, @"webui\models\interrogate"),
            AppPaths.ExeDir,
        ];
        foreach (var dir in dirs.Where(Directory.Exists))
            foreach (var onnx in Directory.EnumerateFiles(dir, "*.onnx", SearchOption.TopDirectoryOnly))
                if (File.Exists(Path.Combine(Path.GetDirectoryName(onnx)!, "selected_tags.csv")))
                    return onnx;
        return null;
    }

    private static (string Name, int Category)[] ParseCsv(string csvPath)
    {
        // Expected header: tag_id,name,category,count  (A-v27-B: assert on first parse)
        var lines = File.ReadAllLines(csvPath);
        return lines.Skip(1)
            .Where(l => l.Contains(','))
            .Select(l => { var p = l.Split(','); return (p[1].Trim(), int.Parse(p[2].Trim())); })
            .ToArray();
    }

    public void Dispose() => _session.Dispose();
}
