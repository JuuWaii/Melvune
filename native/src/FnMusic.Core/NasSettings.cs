namespace FnMusic.Core;

public sealed record NasServerSettings(Guid SourceInstanceId, string Name, string Language);
public sealed record NasSharedLibrary(MusicLibrary Library, string Path, bool AutoDownloadLyric, string MetadataPreference);
public sealed record NasDefaultLibraryAccess(Guid SourceInstanceId, string Mode, IReadOnlyList<string> LibraryIds);
public sealed record NasLibraryReconnectCheck(NasSharedLibrary Original, string Path, bool MatchAccepted, bool RequiresConfirmation)
{
    public bool NeedsConfirmation => RequiresConfirmation || !MatchAccepted;
}
public sealed record NasLibraryReconnectResult(NasSharedLibrary Library, bool ScanStarted);
public sealed class RemoteSettingsConflictException() : IOException("Remote settings changed. Refresh before saving.");
