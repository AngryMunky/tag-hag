using System.Text.Json;

namespace TheTagHag;

/// <summary>
/// Defensive accessors over System.Text.Json — reused verbatim from CivitaiHarvesterApp. Civitai
/// responses are loosely typed (fields missing / null / wrong type intermittently), so the harvest
/// client + StateStore read through these null-guarded helpers. (Used by the Civitai mode, T17.)
/// </summary>
internal static class JsonX
{
    public static readonly JsonSerializerOptions Camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static bool TryProp(this JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value))
            return value.ValueKind != JsonValueKind.Null;
        value = default;
        return false;
    }

    public static string? Str(this JsonElement e, string name)
        => e.TryProp(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;

    public static int IntOr(this JsonElement e, string name, int fallback = 0)
    {
        if (!e.TryProp(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return (int)l;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return (int)s;
        return fallback;
    }

    public static long? LongOrNull(this JsonElement e, string name)
    {
        if (!e.TryProp(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public static double? DoubleOrNull(this JsonElement e, string name)
    {
        if (!e.TryProp(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(),
                System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name)
    {
        if (e.TryProp(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray()) yield return item;
    }
}
