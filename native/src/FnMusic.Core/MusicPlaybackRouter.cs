namespace FnMusic.Core;

public interface IMusicPlaybackSource
{
    MusicSource Source { get; }
    Task<Stream> OpenAsync(TrackReference track, CancellationToken ct);
}

/// <summary>按来源精确路由；来源的连接及凭据由注册方管理。</summary>
public sealed class MusicPlaybackRouter
{
    private sealed record Registration(IMusicPlaybackSource Provider, MusicSource Source, CancellationTokenSource Lifetime);
    private readonly object gate = new();
    private readonly Dictionary<Guid, Registration> sources = [];

    public void Register(IMusicPlaybackSource provider)
    {
        var source = provider.Source;
        if (source.Id == Guid.Empty) throw new ArgumentException("A persistent source identity is required");
        lock (gate)
        {
            if (sources.ContainsKey(source.Id)) throw new InvalidOperationException("Source already registered");
            sources.Add(source.Id, new Registration(provider, source, new CancellationTokenSource()));
        }
    }

    public void Unregister(Guid sourceId)
    {
        Registration? removed;
        lock (gate) sources.Remove(sourceId, out removed);
        if (removed is null) return;
        try { removed.Lifetime.Cancel(); }
        finally { removed.Lifetime.Dispose(); }
    }

    public bool CanPlay(TrackReference track)
    {
        lock (gate) return sources.TryGetValue(track.SourceInstanceId, out var entry)
            && entry.Source.Capabilities.HasFlag(MusicSourceCapabilities.Playback);
    }

    public async Task<Stream> OpenAsync(TrackReference track, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Registration entry;
        CancellationTokenSource pending;
        lock (gate)
        {
            if (!sources.TryGetValue(track.SourceInstanceId, out entry!))
                throw new MusicApiException(MusicFailure.Unavailable);
            if (!entry.Source.Capabilities.HasFlag(MusicSourceCapabilities.Playback))
                throw new MusicApiException(MusicFailure.Rejected);
            pending = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Lifetime.Token);
        }
        using (pending)
        {
            Stream stream;
            try { stream = await entry.Provider.OpenAsync(track, pending.Token).ConfigureAwait(false); }
            catch (Exception) when (pending.IsCancellationRequested || !IsCurrent(track.SourceInstanceId, entry))
            {
                ct.ThrowIfCancellationRequested();
                throw new MusicApiException(MusicFailure.Unavailable);
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                lock (gate)
                {
                    // 即使 Provider 忽略取消，也不能把旧登录实例的流交给播放器。
                    if (!sources.TryGetValue(track.SourceInstanceId, out var current) || !ReferenceEquals(entry, current))
                        throw new MusicApiException(MusicFailure.Unavailable);
                }
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
    }

    private bool IsCurrent(Guid sourceId, Registration entry)
    {
        lock (gate) return sources.TryGetValue(sourceId, out var current) && ReferenceEquals(entry, current);
    }
}
