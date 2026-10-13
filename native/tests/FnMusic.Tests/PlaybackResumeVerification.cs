using System.Text.Json;
using System.Text.Json.Nodes;
using FnMusic.Core;
using FnMusic.Infrastructure;

internal static class PlaybackResumeVerification
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "fnmusic-resume-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "playback-resume.json");
        var store = new PlaybackResumeStore(root);
        var track = new MusicTrack("song-one", "歌曲", "歌手", 180.5, false)
        {
            SourceInstanceId = Guid.NewGuid(), Album = "专辑", AlbumId = "album-one", CoverId = "cover-one",
            LibraryIds = ["library-one", "library-two"], Artists = [new("artist-one", "歌手")],
            SourceLabel = "Fixture music source", IsFavorite = true
        };
        try
        {
            Require(await store.LoadAsync() is null, "absent record");
            var state = new PlaybackResumeState(track, 42.75);
            await store.SaveAsync(state);
            var restored = await new PlaybackResumeStore(root).LoadAsync();
            Same(state, restored, "restart snapshot and fractional position");
            // No provider/router is required to restore an unavailable source reference.
            Require(restored!.Track.Reference == track.Reference && restored.Track.LibraryIds.SequenceEqual(track.LibraryIds)
                && restored.Track.Artists.SequenceEqual(track.Artists) && restored.Track.AlbumId == track.AlbumId
                && restored.Track.CoverId == track.CoverId && restored.Track.SourceLabel == track.SourceLabel
                && restored.Track.IsFavorite, "related metadata and unavailable identity retained");
            using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(file)))
            {
                var fields = json.RootElement.GetProperty("Track").EnumerateObject().Select(item => item.Name).ToHashSet();
                Require(fields.SetEquals(new[] { "SourceInstanceId", "Id", "Title", "Artist", "Album", "DurationSeconds",
                    "IsCue", "IsFavorite", "SourceLabel", "CoverId", "AlbumId", "LibraryIds", "Artists" }),
                    "explicit snapshot excludes paths URLs credentials and derived properties");
            }
            byte[] saved = await File.ReadAllBytesAsync(file);
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await Canceled(() => store.SaveAsync(state with { PositionSeconds = 90 }, canceled.Token));
                await Canceled(() => Task.Run(() => store.Save(state, canceled.Token)));
                await Canceled(() => store.ClearAsync(canceled.Token));
                await Canceled(() => Task.Run(() => store.Clear(canceled.Token)));
                await Canceled(() => store.LoadAsync(canceled.Token));
            }
            Require(Enumerable.SequenceEqual(saved, await File.ReadAllBytesAsync(file)), "cancel preserves previous record");
            foreach (var invalid in InvalidStates(track))
            {
                await Rejected(() => store.SaveAsync(invalid));
                await Rejected(() => Task.Run(() => store.Save(invalid)));
                Require(Enumerable.SequenceEqual(saved, await File.ReadAllBytesAsync(file)), "invalid save preserves previous record");
            }
            // A individually valid field collection can still exceed the total UTF-8 document bound.
            var oversized = state with { Track = track with { LibraryIds = Enumerable.Range(0, 100)
                .Select(index => index.ToString() + new string('x', 4000)).ToArray() } };
            await Rejected(() => store.SaveAsync(oversized));
            await Rejected(() => Task.Run(() => store.Save(oversized)));
            Require(Enumerable.SequenceEqual(saved, await File.ReadAllBytesAsync(file)), "oversize save preserves previous record");
            Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "no temporary files after rejected writes");

            store.Save(state with { PositionSeconds = 0 });
            Same(state with { PositionSeconds = 0 }, await store.LoadAsync(), "zero position survives synchronous save");
            var second = state with { Track = track with { SourceInstanceId = Guid.NewGuid() }, PositionSeconds = 12.5 };
            await store.SaveAsync(second);
            Same(second, await new PlaybackResumeStore(root).LoadAsync(), "same song ID belongs to distinct source");
            Require((await store.LoadAsync())!.Track.Reference != track.Reference, "source identity is not conflated");
            var unknownDuration = state with { Track = track with { DurationSeconds = 0 }, PositionSeconds = 0 };
            await store.SaveAsync(unknownDuration);
            Same(unknownDuration, await store.LoadAsync(), "zero duration zero position valid");
            await store.SaveAsync(state with { PositionSeconds = track.DurationSeconds });
            Same(state with { PositionSeconds = track.DurationSeconds }, await store.LoadAsync(), "duration boundary valid");

            foreach (string? cover in new string?[] { null, "", " \t " })
            {
                var noCover = state with { Track = track with { CoverId = cover } };
                await store.SaveAsync(noCover);
                var noCoverRestored = await new PlaybackResumeStore(root).LoadAsync();
                Same(state, noCoverRestored, "no-cover song retains identity and progress");
                Require(noCoverRestored!.Track.CoverId is null, "empty optional cover normalized");
                store.Save(noCover);
                Require((await store.LoadAsync())?.Track.CoverId is null, "synchronous no-cover save uses same normalization");
            }
            string longText = new('歌', 65536);
            string nasPayload = JsonSerializer.Serialize(new { code = 0, data = new
            {
                list = new[] { new { guid = "fn-song", title = longText, duration = 180500, coverId = "",
                    album = new { guid = "fn-album", name = longText },
                    artists = new[] { new { guid = "fn-artist", name = longText } } } }, total = 1
            } });
            using (var nas = new NasApiClient(ServerEndpoint.Parse("https://resume-fixture.example.invalid/music/"),
                new MetadataHandler(nasPayload), Guid.NewGuid()))
            {
                var fnTrack = (await nas.ListTracksAsync(1, 10, default)).Tracks.Single();
                Require(fnTrack.CoverId == "" && fnTrack.Title.Length == 65536, "FN parser preserves empty cover and legitimate long tags");
                await store.SaveAsync(new(fnTrack, 42.75));
                var fnRestored = await store.LoadAsync();
                Require(fnRestored is not null && fnRestored.Track.Reference == fnTrack.Reference && fnRestored.PositionSeconds == 42.75
                    && fnRestored.Track.CoverId is null && fnRestored.Track.Title.Length == 4096
                    && fnRestored.Track.Artist.Length == 4096 && fnRestored.Track.Album.Length == 4096
                    && fnRestored.Track.AlbumId == "fn-album" && fnRestored.Track.Artists.Single().Id == "fn-artist"
                    && fnRestored.Track.Artists.Single().Name.Length == 4096, "parsed FN metadata saves without losing identity or progress");
            }
            var longTags = state with { Track = track with
            {
                Title = longText, Artist = longText, Album = longText, SourceLabel = longText,
                Artists = [new("artist-one", longText)]
            } };
            var clipped = longTags with { Track = longTags.Track with
            {
                Title = longText[..4096], Artist = longText[..4096], Album = longText[..4096],
                SourceLabel = longText[..4096], Artists = [new("artist-one", longText[..4096])]
            } };
            await store.SaveAsync(longTags);
            var longRestored = await new PlaybackResumeStore(root).LoadAsync();
            Same(clipped, longRestored, "FN long tags clipped without losing source song or progress");
            Require(longRestored!.Track.SourceLabel == clipped.Track.SourceLabel
                && longRestored.Track.Artists.SequenceEqual(clipped.Track.Artists)
                && longRestored.Track.AlbumId == track.AlbumId && longRestored.Track.CoverId == track.CoverId
                && longRestored.Track.LibraryIds.SequenceEqual(track.LibraryIds), "display clipping preserves related identities");
            store.Save(longTags);
            Same(clipped, await store.LoadAsync(), "synchronous long-tag save uses same bounds");
            await store.SaveAsync(state with { PositionSeconds = track.DurationSeconds });
            saved = await File.ReadAllBytesAsync(file);
            var malformed = new List<byte[]> { Array.Empty<byte>(), System.Text.Encoding.UTF8.GetBytes("{not JSON}"), new byte[256 * 1024 + 1] };
            foreach (var mutate in new Action<JsonObject>[]
            {
                document => document["Version"] = 999,
                document => document["Track"] = null,
                document => document["PositionSeconds"] = -1,
                document => document["PositionSeconds"] = 181,
                document => document["PositionSeconds"] = "NaN",
                document => document["Track"]!["SourceInstanceId"] = Guid.Empty.ToString(),
                document => document["Track"]!["Id"] = " ",
                document => document["Track"]!["DurationSeconds"] = -1,
                document => document["Track"]!["LibraryIds"] = null,
                document => document["Track"]!["Artists"] = null,
                document => document["Track"]!["Artists"] = new JsonArray((JsonNode?)null),
                document => document["Track"]!["Album"] = null,
                document => document["Track"]!["Title"] = new string('x', 4097),
                document => document["Track"]!["Artists"]![0]!["Name"] = new string('x', 4097)
            })
            {
                var document = JsonNode.Parse(saved)!.AsObject(); mutate(document);
                malformed.Add(JsonSerializer.SerializeToUtf8Bytes(document));
            }
            foreach (var bytes in malformed)
            {
                await File.WriteAllBytesAsync(file, bytes);
                Require(await store.LoadAsync() is null, "malformed or oversized load discarded");
                Require(Enumerable.SequenceEqual(bytes, await File.ReadAllBytesAsync(file)), "corrupt input retained unchanged");
            }
            await store.SaveAsync(state);
            await store.ClearAsync();
            Require(await store.LoadAsync() is null && !File.Exists(file), "explicit asynchronous clear");
            store.Save(state); store.Clear(); store.Clear();
            Require(await store.LoadAsync() is null, "synchronous clear is idempotent");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static IEnumerable<PlaybackResumeState> InvalidStates(MusicTrack track)
    {
        foreach (double position in new[] { -0.1, double.NaN, double.PositiveInfinity, 181 })
            yield return new(track, position);
        foreach (var invalidTrack in new[]
        {
            track with { SourceInstanceId = Guid.Empty }, track with { Id = "" }, track with { Id = "x\n" },
            track with { DurationSeconds = double.NaN }, track with { DurationSeconds = double.PositiveInfinity },
            track with { DurationSeconds = -1 }, track with { DurationSeconds = 864001 },
            track with { Title = null! },
            track with { AlbumId = "" }, track with { CoverId = "cover\n" },
            track with { LibraryIds = ["duplicate", "duplicate"] }, track with { LibraryIds = null! },
            track with { Artists = [new("artist", "one"), new("artist", "two")] },
            track with { Artists = [null!] }, track with { Artists = null! }, track with { Artists = [new("artist", null!)] }
        }) yield return new(invalidTrack, 0);
    }

    private static void Same(PlaybackResumeState expected, PlaybackResumeState? actual, string context) =>
        Require(actual is not null && actual.PositionSeconds == expected.PositionSeconds && actual.Track.Reference == expected.Track.Reference
            && actual.Track.Title == expected.Track.Title && actual.Track.Artist == expected.Track.Artist
            && actual.Track.Album == expected.Track.Album && actual.Track.DurationSeconds == expected.Track.DurationSeconds, context);
    private static async Task Rejected(Func<Task> action)
    {
        try { await action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid resume state accepted.");
    }
    private static async Task Canceled(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Canceled resume operation completed.");
    }
    private static void Require(bool value, string context)
    { if (!value) throw new InvalidOperationException("Playback resume verification failed: " + context); }
    private sealed class MetadataHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
