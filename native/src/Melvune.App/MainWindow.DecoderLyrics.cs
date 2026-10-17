using Melvune.Core;

namespace Melvune.App;
public sealed partial class MainWindow
{
    private TaskCompletionSource<Lyrics>? decoderLyrics;
    private TrackReference? decoderLyricsReference;
    private async Task<Lyrics> ReadDecoderLyricsAsync(MusicTrack track, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var task = decoderLyricsReference == track.Reference ? decoderLyrics?.Task : null;
        if (task is null || queue.Current?.Reference != track.Reference) return Lyrics.Empty;
        var value = await task.WaitAsync(ct);
        return decoderLyricsReference == track.Reference && queue.Current?.Reference == track.Reference && ReferenceEquals(task, decoderLyrics?.Task) ? value : Lyrics.Empty;
    }
    private void ClearDecoderLyrics()
    { decoderLyrics?.TrySetResult(Lyrics.Empty); decoderLyrics = null; decoderLyricsReference = null; }
}
