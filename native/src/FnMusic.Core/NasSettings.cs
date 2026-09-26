namespace FnMusic.Core;

public sealed record NasServerSettings(Guid SourceInstanceId, string Name, string Language);
public sealed record NasSharedLibrary(MusicLibrary Library, string Path, bool AutoDownloadLyric, string MetadataPreference);
public sealed record NasDefaultLibraryAccess(Guid SourceInstanceId, string Mode, IReadOnlyList<string> LibraryIds);
public sealed class RemoteSettingsConflictException() : IOException("Remote settings changed. Refresh before saving.");
