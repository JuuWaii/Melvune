namespace Melvune.Infrastructure;

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
    private bool disposed;
    private Task? disposal;
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
                    lock (sync) chunks.Add(chunk);
                    while (count < chunk.Length)
                    {
                        int read = await input.ReadAsync(chunk.AsMemory(count), lifetime.Token).ConfigureAwait(false);
                        if (read == 0) return;
                        count += read;
                        lock (sync)
                        {
                            length += read;
                            var previous = changed; changed = NewSignal(); previous.TrySetResult();
                        }
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
                if (disposed) throw new ObjectDisposedException(nameof(PcmReadAheadBuffer));
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
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal is not null) return new(disposal);
            disposed = true;
            changed.TrySetResult();
            disposal = ReleaseAsync();
            return new(disposal);
        }
    }
    private async Task ReleaseAsync()
    {
        lifetime.Cancel();
        await pump.ConfigureAwait(false);
        lock (sync) { chunks.Clear(); length = 0; }
        lifetime.Dispose();
    }
}

public sealed class PcmCacheLimitException : IOException;
