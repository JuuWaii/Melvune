#if DEBUG
using System.Net;
using System.Text.Json;
using FnMusic.App.Services;
using FnMusic.Core;
using FnMusic.Infrastructure;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private sealed class CatalogRevocationHandler(bool emby) : HttpMessageHandler
    {
        public int StreamRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.Contains("stream", StringComparison.Ordinal)) StreamRequests++;
            return Task.FromResult(new HttpResponseMessage(emby ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized));
        }
    }

    private async Task VerifyCatalogRevocationWindowAsync()
    {
        var checks = new Dictionary<string, bool>();
        string stage = "fixture", error = "";
        var ids = new List<Guid>();
        TaskCompletionSource? release = null;
        void Check(string name, bool value)
        { checks[name] = value; if (!value) throw new InvalidOperationException(name); }
        MusicTrack[] Rows() => ((IEnumerable<MusicTrack>)TrackList.ItemsSource).ToArray();
        try
        {
            music.Player.IsMuted = true; motionPreferenceOverride = false;
            await ShowSyntheticPreviewAsync();
            string directory = Path.Combine(AppDataDirectory.Resolve(), "catalog-revocation-local");
            Directory.CreateDirectory(directory);
            using (var audio = PlaybackVerification.CreateWave())
            using (var output = File.Create(Path.Combine(directory, "local.wav"))) await audio.CopyToAsync(output);
            var library = new LocalLibrary(Guid.NewGuid(), directory, "保留本地库", "");
            var catalog = await localLibraryStore.LoadAsync();
            ApplyLocalCatalog(await localLibraryStore.SaveAsync(catalog with { Libraries = [library] }));
            var local = await LocalMusicSource.ScanAsync(library.Settings, default); ReplaceLocalSource(local);
            var localTrack = local.Tracks.Single();
            navidromeLoaded = embyLoaded = true;

            var nasHandler = new AllMusicFixtureHandler("same") { TrackCount = 2 };
            using var nas = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), nasHandler, Guid.NewGuid());
            allMusicTestClient = nas; remoteCapabilityTestSource = nas.SourceInstanceId;
            ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(nas.SourceInstanceId,
                MusicSourceCapabilities.Browse | MusicSourceCapabilities.Search));
            try
            {
                foreach (string provider in new[] { "navidrome", "subsonic", "emby" })
                {
                    stage = provider;
                    var expiredId = Guid.NewGuid(); var survivorId = Guid.NewGuid();
                    ids.Add(expiredId); ids.Add(survivorId);
                    var expired = new MusicTrack("same", provider + " 失效曲目", "验证歌手", 60, false)
                    { SourceInstanceId = expiredId, SourceLabel = provider };
                    var survivor = expired with { SourceInstanceId = survivorId, Title = "保留另一来源", SourceLabel = "保留来源" };
                    var expiredTraffic = new CatalogRevocationHandler(provider == "emby");
                    if (provider == "emby")
                    {
                        var connection = new EmbyConnection(expiredId, "失效 Emby", "https://emby.example.invalid/emby/", "fixture", "fixture-user");
                        embyConnections = embyConnections.Append(connection).ToArray();
                        RegisterEmby(connection, new EmbyClient(expiredId, connection.Name, connection.ServerAddress,
                            new EmbyCredentials(connection.UserId, "fixture-token"), () => expiredTraffic), [expired]);
                        var other = new NavidromeConnection(survivorId, "保留 Navidrome", "https://nav.example.invalid/", "fixture");
                        navidromeConnections = navidromeConnections.Append(other).ToArray();
                        RegisterNavidrome(other, new NavidromeClient(survivorId, other.Name, other.ServerAddress,
                            NavidromeCredentials.Create("fixture", "fixture-only"), () => new CatalogRevocationHandler(false)), [survivor]);
                    }
                    else
                    {
                        var connection = new NavidromeConnection(expiredId, "失效 " + provider, "https://nav.example.invalid/", "fixture") { Provider = provider };
                        navidromeConnections = navidromeConnections.Append(connection).ToArray();
                        RegisterNavidrome(connection, new NavidromeClient(expiredId, connection.Name, connection.ServerAddress,
                            NavidromeCredentials.Create("fixture", "fixture-only"), () => expiredTraffic, provider), [expired]);
                        var other = new EmbyConnection(survivorId, "保留 Emby", "https://emby.example.invalid/emby/", "fixture", "fixture-user");
                        embyConnections = embyConnections.Append(other).ToArray();
                        RegisterEmby(other, new EmbyClient(survivorId, other.Name, other.ServerAddress,
                            new EmbyCredentials(other.UserId, "fixture-token"), () => new CatalogRevocationHandler(true)), [survivor]);
                    }

                    await ShowAllMusicAsync();
                    Check(provider + "_fixture_initial_catalog", Rows().Length == 5
                        && Rows().Any(track => track.Reference == expired.Reference)
                        && Rows().Any(track => track.Reference == survivor.Reference));
                    release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    nasHandler.Pause = release.Task; nasHandler.Entered = entered;
                    var loading = LoadPageAsync(1);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Check(provider + "_nas_read_is_inflight", allMusicLoads == 1 && PageStatus.Text.StartsWith("正在", StringComparison.Ordinal));
                    queue.Replace([expired], expired.Reference);
                    await PlayTrackAsync(expired);
                    Check(provider + "_actual_stream_auth_revokes_only_target", expiredTraffic.StreamRequests == 1
                        && !ViewModel.PlaybackRouter.CanPlay(expired.Reference)
                        && ViewModel.PlaybackRouter.CanPlay(survivor.Reference)
                        && !navidromeTracks.ContainsKey(expiredId) && !embyTracks.ContainsKey(expiredId)
                        && PlaybackStatus.Text.Contains("失效", StringComparison.Ordinal));
                    release.TrySetResult(); await loading.WaitAsync(TimeSpan.FromSeconds(10));
                    nasHandler.Pause = null; nasHandler.Entered = null; release = null;
                    Check(provider + "_catalog_finishes_after_auth_revocation", allMusicLoads == 0
                        && !PageStatus.Text.StartsWith("正在", StringComparison.Ordinal)
                        && Rows().Length == 4 && total == 4
                        && Rows().All(track => track.SourceInstanceId != expiredId));
                    Check(provider + "_other_sources_and_local_remain", Rows().Any(track => track.Reference == survivor.Reference)
                        && Rows().Any(track => track.Reference == localTrack.Reference)
                        && Rows().Count(track => track.SourceInstanceId == nas.SourceInstanceId) == 2);
                    // A cache refresh may start with a list rendered before account revocation.
                    // It must use current source registrations rather than retaining that stale row.
                    TrackList.ItemsSource = Rows().Append(expired).ToArray();
                    RefreshVisibleLocalIndex();
                    Check(provider + "_background_local_refresh_drops_revoked_source", Rows().Length == 4 && total == 4
                        && Rows().All(track => track.SourceInstanceId != expiredId)
                        && Rows().Any(track => track.Reference == survivor.Reference)
                        && Rows().Any(track => track.Reference == localTrack.Reference)
                        && Rows().Count(track => track.SourceInstanceId == nas.SourceInstanceId) == 2
                        && PageStatus.Text.Contains("飞牛 2", StringComparison.Ordinal));
                    if (navidromeSources.ContainsKey(survivorId)) RemoveNavidromeSource(survivorId);
                    if (embySources.ContainsKey(survivorId)) RemoveEmbySource(survivorId);
                    navidromeConnections = []; embyConnections = []; navidromeStatus.Clear(); embyStatus.Clear();
                }
                stage = "complete";
            }
            finally { ViewModel.PlaybackRouter.Unregister(nas.SourceInstanceId); }
        }
        catch (Exception ex) { error = ex.GetType().Name + ":" + ex.Message; }
        finally
        {
            release?.TrySetResult(); allMusicTestClient = null; remoteCapabilityTestSource = null; motionPreferenceOverride = null;
            foreach (var id in ids)
            {
                if (navidromeSources.ContainsKey(id)) RemoveNavidromeSource(id);
                if (embySources.ContainsKey(id)) RemoveEmbySource(id);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(AppDataDirectory.Resolve(), "catalog-revocation-verification.json"),
            JsonSerializer.Serialize(new { stage, error, checks }));
    }
}
#endif
