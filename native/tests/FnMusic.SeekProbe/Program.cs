using FFmpegInteropX;
using System.Diagnostics;
using Windows.Media.Playback;
using FnMusic.Infrastructure;
using System.Net;
using System.Net.Http.Headers;

// Synthetic input only: no user files, credentials or network access.
const int sampleRate = 48000, seconds = 600;
var path = Path.Combine(Path.GetTempPath(), $"fnmusic-seek-{Guid.NewGuid():N}.wav");
var wavePath = path;
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
    var format = args.FirstOrDefault(value => value.StartsWith("--format=", StringComparison.Ordinal))?[9..] ?? "wav";
    if (format is not ("wav" or "flac" or "mp3" or "ogg")) throw new ArgumentException("Unsupported synthetic format.");
    if (format != "wav")
    {
        path = Path.ChangeExtension(wavePath, format);
        var start = new ProcessStartInfo(FfmpegPcmDecoder.FindExecutable())
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", wavePath, path })
            start.ArgumentList.Add(argument);
        using var encoder = Process.Start(start) ?? throw new Exception("Synthetic encoder did not start.");
        var errors = encoder.StandardError.ReadToEndAsync();
        try { await encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!encoder.HasExited) encoder.Kill(true); await encoder.WaitForExitAsync(); throw; }
        await errors;
        if (encoder.ExitCode != 0) throw new Exception("Synthetic encoding failed.");
    }
    var rangeMode = args.Contains("--range", StringComparer.Ordinal);
    using var handler = new RangeHandler(path);
    using Stream input = rangeMode
        ? await HttpRangeStream.OpenAsync(new HttpClient(handler, false), new Uri("https://seek.invalid/audio"), CancellationToken.None)
        : new MeasuredStream(path);
    long ReadCount() => rangeMode ? handler.BytesRead : ((MeasuredStream)input).BytesRead;
    using var random = input.AsRandomAccessStream();
    var config = new MediaSourceConfig();
    if (args.Contains("--fast-seek", StringComparer.Ordinal)) config.FFmpegOptions.Add("fflags", "+fastseek");
    using var source = await FFmpegMediaSource.CreateFromStreamAsync(random, config);
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
        var before = ReadCount();
        var watch = Stopwatch.StartNew();
        player.PlaybackSession.Position = TimeSpan.FromSeconds(target);
        player.Play();
        await AwaitOrFailure(WaitForProgress(target));
        player.Pause();
        var read = ReadCount() - before;
        if (read < 0 || read > sampleRate * 2 * 60 || (format == "wav" && read == 0))
            throw new Exception("Seek read count outside expected bounds.");
        Console.WriteLine($"PASS {format} seek {target}s: {watch.ElapsedMilliseconds}ms, read {read} bytes");
        if (read > input.Length / 2)
            Console.WriteLine($"NOTICE {format}: this seek read over half the encoded file; network efficiency is not established.");
    }
    player.Source = null;
    Console.WriteLine($"PASS {(rangeMode ? "controlled HTTP Range" : "local")} playback; total read {ReadCount()} / {input.Length} bytes");
    if (rangeMode)
    {
        await VerifyBlockedOpenCancellation(path, false);
        await VerifyBlockedOpenCancellation(path, true);
    }

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
finally { File.Delete(path); if (path != wavePath) File.Delete(wavePath); }

static async Task VerifyBlockedOpenCancellation(string path, bool body)
{
    using var handler = new RangeHandler(path);
    using var input = await HttpRangeStream.OpenAsync(new HttpClient(handler, false), new Uri("https://seek.invalid/audio"), CancellationToken.None);
    handler.Block = !body;
    handler.BlockBody = body;
    using var random = input.AsRandomAccessStream();
    var opening = FFmpegMediaSource.CreateFromStreamAsync(random).AsTask();
    await handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var watch = Stopwatch.StartNew();
    input.Dispose();
    try
    {
        using var unexpected = await opening.WaitAsync(TimeSpan.FromSeconds(3));
        throw new InvalidOperationException("Blocked input unexpectedly opened.");
    }
    catch (Exception error) when (error is not TimeoutException and not InvalidOperationException)
    {
        if (!handler.Cancelled) throw new Exception("HTTP request was not cancelled.");
        Console.WriteLine($"PASS blocked {(body ? "body" : "headers")} native open cancels and exits in {watch.ElapsedMilliseconds}ms");
    }
}

sealed class RangeHandler(string path) : HttpMessageHandler
{
    private long bytesRead;
    public long BytesRead => Interlocked.Read(ref bytesRead);
    public bool Block { get; set; }
    public bool BlockBody { get; set; }
    public bool Cancelled { get; private set; }
    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri?.Host != "seek.invalid") throw new Exception("Unexpected origin.");
        if (Block)
        {
            await WaitBlocked(token);
        }
        var range = request.Headers.Range?.Ranges.Single() ?? throw new Exception("Range required.");
        var from = range.From ?? throw new Exception("Explicit start required.");
        var to = range.To ?? throw new Exception("Explicit end required.");
        using var file = File.OpenRead(path);
        file.Position = from;
        var bytes = new byte[checked((int)(to - from + 1))];
        await file.ReadExactlyAsync(bytes, token);
        Interlocked.Add(ref bytesRead, bytes.Length);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        { Content = BlockBody ? new StreamContent(new BlockedBody(WaitBlocked)) : new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Length);
        return response;
    }
    private async Task WaitBlocked(CancellationToken token)
    {
        Blocked.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, token); }
        catch (OperationCanceledException) { Cancelled = true; throw; }
    }
}

sealed class BlockedBody(Func<CancellationToken, Task> wait) : MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await wait(cancellationToken); return 0;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await wait(cancellationToken); return 0;
    }
}

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
