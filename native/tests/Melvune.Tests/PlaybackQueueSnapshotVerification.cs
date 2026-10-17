using Melvune.Core;

internal static class PlaybackQueueSnapshotVerification
{
    private static readonly Guid FirstSource = Guid.Parse("cae8d68a-e4f6-4a01-a496-2ce27dc5829c");
    private static readonly Guid SecondSource = Guid.Parse("bae8d68a-e4f6-4a01-a496-2ce27dc5829c");
    private static MusicTrack Track(string id, Guid? source = null) => new(id, "Track " + id, "Artist", 120, false)
    { SourceInstanceId = source ?? FirstSource, LibraryIds = ["library"], Artists = [new("artist", "Artist")] };
    private static void Require(bool condition, string context)
    { if (!condition) throw new InvalidOperationException("Playback queue snapshot verification failed: " + context); }

    public static Task RunAsync()
    {
        Sequential();
        EmptyCurrent();
        RequestsAndHistory();
        QualifiedIdentity();
        InvalidIsAtomic();
        Boundaries();
        Isolation();
        Console.WriteLine("Playback queue snapshot: 7 groups passed.");
        return Task.CompletedTask;
    }

    private static void Sequential()
    {
        var a = Track("a"); var b = Track("b"); var c = Track("c");
        var original = new PlaybackQueue(); original.Replace([a, b, c], a.Reference);
        Require(original.Next(true)?.Reference == b.Reference, "advance before capture");
        var restored = new PlaybackQueue();
        Require(restored.Restore(original.CaptureSnapshot()), "sequential restore");
        Require(restored.Current?.Reference == b.Reference && restored.Previous()?.Reference == a.Reference &&
            restored.Next(false)?.Reference == b.Reference && restored.Next(true)?.Reference == c.Reference &&
            restored.Next(true) is null, "order, previous and end survive restore");
    }

    private static void EmptyCurrent()
    {
        var a = Track("a"); var b = Track("b"); var queue = new PlaybackQueue();
        queue.Replace([a, b], a.Reference); queue.Remove(a.Reference);
        var restored = new PlaybackQueue();
        Require(restored.Restore(queue.CaptureSnapshot()) && restored.Count == 1 && restored.Current is null &&
            restored.Index == -1 && !restored.CanNext && !restored.CanPrevious && restored.Next(true) is null,
            "remaining queue with no current track retained");
        Require(restored.Select(b.Reference)?.Reference == b.Reference, "selection starts retained queue");
        Require(restored.Restore(new([], null, QueueMode.RepeatAll, [], [])) && restored.Count == 0 &&
            restored.Current is null && restored.Mode == QueueMode.RepeatAll, "empty queue retained");
    }

    private static void RequestsAndHistory()
    {
        var a = Track("a"); var b = Track("b"); var c = Track("c"); var d = Track("d");
        foreach (var mode in new[] { QueueMode.Shuffle, QueueMode.RepeatOne })
        {
            var original = new PlaybackQueue(new Random(17)) { Mode = mode };
            original.Replace([a, b, c, d], a.Reference);
            original.InsertNext(d); original.InsertNext(c);
            var restored = new PlaybackQueue(new Random(999));
            Require(restored.Restore(original.CaptureSnapshot()) && restored.Mode == mode, "request mode restored");
            Require(restored.Next(true)?.Reference == c.Reference && restored.Next(true)?.Reference == d.Reference,
                "explicit requests outrank shuffle and repeat-one in recorded order");
            var recaptured = restored.CaptureSnapshot();
            Require(recaptured.History.SequenceEqual(new[] { c.Reference, a.Reference }), "history most recent first");
            var restarted = new PlaybackQueue(); Require(restarted.Restore(recaptured), "history restored");
            Require(restarted.Previous()?.Reference == c.Reference && restarted.Previous()?.Reference == a.Reference,
                "previous follows restored history");
            var duplicateHistory = new PlaybackQueueSnapshot([a, b, c], c.Reference, mode,
                [a.Reference, b.Reference, a.Reference], []);
            Require(restarted.Restore(duplicateHistory) && restarted.Previous()?.Reference == a.Reference &&
                restarted.Previous()?.Reference == b.Reference && restarted.Previous()?.Reference == a.Reference,
                "repeated history entries retained");
        }
    }

    private static void QualifiedIdentity()
    {
        var one = Track("shared"); var two = Track("shared", SecondSource); var three = Track("other");
        var queue = new PlaybackQueue(new Random(3));
        Require(queue.Restore(new([one, two, three], two.Reference, QueueMode.Shuffle,
            [one.Reference], [three.Reference])), "same id across sources allowed");
        Require(queue.Current?.SourceInstanceId == SecondSource && queue.Previous()?.SourceInstanceId == FirstSource,
            "source-qualified current and history");
        Require(queue.Next(true)?.Reference == three.Reference && queue.Count == 3,
            "source-qualified explicit request retained after previous");
        var legacy = Track("legacy", Guid.Empty);
        Require(queue.Restore(new([legacy], legacy.Reference, QueueMode.Sequential, [], [])),
            "core permits legacy and synthetic empty source identity");
    }

    private static void InvalidIsAtomic()
    {
        var a = Track("a"); var b = Track("b"); var missing = Track("missing").Reference;
        var baseline = new PlaybackQueueSnapshot([a, b], b.Reference, QueueMode.RepeatAll, [a.Reference], [a.Reference]);
        var queue = new PlaybackQueue(); Require(queue.Restore(baseline), "atomic baseline");
        var invalid = new PlaybackQueueSnapshot?[]
        {
            null, baseline with { Mode = (QueueMode)1234 }, baseline with { Tracks = null! },
            baseline with { History = null! }, baseline with { RequestedNext = null! },
            baseline with { Tracks = [a, a] }, baseline with { Tracks = [a, b with { IsCue = true }] },
            baseline with { Tracks = [a, null!] }, baseline with { Tracks = [a, b with { Id = "" }] },
            baseline with { Tracks = [a, b with { LibraryIds = null! }] },
            baseline with { Tracks = [a, b with { Artists = null! }] },
            baseline with { Tracks = [a, b with { LibraryIds = [null!] }] },
            baseline with { Tracks = [a, b with { Artists = [null!] }] },
            baseline with { Current = missing }, baseline with { History = [missing] },
            baseline with { History = [null!] }, baseline with { RequestedNext = [missing] },
            baseline with { RequestedNext = [null!] }, baseline with { RequestedNext = [b.Reference] },
            baseline with { RequestedNext = [a.Reference, a.Reference] }
        };
        foreach (var state in invalid)
        {
            Require(!queue.Restore(state!), "invalid state rejected");
            var actual = queue.CaptureSnapshot();
            Require(actual.Tracks.Select(t => t.Reference).SequenceEqual(baseline.Tracks.Select(t => t.Reference)) &&
                actual.Current == baseline.Current && actual.Mode == baseline.Mode &&
                actual.History.SequenceEqual(baseline.History) && actual.RequestedNext.SequenceEqual(baseline.RequestedNext),
                "rejection preserves all state");
        }
        Require(queue.Next(true)?.Reference == a.Reference && queue.Previous()?.Reference == b.Reference &&
            queue.Previous()?.Reference == a.Reference, "rejected states leave operational request and history intact");
    }

    private static void Boundaries()
    {
        var tracks = Enumerable.Range(0, 10000).Select(i => Track(i.ToString())).ToArray();
        var queue = new PlaybackQueue();
        Require(queue.Restore(new(tracks, tracks[0].Reference, QueueMode.Sequential,
            Enumerable.Repeat(tracks[1].Reference, 10000).ToArray(), tracks.Skip(1).Select(t => t.Reference).ToArray())),
            "maximum valid collections accepted");
        var baseline = queue.CaptureSnapshot();
        Require(!queue.Restore(baseline with { Tracks = tracks.Append(Track("overflow")).ToArray() }) &&
            !queue.Restore(baseline with { History = Enumerable.Repeat(tracks[1].Reference, 10001).ToArray() }) &&
            !queue.Restore(baseline with { RequestedNext = Enumerable.Repeat(tracks[1].Reference, 10001).ToArray() }),
            "oversized collections rejected");
        Require(queue.Count == 10000 && queue.Current?.Reference == tracks[0].Reference &&
            queue.CaptureSnapshot().History.Count == 10000, "limit rejections atomic");
        int enumerated = 0;
        IEnumerable<MusicTrack> OverflowingSource()
        {
            foreach (var track in tracks) { enumerated++; yield return track; }
            enumerated++; yield return Track("overflow");
            throw new InvalidOperationException("Replacement enumerated beyond the first excess unique track.");
        }
        Require(!queue.Replace(OverflowingSource(), tracks[0].Reference) && enumerated == PlaybackQueue.MaximumTracks + 1,
            "oversized replacement stops enumerating immediately and is rejected");
        Require(!queue.InsertNext(Track("overflow")), "new insertion into full queue rejected");
        var unchanged = queue.CaptureSnapshot();
        Require(unchanged.Tracks.Select(track => track.Reference).SequenceEqual(baseline.Tracks.Select(track => track.Reference)) &&
            unchanged.Current == baseline.Current && unchanged.Mode == baseline.Mode &&
            unchanged.History.SequenceEqual(baseline.History) && unchanged.RequestedNext.SequenceEqual(baseline.RequestedNext),
            "capacity failures preserve track order, current, mode, history and next requests");
        Require(queue.InsertNext(tracks[^1]) && queue.Count == PlaybackQueue.MaximumTracks &&
            queue.Current?.Reference == tracks[0].Reference && queue.Next(false)?.Reference == tracks[^1].Reference,
            "full queue allows an existing track to be rearranged and explicitly requested");
        var cue = Track("cue") with { IsCue = true };
        Require(queue.Replace(tracks.SelectMany(track => new[] { track, track, cue }), tracks[^1].Reference) &&
            queue.Count == PlaybackQueue.MaximumTracks && queue.Index == PlaybackQueue.MaximumTracks - 1,
            "replacement capacity counts distinct playable identities after cue exclusion");
        Require(queue.Restore(new(tracks, null, QueueMode.Shuffle, [], tracks.Select(t => t.Reference).ToArray())) &&
            queue.Current is null && queue.CaptureSnapshot().RequestedNext.Count == 10000,
            "maximum requested list allowed when no current track is selected");

        var a = Track("a"); var b = Track("b"); queue.Replace([a, b], a.Reference);
        for (int i = 0; i < 10017; i++) queue.Select(i % 2 == 0 ? b.Reference : a.Reference);
        var captured = queue.CaptureSnapshot();
        Require(captured.History.Count == 10000 && captured.History[0] == a.Reference &&
            captured.History[^1] == b.Reference, "capture bounds recent history without reversing order");
        var restart = new PlaybackQueue();
        Require(restart.Restore(captured) && restart.Previous()?.Reference == a.Reference,
            "bounded capture remains restorable");
    }

    private static void Isolation()
    {
        var libraries = new List<string> { "original" }; var artists = new List<TrackArtist> { new("first", "First") };
        var a = Track("a") with { LibraryIds = libraries, Artists = artists }; var b = Track("b");
        var original = new PlaybackQueue(); original.Replace([a, b], a.Reference);
        var captured = original.CaptureSnapshot();
        libraries[0] = "mutated"; artists[0] = new("changed", "Changed"); original.Select(b.Reference);
        Require(captured.Current == a.Reference && captured.Tracks[0].LibraryIds[0] == "original" &&
            captured.Tracks[0].Artists[0].Id == "first" && captured.History.Count == 0,
            "capture copies metadata lists and queue collections");
        Require(captured.Tracks is not MusicTrack[] && captured.Tracks[0].LibraryIds is not string[] &&
            captured.Tracks[0].Artists is not TrackArtist[] && captured.History is not TrackReference[] &&
            captured.RequestedNext is not TrackReference[], "capture exposes read-only collection wrappers");

        var inputTracks = new List<MusicTrack> { a, b }; var inputHistory = new List<TrackReference> { b.Reference };
        var inputNext = new List<TrackReference> { b.Reference }; var restored = new PlaybackQueue();
        Require(restored.Restore(new(inputTracks, a.Reference, QueueMode.RepeatOne, inputHistory, inputNext)),
            "mutable input restore accepted");
        inputTracks.Clear(); inputHistory.Clear(); inputNext.Clear(); libraries[0] = "again"; artists.Clear();
        Require(restored.Count == 2 && restored.Current?.LibraryIds[0] == "mutated" && restored.Current.Artists.Count == 1 &&
            restored.Next(true)?.Reference == b.Reference && restored.Previous()?.Reference == a.Reference &&
            restored.Previous()?.Reference == b.Reference, "restore isolates live state from all caller lists");
    }
}
