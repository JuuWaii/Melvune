using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Melvune.Core;
using Melvune.Infrastructure;

internal static class PlaybackSessionVerification
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "melvune-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "playback-resume.json");
        var store = new PlaybackResumeStore(root);
        var source = Guid.NewGuid();
        MusicTrack Song(string id, Guid? owner = null) => new(id, "歌曲 " + id, "歌手", 180.5, false)
        {
            SourceInstanceId = owner ?? source, Album = "专辑", AlbumId = "album", CoverId = "cover",
            Artists = [new("artist", "歌手")], LibraryIds = ["library"], SourceLabel = "Fixture source", IsFavorite = true
        };
        var a = Song("same"); var b = Song("same", Guid.NewGuid()); var c = Song("third");
        var queue = new PlaybackQueue(new Random(9));
        Require(queue.Replace([a, b, c], a.Reference), "fixture queue");
        Require(queue.InsertNext(c), "fixture explicit next");
        Require(queue.Select(b.Reference) == b, "fixture history");
        var state = new PlaybackSessionState(queue.CaptureSnapshot(), 32.125, "跨库歌单");
        int groups = 0;
        try
        {
            Require(await store.LoadSessionAsync() is null, "missing session");
            Require(!File.Exists(file + ".lock"), "missing load creates no personal lock file");
            string absentProfile = Path.Combine(root, "absent-profile");
            Require(await new PlaybackResumeStore(absentProfile).LoadSessionAsync() is null && !Directory.Exists(absentProfile),
                "missing load creates no profile directory");
            await store.SaveAsync(new(a, 12.75));
            var migrated = await new PlaybackResumeStore(root).LoadSessionAsync();
            Require(migrated is not null && migrated.Queue.Tracks.Single().Reference == a.Reference &&
                migrated.Queue.Current == a.Reference && migrated.Queue.Mode == QueueMode.Sequential &&
                migrated.Queue.History.Count == 0 && migrated.Queue.RequestedNext.Count == 0 &&
                migrated.PositionSeconds == 12.75 && migrated.Origin == "上次播放歌曲", "v1 migration without provider");
            using (var legacyJson = JsonDocument.Parse(await File.ReadAllBytesAsync(file)))
                Require(legacyJson.RootElement.GetProperty("Version").GetInt32() == 1, "migration does not rewrite input");
            groups++;

            foreach (var mode in Enum.GetValues<QueueMode>())
            {
                var expected = state with { Queue = state.Queue with { Mode = mode } };
                await store.SaveSessionAsync(expected);
                var actual = await new PlaybackResumeStore(root).LoadSessionAsync();
                Same(expected, actual, "v2 roundtrip " + mode);
                var legacy = await store.LoadAsync();
                Require(legacy?.Track.Reference == b.Reference && legacy.PositionSeconds == 32.125, "v2 old API current projection");
                var restored = new PlaybackQueue(new Random(9));
                Require(restored.Restore(actual!.Queue), "restore queue");
                Require(restored.Next(automatic: true)?.Reference == c.Reference, "explicit next survives each mode");
                Require(restored.Previous()?.Reference == b.Reference, "history survives explicit next");
                Require(restored.Previous()?.Reference == a.Reference, "pre-exit history survives");
            }
            var exact = state with { PositionSeconds = b.DurationSeconds };
            store.SaveSession(exact);
            Same(exact, await store.LoadSessionAsync(), "synchronous boundary write");
            using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(file)))
            {
                var names = json.RootElement.GetProperty("Queue").GetProperty("Tracks")[0].EnumerateObject().Select(p => p.Name).ToHashSet();
                Require(names.SetEquals(new[] { "SourceInstanceId", "Id", "Title", "Artist", "Album", "DurationSeconds", "IsCue",
                    "IsFavorite", "SourceLabel", "CoverId", "AlbumId", "LibraryIds", "Artists" }), "explicit song DTO only");
                Require(json.RootElement.GetProperty("Version").GetInt32() == 2 &&
                    json.RootElement.GetProperty("Track").GetProperty("SourceInstanceId").GetGuid() == b.SourceInstanceId,
                    "script-compatible root current");
            }
            groups++;

            var unselected = state with { Queue = new([a, b], null, QueueMode.Shuffle, [], []), PositionSeconds = 0 };
            store.SaveSession(unselected);
            Same(unselected, await store.LoadSessionAsync(), "unselected nonempty queue");
            Require(await store.LoadAsync() is null, "unselected has no single-song resume");
            var empty = unselected with { Queue = new([], null, QueueMode.RepeatAll, [], []) };
            await store.SaveSessionAsync(empty);
            Same(empty, await store.LoadSessionAsync(), "empty queue preserves mode");
            Require(await store.LoadAsync() is null, "empty has no single-song resume");
            groups++;

            await store.SaveSessionAsync(state);
            byte[] saved = await File.ReadAllBytesAsync(file);
            var malformed = new List<byte[]>
            {
                Array.Empty<byte>(), Encoding.UTF8.GetBytes("[]"), Encoding.UTF8.GetBytes("{\"Version\":\"2\"}"),
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(saved).Replace("\"Origin\":", "\"Origin\":\"duplicate\",\"Origin\":")),
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(saved).Replace("\"TrackId\":", "\"TrackId\":\"duplicate\",\"TrackId\":"))
            };
            foreach (var mutation in new Action<JsonObject>[]
            {
                json => json["Unknown"] = true,
                json => json.Remove("Origin"),
                json => json["Queue"]!["Tracks"]![0]!["StreamUrl"] = "not persisted",
                json => json["Queue"]!["Tracks"]![0]!["Artists"]![0]!["Unknown"] = true,
                json => json["Track"]!["Title"] = "different metadata",
                json => json["Track"] = null,
                json => json["PositionSeconds"] = 181,
                json => json["Queue"]!["Tracks"]![0]!["SourceInstanceId"] = Guid.Empty.ToString(),
                json => json["Queue"]!["Tracks"]![0]!["IsCue"] = true,
                json => json["Queue"]!["Tracks"]![0]!["DurationSeconds"] = 864001,
                json => json["Queue"]!["Tracks"]![0]!["Title"] = new string('x', 4097),
                json => json["Queue"]!["Tracks"]!.AsArray().Add(json["Queue"]!["Tracks"]![0]!.DeepClone()),
                json => json["Queue"]!["Current"]!["TrackId"] = "absent",
                json => json["Queue"]!["History"]![0]!["TrackId"] = "absent",
                json => json["Queue"]!["RequestedNext"]![0]!["TrackId"] = "absent",
                json => json["Queue"]!["Mode"] = 999,
                json => json["Origin"] = new string('x', 513)
            })
            {
                var json = JsonNode.Parse(saved)!.AsObject(); mutation(json);
                malformed.Add(JsonSerializer.SerializeToUtf8Bytes(json));
            }
            foreach (var bytes in malformed)
            {
                await File.WriteAllBytesAsync(file, bytes);
                Require(await store.LoadSessionAsync() is null && await store.LoadAsync() is null, "malformed v2 rejected");
                Require(Enumerable.SequenceEqual(bytes, await File.ReadAllBytesAsync(file)), "malformed input retained");
            }
            await File.WriteAllBytesAsync(file, saved);
            groups++;

            foreach (var invalid in new[]
            {
                state with { PositionSeconds = double.NaN }, state with { PositionSeconds = -1 },
                state with { PositionSeconds = 181 }, state with { Origin = null! },
                state with { Queue = state.Queue with { Mode = (QueueMode)999 } },
                state with { Queue = state.Queue with { Current = null } },
                state with { Queue = state.Queue with { Tracks = [a, a] } },
                state with { Queue = state.Queue with { Tracks = [a with { IsCue = true }] } },
                state with { Queue = state.Queue with { Tracks = [a with { SourceInstanceId = Guid.Empty }] } },
                state with { Queue = state.Queue with { Tracks = [a with { AlbumId = "" }] } },
                state with { Queue = state.Queue with { History = Enumerable.Repeat(a.Reference, 10001).ToArray() } },
                state with { Queue = state.Queue with { RequestedNext = Enumerable.Repeat(a.Reference, 10001).ToArray() } }
            })
            {
                await Rejected(() => store.SaveSessionAsync(invalid));
                await Rejected(() => Task.Run(() => store.SaveSession(invalid)));
                Require(Enumerable.SequenceEqual(saved, await File.ReadAllBytesAsync(file)), "invalid write preserves prior file");
            }
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await Canceled(() => store.SaveSessionAsync(state, canceled.Token));
                await Canceled(() => Task.Run(() => store.SaveSession(state, canceled.Token)));
                await Canceled(() => store.LoadSessionAsync(canceled.Token));
                Require(Enumerable.SequenceEqual(saved, await File.ReadAllBytesAsync(file)), "pre-canceled preserves file");
            }
            // Reflection avoids shipping a public test-only API. Release intentionally has no seam.
            var boundary = typeof(PlaybackResumeStore).GetField("commitBoundary", BindingFlags.Instance | BindingFlags.NonPublic);
            if (boundary is not null)
            {
                foreach (bool committed in new[] { false, true })
                foreach (bool synchronous in new[] { false, true })
                {
                    await store.SaveSessionAsync(state);
                    using var canceled = new CancellationTokenSource();
                    boundary.SetValue(store, (Action<bool>)(afterCommit => { if (afterCommit == committed) canceled.Cancel(); }));
                    var changed = state with { PositionSeconds = 77.25 };
                    Func<Task> write = synchronous ? () => Task.Run(() => store.SaveSession(changed, canceled.Token))
                        : () => store.SaveSessionAsync(changed, canceled.Token);
                    try
                    {
                        if (committed) await write(); else await Canceled(write);
                    }
                    finally { boundary.SetValue(store, null); }
                    Same(committed ? changed : state, await store.LoadSessionAsync(), "commit boundary cancellation");
                }
            }
            groups++;

            string longText = new('歌', 6000);
            var longTags = a with { Title = longText, Artist = longText, Album = longText, SourceLabel = longText,
                Artists = [new("artist", longText)] };
            var bounded = new PlaybackSessionState(new([longTags], longTags.Reference, QueueMode.Sequential, [], []), 0, new string('源', 600));
            await store.SaveSessionAsync(bounded);
            var clipped = await store.LoadSessionAsync();
            Require(clipped?.Origin.Length == 512 && clipped.Queue.Tracks.Single().Title.Length == 4096 &&
                clipped.Queue.Tracks.Single().Artists.Single().Name.Length == 4096 &&
                clipped.Queue.Tracks.Single().Reference == a.Reference, "clip display text preserve exact identity");
            var many = Enumerable.Range(0, 10000).Select(index => Song("song-" + index)).ToArray();
            var maximum = state with { Queue = new(many, many[^1].Reference, QueueMode.Sequential, [], []), PositionSeconds = 0 };
            await store.SaveSessionAsync(maximum);
            Require((await store.LoadSessionAsync())?.Queue.Tracks.Count == 10000, "maximum track capacity");
            saved = await File.ReadAllBytesAsync(file);
            await Rejected(() => store.SaveSessionAsync(maximum with { Queue = maximum.Queue with { Tracks = many.Append(Song("extra")).ToArray() } }));
            var libraries = Enumerable.Range(0, 100).Select(index => index + new string('x', 4000)).ToArray();
            var huge = Enumerable.Range(0, 50).Select(index => Song("large-" + index) with { LibraryIds = libraries }).ToArray();
            await Rejected(() => store.SaveSessionAsync(maximum with { Queue = new(huge, huge[0].Reference, QueueMode.Sequential, [], []) }));
            Require(Enumerable.SequenceEqual(saved, await File.ReadAllBytesAsync(file)), "capacity rejection preserves record");
            // Shared long input tags are cheap to construct, but eagerly clipping all
            // 10000 songs would allocate gigabytes. Count input visits to prove rejection
            // occurs during incremental encoding, without fragile process-memory timing.
            var extremeArtists = Enumerable.Range(0, 100).Select(index => new TrackArtist("artist-" + index, longText)).ToArray();
            var extreme = new CountingTracks(Enumerable.Range(0, 10000).Select(index =>
                Song("extreme-" + index) with { Artists = extremeArtists }).ToArray());
            var excessiveTags = maximum with { Queue = new(extreme, extreme[0].Reference, QueueMode.Sequential, [], []) };
            extreme.ResetVisits();
            await Rejected(() => store.SaveSessionAsync(excessiveTags));
            Require(extreme.Visits is > 0 and < 100, "oversize metadata rejected before normalizing full input");
            extreme.ResetVisits();
            await Rejected(() => Task.Run(() => store.SaveSession(excessiveTags)));
            Require(extreme.Visits is > 0 and < 100, "synchronous oversize metadata uses incremental budget");
            Require(Enumerable.SequenceEqual(saved, await File.ReadAllBytesAsync(file)) && !Directory.EnumerateFiles(root, "*.tmp").Any(),
                "early metadata rejection preserves prior file and creates no pending output");
            await File.WriteAllBytesAsync(file, new byte[16 * 1024 * 1024 + 1]);
            Require(await store.LoadSessionAsync() is null, "actual document byte bound");
            await store.SaveSessionAsync(state);
            groups++;

            var writers = Enumerable.Range(0, 12).Select(index => new PlaybackResumeStore(root)
                .SaveSessionAsync(state with { PositionSeconds = index, Origin = "writer-" + index })).ToArray();
            for (int read = 0; read < 24; read++)
            {
                var observed = await new PlaybackResumeStore(root).LoadSessionAsync();
                Require(observed is not null && observed.Queue.Tracks.Count == 3, "concurrent reader sees complete snapshot");
            }
            await Task.WhenAll(writers);
            var final = await store.LoadSessionAsync();
            Require(final is not null && final.Origin.StartsWith("writer-", StringComparison.Ordinal) && final.PositionSeconds is >= 0 and < 12,
                "concurrent final writer is complete");
            Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "no orphaned pending files");

            // All public mutations participate in the same OS lock, even through a
            // different store instance. Cancellation while waiting preserves the record.
            await store.SaveSessionAsync(state);
            saved = await File.ReadAllBytesAsync(file);
            using (var writerLock = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                foreach (var action in new Func<CancellationToken, Task>[]
                {
                    token => new PlaybackResumeStore(root).SaveSessionAsync(state, token),
                    token => Task.Run(() => new PlaybackResumeStore(root).SaveSession(state, token)),
                    token => new PlaybackResumeStore(root).SaveAsync(new(a, 0), token),
                    token => Task.Run(() => new PlaybackResumeStore(root).Save(new(a, 0), token)),
                    token => new PlaybackResumeStore(root).ClearAsync(token),
                    token => Task.Run(() => new PlaybackResumeStore(root).Clear(token))
                })
                {
                    using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
                    await Canceled(() => action(canceled.Token));
                }
                await Busy(() => new PlaybackResumeStore(root).SaveSessionAsync(state));
                await Busy(() => Task.Run(() => new PlaybackResumeStore(root).Clear()));
                Require(Enumerable.SequenceEqual(saved, await File.ReadAllBytesAsync(file)) && !Directory.EnumerateFiles(root, "*.tmp").Any(),
                    "waiting cancellation and timeout preserve previous record");
            }

            // Hold a real save at the last pre-commit boundary. Clear must remain
            // pending until that writer commits, then delete its result permanently.
            if (boundary is not null)
            {
                foreach (bool synchronous in new[] { false, true })
                {
                    using var release = new ManualResetEventSlim(false);
                    var writing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    boundary.SetValue(store, (Action<bool>)(afterCommit =>
                    {
                        if (afterCommit) return;
                        writing.TrySetResult(true);
                        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Writer barrier timed out.");
                    }));
                    Task save = Task.Run(async () =>
                    {
                        if (synchronous) store.SaveSession(state);
                        else await store.SaveSessionAsync(state);
                    });
                    Task? clear = null;
                    try
                    {
                        await writing.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        clear = new PlaybackResumeStore(root).ClearAsync();
                        Require(!clear.IsCompleted, "clear waits for in-flight save lock");
                    }
                    finally
                    {
                        release.Set();
                        await save;
                        boundary.SetValue(store, null);
                        if (clear is not null) await clear;
                    }
                    Require(await store.LoadSessionAsync() is null && !File.Exists(file), "clear cannot be resurrected by prior writer");
                }
            }
            groups++;

            await store.ClearAsync();
            Require(await store.LoadSessionAsync() is null && !File.Exists(file), "clear session file");
            store.SaveSession(state); store.Clear(); store.Clear();
            Require(await store.LoadAsync() is null && await store.LoadSessionAsync() is null, "legacy clear compatible and idempotent");
            groups++;
            Console.WriteLine($"Playback session persistence: {groups} groups passed.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Same(PlaybackSessionState expected, PlaybackSessionState? actual, string context)
    {
        Require(actual is not null && actual.PositionSeconds == expected.PositionSeconds && actual.Origin == expected.Origin &&
            actual.Queue.Mode == expected.Queue.Mode && actual.Queue.Current == expected.Queue.Current &&
            actual.Queue.Tracks.Select(track => track.Reference).SequenceEqual(expected.Queue.Tracks.Select(track => track.Reference)) &&
            actual.Queue.History.SequenceEqual(expected.Queue.History) && actual.Queue.RequestedNext.SequenceEqual(expected.Queue.RequestedNext), context);
        for (int index = 0; index < expected.Queue.Tracks.Count; index++)
        {
            var left = expected.Queue.Tracks[index]; var right = actual!.Queue.Tracks[index];
            Require(left.Title == right.Title && left.Artist == right.Artist && left.Album == right.Album &&
                left.DurationSeconds == right.DurationSeconds && left.CoverId == right.CoverId && left.AlbumId == right.AlbumId &&
                left.SourceLabel == right.SourceLabel && left.IsFavorite == right.IsFavorite &&
                left.LibraryIds.SequenceEqual(right.LibraryIds) && left.Artists.SequenceEqual(right.Artists), context + " metadata");
        }
    }
    private sealed class CountingTracks(MusicTrack[] tracks) : IReadOnlyList<MusicTrack>
    {
        public int Visits { get; private set; }
        public int Count => tracks.Length;
        public MusicTrack this[int index] { get { Visits++; return tracks[index]; } }
        public void ResetVisits() => Visits = 0;
        public IEnumerator<MusicTrack> GetEnumerator()
        {
            for (int index = 0; index < tracks.Length; index++) yield return this[index];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static async Task Rejected(Func<Task> action)
    {
        try { await action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid session accepted.");
    }
    private static async Task Busy(Func<Task> action)
    {
        try { await action(); } catch (IOException) { return; }
        throw new InvalidOperationException("Locked session mutation did not time out.");
    }
    private static async Task Canceled(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Canceled session operation completed.");
    }
    private static void Require(bool value, string context)
    { if (!value) throw new InvalidOperationException("Playback session verification failed: " + context); }
}
