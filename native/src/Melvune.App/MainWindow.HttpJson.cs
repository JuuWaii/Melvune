using Melvune.Core;
using Melvune.Infrastructure;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private readonly HttpJsonConnectionStore httpJsonStore = new(ThemeSettingsDirectory());
    private readonly Dictionary<Guid, HttpJsonClient> httpJsonSources = [];
    private readonly Dictionary<Guid, IReadOnlyList<MusicTrack>> httpJsonTracks = [];
    private readonly Dictionary<Guid, string> httpJsonStatus = [];
    private IReadOnlyList<HttpJsonConnection> httpJsonConnections = [];
    private readonly SemaphoreSlim httpJsonGate = new(1);
    private bool httpJsonLoaded;
#if DEBUG
    private Func<HttpJsonConnection, HttpJsonClient>? httpJsonTestFactory;
    private Action? httpJsonAfterCommit;
#endif
    private HttpJsonClient CreateHttpJson(HttpJsonConnection connection) =>
#if DEBUG
        httpJsonTestFactory?.Invoke(connection) ??
#endif
        new(connection.SourceId, connection.Name, connection.ServerAddress, connection.Definition);

    private bool CurrentHttpJson(HttpJsonClient client) => !closed && ReferenceEquals(httpJsonSources.GetValueOrDefault(client.Source.Id), client);

    private async Task RefreshHttpJsonAsync(CancellationToken ct, bool force = false)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, localLifetime.Token);
        ct = lifetime.Token;
        await httpJsonGate.WaitAsync(ct);
        try
        {
            if (httpJsonLoaded && !force) return;
            var connections = await httpJsonStore.LoadAsync(ct);
            ct.ThrowIfCancellationRequested();
            foreach (var id in httpJsonSources.Keys.Where(id => !connections.Any(item => item.SourceId == id)).ToArray()) RemoveHttpJsonSource(id);
            httpJsonConnections = connections;
            foreach (var connection in connections)
            {
                ct.ThrowIfCancellationRequested();
                HttpJsonClient? candidate = null;
                try
                {
                    candidate = CreateHttpJson(connection);
                    var tracks = await candidate.LoadTracksAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    RegisterHttpJson(connection, candidate, tracks); candidate = null;
                }
                catch (OperationCanceledException) { throw; }
                catch (MusicApiException ex)
                {
                    if (ex.Failure == MusicFailure.Unauthorized) RemoveHttpJsonSource(connection.SourceId);
                    httpJsonStatus[connection.SourceId] = ex.Failure == MusicFailure.Unauthorized
                        ? "服务需要认证；当前仅支持匿名接口" : "读取失败，检查字段方案并刷新重试";
                }
                catch (Exception) { httpJsonStatus[connection.SourceId] = "配置或连接不可用，请检查服务与字段方案"; }
                finally { candidate?.Dispose(); }
            }
            httpJsonLoaded = true;
        }
        finally { httpJsonGate.Release(); }
    }

    private void RegisterHttpJson(HttpJsonConnection connection, HttpJsonClient client, IReadOnlyList<MusicTrack> tracks)
    {
        if (closed) { client.Dispose(); return; }
        if (httpJsonSources.Remove(connection.SourceId, out var previous))
        { ViewModel.PlaybackRouter.Unregister(connection.SourceId); previous.Dispose(); }
        httpJsonSources[connection.SourceId] = client;
        httpJsonTracks[connection.SourceId] = tracks.Select(track => track with { SourceLabel = connection.Name }).ToArray();
        ViewModel.PlaybackRouter.Register(client); httpJsonStatus.Remove(connection.SourceId); coverCache.Clear();
    }

    private void RemoveHttpJsonSource(Guid id)
    {
        if (queue.Current?.SourceInstanceId == id) StopUnavailableSource(id);
        foreach (var track in queue.Tracks.Where(track => track.SourceInstanceId == id).ToArray()) queue.Remove(track.Reference);
        ViewModel.PlaybackRouter.Unregister(id);
        if (httpJsonSources.Remove(id, out var previous)) previous.Dispose();
        httpJsonTracks.Remove(id); coverCache.Clear(); UpdateQueueStatus();
    }

    private async Task SaveHttpJsonAsync(LibraryManagerRow? selected, LibraryEditRequest request, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, localLifetime.Token);
        ct = lifetime.Token;
        await httpJsonGate.WaitAsync(ct);
        HttpJsonClient? candidate = null;
        try
        {
            var existing = selected?.HttpJson;
            var connection = HttpJsonConnectionStore.NormalizeConnection(new(existing?.SourceId ?? Guid.NewGuid(), request.Name,
                request.Server, HttpJsonDefinition.Parse(request.JsonDefinition)));
            // A changed route or mapping can assign old song IDs to different media. Require a new identity before HTTP.
            if (existing is not null && (connection.ServerAddress != existing.ServerAddress || connection.Definition != existing.Definition))
                throw new InvalidOperationException("修改服务或字段方案需新建音乐库。");
            candidate = CreateHttpJson(connection);
            var tracks = await candidate.LoadTracksAsync(ct);
            await httpJsonStore.SaveAsync(connection, ct);
#if DEBUG
            httpJsonAfterCommit?.Invoke();
#endif
            httpJsonConnections = httpJsonConnections.Where(item => item.SourceId != connection.SourceId).Append(connection).ToArray();
            if (!closed) { RegisterHttpJson(connection, candidate, tracks); candidate = null; httpJsonLoaded = true; }
        }
        finally { candidate?.Dispose(); httpJsonGate.Release(); }
    }

    private async Task DeleteHttpJsonAsync(Guid id, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, localLifetime.Token);
        ct = lifetime.Token;
        await httpJsonGate.WaitAsync(ct);
        try
        {
            await httpJsonStore.RemoveAsync(id, ct);
#if DEBUG
            httpJsonAfterCommit?.Invoke();
#endif
            httpJsonConnections = httpJsonConnections.Where(item => item.SourceId != id).ToArray();
            if (!closed) { RemoveHttpJsonSource(id); httpJsonStatus.Remove(id); }
        }
        finally { httpJsonGate.Release(); }
    }

    private async Task RestoreHttpJsonAtStartupAsync()
    {
        try { await RefreshHttpJsonAsync(localLifetime.Token); if (!closed && allMusicView) await LoadPageAsync(1); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) ShowMetadataNotice("自定义 JSON 音乐源读取失败，可在音乐库管理中重试。"); }
    }

    private void CloseHttpJson()
    {
        foreach (var client in httpJsonSources.Values) { ViewModel.PlaybackRouter.Unregister(client.Source.Id); client.Dispose(); }
        httpJsonSources.Clear(); httpJsonTracks.Clear();
    }
}
