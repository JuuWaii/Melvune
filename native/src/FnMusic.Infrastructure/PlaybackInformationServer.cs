using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using FnMusic.Core;

namespace FnMusic.Infrastructure;

/// <summary>Current-user-only, bounded read-only playback information. Never touches account configuration.</summary>
[SupportedOSPlatform("windows")]
public sealed class PlaybackInformationServer : IAsyncDisposable
{
    private readonly PlaybackSnapshotHub hub;
    private readonly string discoveryDirectory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> streams = new();
    private Task[] workers = [];
    private bool started;
    private bool disposed;
    public string PipeName { get; } = "fnmusic-info-v1-" + Guid.NewGuid().ToString("N");
    public string DiscoveryPath { get; }

    public PlaybackInformationServer(PlaybackSnapshotHub hub, string directory)
    {
        this.hub = hub;
        discoveryDirectory = Path.Combine(directory, "playback-information");
        DiscoveryPath = Path.Combine(discoveryDirectory, hub.InstanceId.ToString("N") + ".json");
    }

    public async Task StartAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) return;
            Directory.CreateDirectory(discoveryDirectory);
            var initial = new List<NamedPipeServerStream>();
            string temporary = DiscoveryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                for (int i = 0; i < PlaybackInformationProtocol.MaximumClients; i++) initial.Add(CreatePipe());
                var record = new PlaybackInformationDiscovery(PlaybackSnapshotHub.ProtocolVersion, hub.InstanceId, PipeName);
                await File.WriteAllBytesAsync(temporary,
                    JsonSerializer.SerializeToUtf8Bytes(record, PlaybackInformationProtocol.JsonOptions), lifetime.Token).ConfigureAwait(false);
                File.Move(temporary, DiscoveryPath, true);
                started = true;
                workers = initial.Select(pipe => Task.Run(() => AcceptLoopAsync(pipe))).ToArray();
            }
            catch
            {
                foreach (var pipe in initial) ReleasePipe(pipe);
                TryDelete(temporary);
                throw;
            }
        }
        finally { lifecycle.Release(); }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut,
            PlaybackInformationProtocol.MaximumClients, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096,
            pipeSecurity: null, inheritability: HandleInheritability.None,
            additionalAccessRights: PipeAccessRights.ChangePermissions);
        try
        {
            // CurrentUserOnly supplies the owner ACL. Also deny Windows network logon tokens:
            // even the same account authenticated through SMB must not consume local information.
            var security = pipe.GetAccessControl();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            pipe.SetAccessControl(security);
            streams.TryAdd(pipe, 0);
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    private void ReleasePipe(NamedPipeServerStream pipe)
    {
        streams.TryRemove(pipe, out _);
        pipe.Dispose();
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream? pipe = first;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    pipe ??= CreatePipe();
                    await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    await ServeAsync(pipe, lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException
                    or ObjectDisposedException or JsonException or ArgumentException)
                {
                    // Neither raw exceptions nor client input go to UI/logs/wire.
                    if (lifetime.IsCancellationRequested) break;
                }
                finally { if (pipe is not null) { ReleasePipe(pipe); pipe = null; } }
            }
        }
        finally { if (pipe is not null) ReleasePipe(pipe); }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        long windowStart = Environment.TickCount64;
        int windowRequests = 0;
        for (int count = 0; count < PlaybackInformationProtocol.MaximumRequestsPerConnection; count++)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var bytes = await PlaybackInformationProtocol.ReadFrameAsync(pipe,
                PlaybackInformationProtocol.MaximumRequestBytes, readTimeout.Token).ConfigureAwait(false);
            if (bytes is null) return;
            PlaybackInformationRequest request;
            try { request = PlaybackInformationProtocol.ParseRequest(bytes); }
            catch (Exception ex) when (ex is JsonException or System.Text.DecoderFallbackException)
            {
                await WriteAsync(pipe, Error(null, "invalidRequest"), cancellationToken).ConfigureAwait(false);
                return;
            }
            long now = Environment.TickCount64;
            if (now - windowStart >= 1000) { windowStart = now; windowRequests = 0; }
            if (++windowRequests > 20)
            {
                await WriteAsync(pipe, Error(request.Id, "rateLimited"), cancellationToken).ConfigureAwait(false);
                return;
            }
            if (request.Version != PlaybackSnapshotHub.ProtocolVersion)
            {
                await WriteAsync(pipe, Error(request.Id, "unsupportedVersion"), cancellationToken).ConfigureAwait(false);
                continue;
            }
            switch (request.Command)
            {
                case "snapshot":
                    await WriteAsync(pipe, Success(request.Id, "snapshot", hub.Current), cancellationToken).ConfigureAwait(false);
                    break;
                case "lyrics":
                    await WriteLyricsAsync(pipe, request, cancellationToken).ConfigureAwait(false);
                    break;
                case "subscribe":
                    await SubscribeAsync(pipe, request.Id, cancellationToken).ConfigureAwait(false);
                    return;
                default:
                    await WriteAsync(pipe, Error(request.Id, "unknownCommand"), cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task WriteLyricsAsync(Stream pipe, PlaybackInformationRequest request, CancellationToken cancellationToken)
    {
        if (request.Offset < 0 || request.Limit is < 1 or > PlaybackInformationProtocol.MaximumPageLines)
        {
            await WriteAsync(pipe, Error(request.Id, "invalidPage"), cancellationToken).ConfigureAwait(false);
            return;
        }
        var lyrics = hub.Lyrics;
        // Lyrics that have not caught up to the actual track must never describe the previous track.
        var current = hub.Current;
        if (lyrics.TrackGeneration != current.TrackGeneration)
            lyrics = new(current.TrackGeneration, current.LyricsKind, Array.Empty<LyricLine>(), false);
        if (request.TrackGeneration is not null && request.TrackGeneration != lyrics.TrackGeneration)
        {
            await WriteAsync(pipe, Error(request.Id, "trackChanged"), cancellationToken).ConfigureAwait(false);
            return;
        }
        int offset = Math.Min(request.Offset, lyrics.Lines.Count);
        int size = Math.Min(request.Limit, lyrics.Lines.Count - offset);
        while (true)
        {
            var page = new PlaybackInformationLyricsPage(lyrics.TrackGeneration, lyrics.Kind, offset, lyrics.Lines.Count,
                offset + size < lyrics.Lines.Count ? offset + size : null, lyrics.IsLimited,
                lyrics.Lines.Skip(offset).Take(size).ToArray());
            var response = Success(request.Id, "lyrics", page);
            // JSON escaping can expand text sixfold. Page by serialized bytes, not characters alone.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, PlaybackInformationProtocol.JsonOptions);
            if (bytes.Length <= PlaybackInformationProtocol.MaximumResponseBytes)
            {
                await WriteBytesAsync(pipe, bytes, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (size <= 1) throw new InvalidDataException("Bounded lyric line exceeds frame.");
            size = Math.Max(1, size / 2);
        }
    }

    private async Task SubscribeAsync(Stream pipe, string? id, CancellationToken cancellationToken)
    {
        var changes = Channel.CreateBounded<PlaybackSnapshot>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        void OnChanged(PlaybackSnapshot snapshot) => changes.Writer.TryWrite(snapshot);
        hub.Changed += OnChanged;
        try
        {
            changes.Writer.TryWrite(hub.Current);
            while (!cancellationToken.IsCancellationRequested)
            {
                PlaybackSnapshot snapshot;
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(5));
                try { snapshot = await changes.Reader.ReadAsync(heartbeat.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { snapshot = hub.Current; }
                // Stop waiting subscriptions from exhausting slots when clients disconnect; heartbeat detects it.
                await WriteAsync(pipe, Success(id, "snapshot", snapshot), cancellationToken).ConfigureAwait(false);
            }
        }
        finally { hub.Changed -= OnChanged; changes.Writer.TryComplete(); }
    }

    private static PlaybackInformationResponse Success(string? id, string type, object data) => new(1, id, true, type, data);
    private static PlaybackInformationResponse Error(string? id, string error) => new(1, id, false, "error", null, error);
    private static Task WriteAsync(Stream pipe, PlaybackInformationResponse response, CancellationToken cancellationToken) =>
        WriteBytesAsync(pipe, PlaybackInformationProtocol.Serialize(response), cancellationToken);
    private static async Task WriteBytesAsync(Stream pipe, byte[] bytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { (byte)'\n' }, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            foreach (var pipe in streams.Keys) pipe.Dispose();
            // Remove discovery before the first incomplete await: WinUI Closed can end the process
            // while asynchronous worker cleanup is still in flight. Read at most 4097 bytes.
            try
            {
                if (File.Exists(DiscoveryPath))
                {
                    using var file = File.OpenRead(DiscoveryPath);
                    var bounded = new byte[4097];
                    int read = file.ReadAtLeast(bounded, bounded.Length, throwOnEndOfStream: false);
                    if (read <= 4096)
                    {
                        var record = JsonSerializer.Deserialize<PlaybackInformationDiscovery>(bounded.AsSpan(0, read), PlaybackInformationProtocol.JsonOptions);
                        file.Dispose();
                        if (record?.InstanceId == hub.InstanceId && record.PipeName == PipeName) TryDelete(DiscoveryPath);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            lifetime.Dispose();
        }
        finally { lifecycle.Release(); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
