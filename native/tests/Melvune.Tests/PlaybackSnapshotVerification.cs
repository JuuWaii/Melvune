using Melvune.Core;

internal static class PlaybackSnapshotVerification
{
    internal static Task RunAsync()
    {
        static void Check(bool value) { if (!value) throw new InvalidOperationException("Snapshot regression failed."); }
        var hub = new PlaybackSnapshotHub(); var other = new PlaybackSnapshotHub();
        Check(hub.InstanceId != other.InstanceId && hub.Current.TrackGeneration is null);
        var generation = Guid.NewGuid(); int notices = 0; hub.Changed += _ => notices++;
        hub.Publish(hub.Current with { InstanceId = Guid.Empty, Sequence = 999, TrackGeneration = generation,
            State = PlaybackStatus.Playing, Title = new string('中', 3000), PositionSeconds = double.NaN, DurationSeconds = -5 });
        Check(hub.Current.InstanceId == hub.InstanceId && hub.Current.Sequence == 1 && hub.Current.Title.Length == 2048
            && hub.Current.PositionSeconds == 0 && hub.Current.DurationSeconds == 0 && notices == 1);
        hub.SetLyrics(generation, PlaybackLyricsKind.Timed, new(Enumerable.Range(0, 2001).Select(i => new LyricLine(i, new string('a', 2049))).ToArray(), true));
        Check(hub.Lyrics.Lines.Count == 2000 && hub.Lyrics.IsLimited && hub.Lyrics.Lines[0].Text.Length == 2048);
        // A short input with many time stamps must not expand into hundreds of MB of grouped text.
        string expansion = string.Concat(Enumerable.Repeat("[00:01]", 500)) + new string('x', 5000);
        try { Lyrics.Parse(expansion); throw new InvalidOperationException("Expansion was accepted."); }
        catch (ArgumentException) { }
        var original = Lyrics.Parse("[00:01]一\n[00:01]译\n[00:02]二");
        Check(original.Lines.Count == 2 && original.Lines[0].Text == "一\n译" && original.FindLine(2) == 1);
        return Task.CompletedTask;
    }
}
