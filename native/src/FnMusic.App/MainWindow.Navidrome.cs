using FnMusic.Core;
using FnMusic.Infrastructure;
using FnMusic.Windows;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private readonly NavidromeConnectionStore navidromeStore = new(ThemeSettingsDirectory(), new DpapiSessionVault(Path.Combine(ThemeSettingsDirectory(), "navidrome-secrets")));
    private readonly Dictionary<Guid, NavidromeClient> navidromeSources = [];
    private readonly Dictionary<Guid, IReadOnlyList<MusicTrack>> navidromeTracks = [];
    private readonly Dictionary<Guid, string> navidromeStatus = [];
    private IReadOnlyList<NavidromeConnection> navidromeConnections = [];
    private readonly SemaphoreSlim navidromeGate = new(1);
    private bool navidromeLoaded;
    private long navidromeRevision;
#if DEBUG
    private Func<NavidromeConnection, NavidromeCredentials, NavidromeClient>? navidromeTestFactory;
    private Action? navidromeAfterCommit;
#endif
    private NavidromeClient CreateNavidrome(NavidromeConnection connection, NavidromeCredentials credentials) =>
#if DEBUG
        navidromeTestFactory?.Invoke(connection, credentials) ??
#endif
        new(connection.SourceId, connection.Name, connection.ServerAddress, credentials, provider: connection.Provider);

    private bool IndependentSource(Guid id) => localSources.ContainsKey(id) || navidromeSources.ContainsKey(id) || embySources.ContainsKey(id) || httpJsonSources.ContainsKey(id);
    private bool CurrentNavidrome(NavidromeClient client) => !closed && ReferenceEquals(navidromeSources.GetValueOrDefault(client.Source.Id), client);

    private async Task RefreshNavidromeAsync(CancellationToken ct, bool force = false)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, localLifetime.Token);
        ct = lifetime.Token;
        await navidromeGate.WaitAsync(ct);
        try
        {
            if (navidromeLoaded && !force) return;
            var connections = await navidromeStore.LoadAsync(ct);
            foreach (var id in navidromeSources.Keys.Where(id => !connections.Any(item => item.SourceId == id)).ToArray()) RemoveNavidromeSource(id);
            navidromeConnections = connections;
            foreach (var connection in connections)
            {
                ct.ThrowIfCancellationRequested();
                NavidromeClient? candidate = null;
                try
                {
                    var credentials = await navidromeStore.LoadCredentialsAsync(connection.SourceId, ct);
                    if (credentials is null) { RemoveNavidromeSource(connection.SourceId); navidromeStatus[connection.SourceId] = "需要重新输入密码"; continue; }
                    candidate = CreateNavidrome(connection, credentials);
                    var tracks = await candidate.LoadTracksAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    RegisterNavidrome(connection, candidate, tracks); candidate = null;
                }
                catch (OperationCanceledException) { throw; }
                catch (MusicApiException ex)
                {
                    // Offline retains the last complete catalog. A rejected account is revoked independently of FNMusic.
                    if (ex.Failure == MusicFailure.Unauthorized) RemoveNavidromeSource(connection.SourceId);
                    navidromeStatus[connection.SourceId] = ex.Failure == MusicFailure.Unauthorized ? "认证失效，请编辑后重新输入密码" : "读取失败，刷新重试";
                }
                catch (Exception) { navidromeStatus[connection.SourceId] = "配置或连接不可用，请编辑后重试"; }
                finally { candidate?.Dispose(); }
            }
            navidromeLoaded = true; navidromeRevision++;
        }
        finally { navidromeGate.Release(); }
    }

    private void RegisterNavidrome(NavidromeConnection connection, NavidromeClient client, IReadOnlyList<MusicTrack> tracks)
    {
        if (closed) { client.Dispose(); return; }
        // Reconnecting the same account keeps track identity and its already-open stream.
        if (navidromeSources.Remove(connection.SourceId, out var previous))
        { ViewModel.PlaybackRouter.Unregister(connection.SourceId); previous.Dispose(); }
        navidromeSources[connection.SourceId] = client;
        navidromeTracks[connection.SourceId] = tracks.Select(track => track with { SourceLabel = connection.Name }).ToArray();
        ViewModel.PlaybackRouter.Register(client); navidromeStatus.Remove(connection.SourceId); navidromeRevision++; coverCache.Clear();
    }

    private void RemoveNavidromeSource(Guid id)
    {
        if (queue.Current?.SourceInstanceId == id) StopUnavailableSource(id);
        foreach (var track in queue.Tracks.Where(track => track.SourceInstanceId == id).ToArray()) queue.Remove(track.Reference);
        ViewModel.PlaybackRouter.Unregister(id);
        if (navidromeSources.Remove(id, out var previous)) previous.Dispose();
        navidromeTracks.Remove(id); navidromeRevision++; coverCache.Clear(); UpdateQueueStatus();
    }

    private async Task SaveNavidromeAsync(LibraryManagerRow? selected, LibraryEditRequest request, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, localLifetime.Token);
        ct = lifetime.Token;
        await navidromeGate.WaitAsync(ct);
        NavidromeClient? candidate = null;
        try
        {
            var existing = selected?.Navidrome;
            string provider = request.Kind == 4 ? "subsonic" : "navidrome";
            string server = NavidromeClient.NormalizeServerAddress(request.Server);
            string username = request.Username.Trim();
            // Reject account changes before an existing credential can reach another endpoint.
            if (existing is not null && (existing.Provider != provider || existing.ServerAddress != server || existing.Username != username))
                throw new InvalidOperationException("更换服务、账号或类型需新建音乐库。");
            var connection = NavidromeConnectionStore.NormalizeConnection(new NavidromeConnection(existing?.SourceId ?? Guid.NewGuid(), request.Name,
                server, username) { Provider = provider });
            var credentials = request.Password.Length > 0 ? NavidromeClient.CreateCredentials(request.Username, request.Password)
                : existing is not null ? await navidromeStore.LoadCredentialsAsync(existing.SourceId, ct) : null;
            if (credentials is null) throw new MusicApiException(MusicFailure.Unauthorized);
            candidate = CreateNavidrome(connection, credentials);
            await candidate.CheckConnectionAsync(ct);
            var tracks = await candidate.LoadTracksAsync(ct);
            await navidromeStore.SaveAsync(connection, credentials, ct);
#if DEBUG
            navidromeAfterCommit?.Invoke();
#endif
            // The persisted connection is committed; mirror it even if cancellation arrived after commit.
            navidromeConnections = navidromeConnections.Where(item => item.SourceId != connection.SourceId).Append(connection).ToArray();
            if (!closed) { RegisterNavidrome(connection, candidate, tracks); candidate = null; navidromeLoaded = true; }
        }
        finally { candidate?.Dispose(); navidromeGate.Release(); }
    }

    private async Task DeleteNavidromeAsync(Guid id, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, localLifetime.Token);
        ct = lifetime.Token;
        await navidromeGate.WaitAsync(ct);
        try
        {
            await navidromeStore.RemoveAsync(id, ct);
#if DEBUG
            navidromeAfterCommit?.Invoke();
#endif
            navidromeConnections = navidromeConnections.Where(item => item.SourceId != id).ToArray();
            if (!closed) { RemoveNavidromeSource(id); navidromeStatus.Remove(id); }
        }
        finally { navidromeGate.Release(); }
    }

    private async Task RestoreNavidromeAtStartupAsync()
    {
        try { await RefreshNavidromeAsync(localLifetime.Token); if (!closed && allMusicView) await LoadPageAsync(1); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) ShowMetadataNotice("Navidrome / Subsonic 配置读取失败，可在音乐库管理中重试。"); }
    }

    private void CloseNavidrome()
    {
        foreach (var client in navidromeSources.Values) { ViewModel.PlaybackRouter.Unregister(client.Source.Id); client.Dispose(); }
        navidromeSources.Clear(); navidromeTracks.Clear();
    }
}
