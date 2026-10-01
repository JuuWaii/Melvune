using FnMusic.Core;

namespace FnMusic.Infrastructure;

public sealed class NasPlaybackSource : IMusicPlaybackSource
{
    private readonly NasApiClient client;
    public MusicSource Source { get; }

    public NasPlaybackSource(NasApiClient client)
    {
        if (client.SourceInstanceId == Guid.Empty) throw new ArgumentException("An account-scoped client is required");
        this.client = client;
        Source = new MusicSource(client.SourceInstanceId, "飞牛音乐", "fnmusic",
            MusicSourceCapabilities.Browse | MusicSourceCapabilities.Search | MusicSourceCapabilities.Playback |
            MusicSourceCapabilities.ReadFavorites | MusicSourceCapabilities.WriteFavorites |
            MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.WritePlaylists |
            MusicSourceCapabilities.ReadSettings | MusicSourceCapabilities.WriteSettings |
            MusicSourceCapabilities.Artwork | MusicSourceCapabilities.Lyrics);
    }

    public async Task<Stream> OpenAsync(TrackReference track, CancellationToken ct) =>
        await client.OpenTrackStreamAsync(track, ct).ConfigureAwait(false);
}
