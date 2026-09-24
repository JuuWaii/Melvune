using FFmpegInteropX;
using Windows.Storage.Streams;

namespace FnMusic.App.Services;

/// <summary>Owns one decoder and its input; cancel managed reads before closing native IO.</summary>
internal sealed class SeekablePlayback
{
    private readonly Stream input;
    private IRandomAccessStream? random;
    private int stopped;
    private readonly object cleanupGate = new();
    private Task? cleanup;
    public Task<FFmpegMediaSource> Opening { get; }

    public SeekablePlayback(Stream input)
    {
        this.input = input;
        Opening = Task.Run(async () =>
        {
            random = input.AsRandomAccessStream();
            var config = new MediaSourceConfig();
            // Only the supplied managed stream may access media. Nested file/network
            // references must not bypass the application's origin and credential rules.
            config.FFmpegOptions.Add("protocol_whitelist", "none");
            return await FFmpegMediaSource.CreateFromStreamAsync(random, config);
        });
    }

    public Task Stop()
    {
        lock (cleanupGate)
        {
            if (cleanup is not null) return cleanup;
            Interlocked.Exchange(ref stopped, 1);
            // HttpRangeStream.Dispose cancels both pending headers and body reads.
            input.Dispose();
            return cleanup = Task.Run(async () =>
            {
                try { using var source = await Opening.ConfigureAwait(false); }
                catch (Exception) { /* Cancellation/invalid media: observed, no private details logged. */ }
                finally { random?.Dispose(); }
            });
        }
    }

    public bool IsStopped => Volatile.Read(ref stopped) != 0;
}
