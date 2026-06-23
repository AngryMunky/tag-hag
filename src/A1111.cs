using System.Text;

namespace TheTagHag;

/// <summary>
/// Builds the A1111 "parameters" string embedded into harvested PNGs (so they carry the same
/// metadata the local scanner reads back). Ported from CivitaiHarvesterApp. Distinct from
/// <see cref="A1111Parser"/> (which is the inverse — reading that text back).
/// </summary>
public static class A1111
{
    public static string BuildParams(ImageRecord r)
    {
        var sb = new StringBuilder();
        sb.Append(r.prompt);
        if (!string.IsNullOrEmpty(r.negativePrompt))
            sb.Append("\nNegative prompt: ").Append(r.negativePrompt);
        sb.Append('\n')
          .Append($"Steps: {r.steps}, Sampler: {r.sampler}, CFG scale: {Num(r.cfgScale)}, ")
          .Append($"Seed: {r.seed}, Size: {r.width}x{r.height}, Model: {r.model}");
        return sb.ToString();
    }

    public static string FormatSidecar(ImageRecord r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.prompt);
        if (!string.IsNullOrEmpty(r.negativePrompt))
            sb.AppendLine($"Negative prompt: {r.negativePrompt}");
        sb.AppendLine($"Steps: {r.steps}, Sampler: {r.sampler}, CFG scale: {Num(r.cfgScale)}, " +
                      $"Seed: {r.seed}, Model: {r.model}, Size: {r.width}x{r.height}");
        if (r.resources.Count > 0)
        {
            var res = string.Join(", ", r.resources.Select(x =>
            {
                var w = x.weight.HasValue ? $":{Num(x.weight)}" : "";
                return $"{x.type}/{x.name}{w}";
            }));
            sb.AppendLine($"Resources: {res}");
        }
        if (r.tags.Count > 0)
            sb.AppendLine($"Tags: {string.Join(", ", r.tags)}");
        var col = r.collection != null ? $"  collection={r.collection}" : "";
        sb.AppendLine($"Civitai: https://civitai.com/images/{r.id}  (likes: {r.likes}, hearts: {r.hearts}){col}");
        return sb.ToString();
    }

    private static string Num(double? d) =>
        d?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
}
