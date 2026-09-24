namespace FnMusic.Infrastructure;

/// <summary>Bounded, in-memory PCM read-ahead. Input remains owned by the decoder.</summary>
public sealed class PcmReadAheadBuffer : IAsyncDisposable
{
    public const int DefaultLimit = 96 * 1024 * 1024;
    private const int ChunkSize = 65536;
    private readonly object sync = new();
    private readonly List<byte[]> chunks = [];
    private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource changed = NewSignal();
    private readonly Task pump;
    private long length;
    private bool ended, limited;
    private Exception? error;
    public long BufferedBytes { get { lock (sync) return length; } }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public PcmReadAheadBuffer(Stream input, int limit = DefaultLimit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        pump = Task.Run(async () =>
        {
            try
            {
                while (length < limit)
                {
                    byte[] chunk = new byte[Math.Min(ChunkSize, limit - (int)length)];
                    int count = 0;
                    while (count < chunk.Length)
                    {
                        int read = await input.ReadAsync(chunk.AsMemory(count), lifetime.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        count += read;
                    }
                    lock (sync)
                    {
                        if (count > 0) { chunks.Add(chunk); length += count; }
                        if (count < chunk.Length) ended = true;
                        var previous = changed; changed = NewSignal(); previous.TrySetResult();
                        if (ended) return;
                    }
                }
                lock (sync) limited = true;
            }
            catch (Exception ex) { lock (sync) error = ex; }
            finally { lock (sync) { ended = true; changed.TrySetResult(); } }
        });
    }
    public async ValueTask<int> ReadAtAsync(long position, Memory<byte> output, CancellationToken ct)
    {
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
        if (output.IsEmpty) return 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task wait;
            lock (sync)
            {
                if (lifetime.IsCancellationRequested) throw new ObjectDisposedException(nameof(PcmReadAheadBuffer));
                if (position < length)
                {
                    int offset = (int)(position % ChunkSize);
                    int count = (int)Math.Min(output.Length, Math.Min(ChunkSize - offset, length - position));
                    chunks[(int)(position / ChunkSize)].AsMemory(offset, count).CopyTo(output);
                    return count;
                }
                if (limited) throw new PcmCacheLimitException();
                if (error is not null) throw new IOException("Audio buffering failed.");
                if (ended) return 0;
                wait = changed.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await pump.ConfigureAwait(false);
        lock (sync) chunks.Clear();
    }
}

public sealed class PcmCacheLimitException : IOException;
