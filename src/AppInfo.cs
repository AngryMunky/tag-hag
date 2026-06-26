using System.Reflection;

namespace TheTagHag;

/// <summary>App identity / version (semver per the project-wide rule). Forked from
/// CivitaiHarvesterApp's AppInfo; canonical version is &lt;Version&gt; in the .csproj.</summary>
public static class AppInfo
{
    /// <summary>Derived from the assembly version so the build is the single source of truth.</summary>
    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";

    public const string Name = "The Tag Hag";
    public const string Tagline = "Tame the AI image hoard.";

    /// <summary>The Tag Hag's public home (the trimmed mirror) — shown in the About dialog.</summary>
    public const string RepoUrl = "https://github.com/AngryMunky/tag-hag";

    /// <summary>Civitai harvest mode is forked from the local CivitaiHarvesterApp (O3); this
    /// repo URL is the upstream reference for that engine only.</summary>
    public const string CivitaiRepoUrl = "https://github.com/AngryMunky/CivitaiHarvester";
}
