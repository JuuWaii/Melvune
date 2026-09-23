using System.Security.Cryptography;
using System.Text;
using FnMusic.Core;

namespace FnMusic.Infrastructure;

public sealed record LocalMusicSettings(Guid SourceId, string Directory);
public sealed record LocalMusicMetadata(string Title, string Artist, double DurationSeconds)
{
    public string Album { get; init; } = "";
}

/// <summary>只播放扫描快照里的文件；曲目 ID 不接受路径或 URI。</summary>
public sealed class LocalMusicSource : IMusicPlaybackSource
{
    private readonly Dictionary<string, string> files;
    public MusicSource Source { get; }
    public IReadOnlyList<MusicTrack> Tracks { get; }
    public LocalMusicSettings Settings { get; }
    private static readonly HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".ape" };

    private LocalMusicSource(LocalMusicSettings settings, Dictionary<string, string> files, List<MusicTrack> tracks)
    {
        Settings = settings; this.files = files; Tracks = tracks.AsReadOnly();
        Source = new(settings.SourceId, "本地音乐", "local", MusicSourceCapabilities.Browse | MusicSourceCapabilities.Search | MusicSourceCapabilities.Playback);
    }

    public static Task<LocalMusicSource> ScanAsync(LocalMusicSettings settings, CancellationToken ct,
        Func<string, CancellationToken, Task<LocalMusicMetadata>>? readMetadata = null, IProgress<int>? progress = null) => Task.Run(async () =>
    {
        if (settings.SourceId == Guid.Empty || !Path.IsPathFullyQualified(settings.Directory)) throw new ArgumentException("Invalid local library");
        string root = Path.GetFullPath(settings.Directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException();
        EnsureRegularPath(root);
        var files = new Dictionary<string, string>();
        var tracks = new List<MusicTrack>();
        progress?.Report(0);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!extensions.Contains(Path.GetExtension(file))) continue;
            if (tracks.Count >= 10000) throw new InvalidDataException("Local library limit reached");
            string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file))));
            files.Add(id, file);
            string title = Path.GetFileNameWithoutExtension(file), artist = "本地文件", album = "";
            double duration = 0;
            if (readMetadata is not null)
            {
                try
                {
                    EnsureRegularPath(file);
                    var metadata = await readMetadata(file, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(metadata.Title)) title = metadata.Title;
                    if (!string.IsNullOrWhiteSpace(metadata.Artist)) artist = metadata.Artist;
                    album = metadata.Album;
                    if (double.IsFinite(metadata.DurationSeconds) && metadata.DurationSeconds > 0) duration = Math.Min(metadata.DurationSeconds, 864000);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { /* 不支持的标签不能阻止文件被浏览和播放。 */ }
            }
            tracks.Add(new(id, title, artist, duration, false) { SourceInstanceId = settings.SourceId, Album = album });
            ct.ThrowIfCancellationRequested();
            if (tracks.Count % 25 == 0) progress?.Report(tracks.Count);
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(tracks.Count);
        tracks.Sort((a, b) => { int title = StringComparer.OrdinalIgnoreCase.Compare(a.Title, b.Title); return title != 0 ? title : StringComparer.Ordinal.Compare(a.Id, b.Id); });
        return new LocalMusicSource(settings with { Directory = root }, files, tracks);
    }, ct);

    public Task<Stream> OpenAsync(TrackReference track, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string file = GetFilePath(track);
        return Task.FromResult<Stream>(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous));
    }
    public string GetFilePath(TrackReference track)
    {
        if (track.SourceInstanceId != Source.Id || !files.TryGetValue(track.TrackId, out var file))
            throw new MusicApiException(MusicFailure.Rejected);
        EnsureRegularPath(file);
        return file;
    }
    public async Task<Lyrics> GetLyricsAsync(TrackReference track, CancellationToken ct)
    {
        string sidecar = Path.ChangeExtension(GetFilePath(track), ".lrc");
        var bytes = await ReadSidecarAsync(sidecar, 512000, ct);
        if (bytes is null) return Lyrics.Empty;
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return Lyrics.Parse(await reader.ReadToEndAsync(ct));
    }
    public async Task<byte[]?> GetSidecarCoverAsync(TrackReference track, CancellationToken ct)
    {
        string file = GetFilePath(track), folder = Path.GetDirectoryName(file)!;
        foreach (string path in new[] { Path.ChangeExtension(file, ".jpg"), Path.ChangeExtension(file, ".png"), Path.Combine(folder, "cover.jpg"), Path.Combine(folder, "cover.png"), Path.Combine(folder, "folder.jpg") })
        {
            var bytes = await ReadSidecarAsync(path, 4 * 1024 * 1024, ct);
            if (bytes is not null) return bytes;
        }
        return null;
    }
    private static async Task<byte[]?> ReadSidecarAsync(string path, int limit, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        EnsureRegularPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > limit) throw new InvalidDataException();
        using var output = new MemoryStream(); byte[] buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void EnsureRegularPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths are not supported");
    }
}
