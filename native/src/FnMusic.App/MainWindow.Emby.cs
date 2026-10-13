using FnMusic.Core;
using FnMusic.Infrastructure;
using FnMusic.Windows;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private readonly EmbyConnectionStore embyStore = new(ThemeSettingsDirectory(), new DpapiSessionVault(Path.Combine(ThemeSettingsDirectory(), "emby-secrets")));
    private readonly Dictionary<Guid, EmbyClient> embySources = [];
    private readonly Dictionary<Guid, IReadOnlyList<MusicTrack>> embyTracks = [];
    private readonly Dictionary<Guid, string> embyStatus = [];
    private IReadOnlyList<EmbyConnection> embyConnections = [];
    private readonly SemaphoreSlim embyGate = new(1);
    private bool embyLoaded;
    private long embyRevision;
#if DEBUG
    private Func<EmbyConnection, EmbyCredentials, EmbyClient>? embyTestFactory;
    private Func<string, string, string, CancellationToken, Task<EmbyCredentials>>? embyAuthTestFactory;
#endif
    private EmbyClient CreateEmby(EmbyConnection connection, EmbyCredentials credentials) =>
#if DEBUG
        embyTestFactory?.Invoke(connection, credentials) ??
#endif
        new(connection.SourceId, connection.Name, connection.ServerAddress, credentials);


    private bool CurrentEmby(EmbyClient client) => !closed && ReferenceEquals(embySources.GetValueOrDefault(client.Source.Id), client);

    private async Task RefreshEmbyAsync(CancellationToken ct, bool force = false)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, localLifetime.Token);
        ct = lifetime.Token;
        await embyGate.WaitAsync(ct);
        try
        {
            if (embyLoaded && !force) return;
            var connections = await embyStore.LoadAsync(ct);
            foreach (var id in embySources.Keys.Where(id => !connections.Any(item => item.SourceId == id)).ToArray()) RemoveEmbySource(id);
            embyConnections = connections;
            foreach (var connection in connections)
            {
                ct.ThrowIfCancellationRequested();
                EmbyClient? candidate = null;
                try
                {
                    var credentials = await embyStore.LoadCredentialsAsync(connection.SourceId, ct);
                    if (credentials is null) { RemoveEmbySource(connection.SourceId); embyStatus[connection.SourceId] = "需要重新输入密码"; continue; }
                    candidate = CreateEmby(connection, credentials);
                    var tracks = await candidate.LoadTracksAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    RegisterEmby(connection, candidate, tracks); candidate = null;
                }
                catch (OperationCanceledException) { throw; }
                catch (MusicApiException ex)
                {
                    // Offline retains the last complete catalog. A rejected account is revoked independently of FNMusic.
                    if (ex.Failure == MusicFailure.Unauthorized) RemoveEmbySource(connection.SourceId);
                    embyStatus[connection.SourceId] = ex.Failure == MusicFailure.Unauthorized ? "认证失效，请编辑后重新输入密码" : "读取失败，刷新重试";
                }
                catch (Exception) { embyStatus[connection.SourceId] = "配置或连接不可用，请编辑后重试"; }
                finally { candidate?.Dispose(); }
            }
            embyLoaded = true; embyRevision++;
        }
        finally { embyGate.Release(); }
    }

    private void RegisterEmby(EmbyConnection connection, EmbyClient client, IReadOnlyList<MusicTrack> tracks)
    {
        if (closed) { client.Dispose(); return; }
        // Reconnecting the same account keeps track identity and its already-open stream.
        if (embySources.Remove(connection.SourceId, out var previous))
        { ViewModel.PlaybackRouter.Unregister(connection.SourceId); previous.Dispose(); }
        embySources[connection.SourceId] = client;
        embyTracks[connection.SourceId] = tracks.Select(track => track with { SourceLabel = connection.Name }).ToArray();
        ViewModel.PlaybackRouter.Register(client); embyStatus.Remove(connection.SourceId); embyRevision++; coverCache.Clear();
    }

    private void RemoveEmbySource(Guid id)
    {
        if (queue.Current?.SourceInstanceId == id) StopUnavailableSource(id);
        foreach (var track in queue.Tracks.Where(track => track.SourceInstanceId == id).ToArray()) queue.Remove(track.Reference);
        ViewModel.PlaybackRouter.Unregister(id);
        if (embySources.Remove(id, out var previous)) previous.Dispose();
        embyTracks.Remove(id); embyRevision++; coverCache.Clear(); UpdateQueueStatus();
    }

    private Task<EmbyCredentials> AuthenticateEmbyAsync(string server, string username, string password, CancellationToken ct) =>
#if DEBUG
        embyAuthTestFactory?.Invoke(server, username, password, ct) ??
#endif
        EmbyClient.AuthenticateAsync(server, username, password, ct);

    private async Task SaveEmbyAsync(LibraryManagerRow? selected, LibraryEditRequest request, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, localLifetime.Token);
        ct = lifetime.Token;
        await embyGate.WaitAsync(ct);
        EmbyClient? candidate = null;
        try
        {
            var existing = selected?.Emby;
            string server = EmbyClient.NormalizeServerAddress(request.Server);
            string username = request.Username.Trim();
            if (existing is not null && (server != existing.ServerAddress || username != existing.Username))
                throw new InvalidOperationException("A different server or account requires a new music library.");
            var credentials = request.Password.Length > 0 ? await AuthenticateEmbyAsync(request.Server, request.Username, request.Password, ct)
                : existing is not null ? await embyStore.LoadCredentialsAsync(existing.SourceId, ct) : null;
            if (credentials is null) throw new MusicApiException(MusicFailure.Unauthorized);
            if (existing is not null && existing.UserId != credentials.UserId)
                throw new InvalidOperationException("A different user requires a new music library.");
            var connection = new EmbyConnection(existing?.SourceId ?? Guid.NewGuid(), request.Name,
                server, username, credentials.UserId);
            candidate = CreateEmby(connection, credentials);
            await candidate.CheckConnectionAsync(ct);
            var tracks = await candidate.LoadTracksAsync(ct);
            await embyStore.SaveAsync(connection, credentials, ct);
            // The persisted connection is committed; mirror it even if cancellation arrived after commit.
            embyConnections = embyConnections.Where(item => item.SourceId != connection.SourceId)
                .Append(EmbyConnectionStore.NormalizeConnection(connection)).ToArray();
            if (!closed) { RegisterEmby(connection, candidate, tracks); candidate = null; embyLoaded = true; }
        }
        finally { candidate?.Dispose(); embyGate.Release(); }
    }

    private async Task DeleteEmbyAsync(Guid id, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, localLifetime.Token);
        ct = lifetime.Token;
        await embyGate.WaitAsync(ct);
        try
        {
            await embyStore.RemoveAsync(id, ct);
            embyConnections = embyConnections.Where(item => item.SourceId != id).ToArray();
            if (!closed) { RemoveEmbySource(id); embyStatus.Remove(id); }
        }
        finally { embyGate.Release(); }
    }

    private async Task RestoreEmbyAtStartupAsync()
    {
        try { await RefreshEmbyAsync(localLifetime.Token); if (!closed && allMusicView) await LoadPageAsync(1); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) ShowMetadataNotice("Emby 配置读取失败，可在音乐库管理中重试。"); }
    }

    private void CloseEmby()
    {
        foreach (var client in embySources.Values) { ViewModel.PlaybackRouter.Unregister(client.Source.Id); client.Dispose(); }
        embySources.Clear(); embyTracks.Clear();
    }
}
