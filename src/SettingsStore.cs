using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheTagHag;

/// <summary>
/// Portable settings (settings.json beside the exe). The Civitai API key is stored DPAPI-encrypted
/// (CurrentUser) — only the encrypted blob is persisted; the plaintext <see cref="ApiKey"/> is
/// [JsonIgnore]'d so it never hits disk. (Inherited pattern from CivitaiHarvesterApp.)
/// </summary>
public sealed class AppSettings
{
    public List<string> SourceRoots { get; set; } = new();
    public string? ExportDir { get; set; }

    /// <summary>T37: User-configured managed store directory. Null = beside-exe default ("library-store").</summary>
    public string? StoreDir { get; set; }
    /// <summary>T37: Old store paths left behind by "Only new images" relocation — kept scanned so legacy files stay indexed.</summary>
    public List<string> LegacyStoreRoots { get; set; } = new();
    /// <summary>T37/T39: Default consolidation mode. Downsample resamples+recycles; MoveOnly relocates full-res.</summary>
    public OptimizeMode DefaultMode { get; set; } = OptimizeMode.Downsample;

    /// <summary>Default max longest-edge for downsample/optimize (T14).</summary>
    public int MaxDim { get; set; } = 1024;

    /// <summary>Civitai harvest parameters (T17 secondary mode).</summary>
    public HarvestOptions Harvest { get; set; } = new();

    /// <summary>Persisted form of the Civitai API key — DPAPI-encrypted base64, never plaintext.</summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>Plaintext Civitai API key (T17 harvest mode). Not serialized; round-trips through DPAPI.</summary>
    [JsonIgnore]
    public string ApiKey
    {
        get => SettingsStore.Decrypt(ApiKeyEncrypted);
        set => ApiKeyEncrypted = SettingsStore.Encrypt(value);
    }

    public int WinW { get; set; }
    public int WinH { get; set; }
    public int WinX { get; set; } = int.MinValue;
    public int WinY { get; set; } = int.MinValue;
    public bool WinMaximized { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try { File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(s, Opts)); }
        catch { /* best-effort */ }
    }

    // --- DPAPI (CurrentUser) helpers for the API key ---
    internal static string? Encrypt(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(blob);
        }
        catch { return null; }
    }

    internal static string Decrypt(string? enc)
    {
        if (string.IsNullOrEmpty(enc)) return "";
        try
        {
            var blob = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(blob);
        }
        catch { return ""; }
    }
}
