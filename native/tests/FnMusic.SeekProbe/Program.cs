using FFmpegInteropX;
using System.Diagnostics;
using Windows.Media.Playback;

// Synthetic input only: no user files, credentials or network access.
const int sampleRate = 48000, seconds = 600;
var path = Path.Combine(Path.GetTempPath(), $"fnmusic-seek-{Guid.NewGuid():N}.wav");
try
{
    using (var file = File.Create(path))
    using (var writer = new BinaryWriter(file))
    {
        var size = sampleRate * seconds * 2;
        writer.Write("RIFF"u8); writer.Write(size + 36); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(size);
        for (var i = 0; i < sampleRate * seconds; i++)
            writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / sampleRate) * 1000));
    }
    using var input = new MeasuredStream(path);
    using var random = input.AsRandomAccessStream();
    using var source = await FFmpegMediaSource.CreateFromStreamAsync(random);
    using var player = new MediaPlayer { IsMuted = true, AutoPlay = false };
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    player.MediaOpened += (_, _) => opened.TrySetResult();
    player.MediaFailed += (_, _) => failed.TrySetException(new InvalidOperationException("Media pipeline failed."));
    player.Source = source.CreateMediaPlaybackItem();
    await AwaitOrFailure(opened.Task);
    if (!player.PlaybackSession.CanSeek) throw new Exception("Source is not seekable.");
    foreach (var target in new[] { 570, 30, 450 })
    {
        player.Pause();
        var before = input.BytesRead;
        var watch = Stopwatch.StartNew();
        player.PlaybackSession.Position = TimeSpan.FromSeconds(target);
        player.Play();
        await AwaitOrFailure(WaitForProgress(target));
        player.Pause();
        var read = input.BytesRead - before;
        if (read <= 0 || read > sampleRate * 2 * 60) throw new Exception("Seek read count outside expected bounds.");
        Console.WriteLine($"PASS seek {target}s: {watch.ElapsedMilliseconds}ms, read {read} bytes");
    }
    player.Source = null;
    Console.WriteLine($"PASS native activation and random stream playback; total read {input.BytesRead} / {input.Length} bytes");

    async Task AwaitOrFailure(Task operation)
    {
        try
        {
            var completed = await Task.WhenAny(operation, failed.Task).WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
            await completed;
        }
        catch { lifetime.Cancel(); throw; }
    }
    async Task WaitForProgress(int target)
    {
        while (player.PlaybackSession.Position.TotalSeconds < target + 0.25 ||
               player.PlaybackSession.Position.TotalSeconds > target + 5 ||
               player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
            await Task.Delay(25, lifetime.Token);
    }
}
finally { File.Delete(path); }

sealed class MeasuredStream(string path) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
{
    private long bytesRead;
    public long BytesRead => Interlocked.Read(ref bytesRead);
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = base.Read(buffer, offset, count);
        Interlocked.Add(ref bytesRead, read); return read;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        var read = await base.ReadAsync(buffer, offset, count, token);
        Interlocked.Add(ref bytesRead, read); return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        var read = await base.ReadAsync(buffer, token);
        Interlocked.Add(ref bytesRead, read); return read;
    }
}
