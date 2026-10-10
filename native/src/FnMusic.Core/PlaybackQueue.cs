namespace FnMusic.Core;

public enum QueueMode { Sequential, RepeatAll, RepeatOne, Shuffle }

/// <summary>播放时固定曲目快照；翻页不改变正在播放的队列。</summary>
public sealed class PlaybackQueue(Random? random = null)
{
    private readonly Random random = random ?? Random.Shared;
    private MusicTrack[] tracks = [];
    private readonly Stack<int> history = new();
    private readonly List<TrackReference> requestedNext = [];
    public QueueMode Mode { get; set; }
    public int Index { get; private set; } = -1;
    public int Count => tracks.Length;
    public IReadOnlyList<MusicTrack> Tracks => Array.AsReadOnly(tracks);
    public MusicTrack? Current => Index >= 0 && Index < tracks.Length ? tracks[Index] : null;
    public bool CanPrevious => Current is not null && (history.Count > 0 || Index > 0 || Mode is QueueMode.RepeatAll or QueueMode.RepeatOne);
    public bool CanNext => Current is not null && (requestedNext.Count > 0 || Index + 1 < Count || Mode != QueueMode.Sequential);

    public bool Replace(IEnumerable<MusicTrack> source, TrackReference selectedReference)
    {
        // 未支持的 CUE 不进入自动续播，避免在曲间意外停住。
        var next = source.Where(t => !t.IsCue).DistinctBy(t => t.Reference).ToArray();
        int selected = Array.FindIndex(next, t => t.Reference == selectedReference);
        if (selected < 0) return false;
        tracks = next; Index = selected; history.Clear(); requestedNext.Clear();
        return true;
    }
    /// <summary>Move one distinct track immediately after the current track without starting playback.
    /// Explicit requests take precedence over shuffle/repeat-one; the most recent request plays first.</summary>
    public bool InsertNext(MusicTrack track)
    {
        if (track.IsCue || Current?.Reference == track.Reference) return false;
        var current = Current?.Reference;
        var next = tracks.Where(item => item.Reference != track.Reference).ToList();
        int currentIndex = current is null ? -1 : next.FindIndex(item => item.Reference == current);
        next.Insert(currentIndex + 1, track);
        tracks = next.ToArray(); Index = currentIndex < 0 ? 0 : currentIndex;
        history.Clear();
        requestedNext.Remove(track.Reference);
        if (currentIndex >= 0) requestedNext.Insert(0, track.Reference);
        return true;
    }
    public MusicTrack? Next(bool automatic)
    {
        if (Current is null) return null;
        while (requestedNext.Count > 0)
        {
            var reference = requestedNext[0]; requestedNext.RemoveAt(0);
            int requested = Array.FindIndex(tracks, t => t.Reference == reference);
            if (requested < 0 || requested == Index) continue;
            history.Push(Index); Index = requested;
            return Current;
        }
        if (automatic && Mode == QueueMode.RepeatOne) return Current;
        int next;
        if (Mode == QueueMode.Shuffle)
        {
            if (Count == 1) return automatic ? null : Current;
            next = random.Next(Count - 1);
            if (next >= Index) next++;
        }
        else
        {
            next = Index + 1;
            if (next >= Count)
            {
                if (Mode == QueueMode.Sequential) return null;
                next = 0;
            }
        }
        history.Push(Index); Index = next;
        return Current;
    }
    public MusicTrack? Previous()
    {
        if (Current is null) return null;
        if (history.TryPop(out int previous)) Index = previous;
        else if (Index > 0) Index--;
        else if (Mode is QueueMode.RepeatAll or QueueMode.RepeatOne) Index = Count - 1;
        else return null;
        return Current;
    }
    public MusicTrack? Select(TrackReference reference)
    {
        int selected = Array.FindIndex(tracks, t => t.Reference == reference);
        if (selected < 0) return null;
        if (Index >= 0 && Index != selected) history.Push(Index);
        Index = selected;
        requestedNext.Remove(reference);
        return Current;
    }
    // 当前曲目由窗口负责停止；删除其他曲目保持当前曲目身份。
    public bool Remove(TrackReference reference)
    {
        int removed = Array.FindIndex(tracks, t => t.Reference == reference);
        if (removed < 0) return false;
        tracks = tracks.Where(t => t.Reference != reference).ToArray();
        if (removed == Index) Index = -1;
        else if (removed < Index) Index--;
        history.Clear(); // 索引已变化，旧随机播放历史不再有效。
        requestedNext.Remove(reference);
        return true;
    }
    public void Clear() { tracks = []; Index = -1; history.Clear(); requestedNext.Clear(); }
}
