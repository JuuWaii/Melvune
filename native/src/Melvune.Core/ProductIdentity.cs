namespace Melvune.Core;

/// <summary>
/// Current application, storage and transport identities. This dependency-free file
/// can also be linked into independent playback-information consumers.
/// </summary>
public static class ProductIdentity
{
    public const string DataDirectoryName = "Melvune.Native";
    public const string SourceAccountDomain = "melvune";
    public const string NavidromeCredentialDomain = "melvune:navidrome";
    public const string EmbyCredentialDomain = "melvune:emby";
    public const string EmbyDeviceId = "Melvune-desktop";
    // Identifies the external NAS music provider, independently of this application's brand.
    public const string NasProviderId = "fnos-music";
    public const string PlaybackPipePrefix = "melvune-info-v1-";

    /// <summary>Use a separate Melvune profile unless an absolute profile was explicitly selected.</summary>
    public static string ResolveDataDirectory(string localApplicationData, string? explicitDirectory = null)
    {
        if (explicitDirectory is not null)
        {
            if (!Path.IsPathFullyQualified(explicitDirectory))
                throw new ArgumentException("The data directory must be an absolute path.", nameof(explicitDirectory));
            return Path.GetFullPath(explicitDirectory);
        }
        if (!Path.IsPathFullyQualified(localApplicationData))
            throw new ArgumentException("The application data root must be an absolute path.", nameof(localApplicationData));
        return Path.Combine(localApplicationData, DataDirectoryName);
    }

    public static bool IsPlaybackPipeName(string? name) => name is not null
        && name.StartsWith(PlaybackPipePrefix, StringComparison.Ordinal)
        && name.Length == PlaybackPipePrefix.Length + 32
        && Guid.TryParseExact(name[PlaybackPipePrefix.Length..], "N", out _);
}
