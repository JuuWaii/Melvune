using System.Net;
using System.Net.Http.Headers;
using Melvune.Core;

namespace Melvune.Infrastructure;

/// <summary>受控 Range 流；强版本最多缓存八个 256 KiB 块，其他来源只缓存一块，不写入磁盘。</summary>
public sealed class HttpRangeStream : Stream
{
    private const int BlockSize = 256 * 1024;
    private readonly HttpClient client;
    private readonly Uri source;
    private readonly long length;
    private readonly EntityTagHeaderValue? versionTag;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private sealed record CachedBlock(long Offset, byte[] Bytes);
    private readonly LinkedList<CachedBlock> blocks = new();
    private long position;
    private volatile bool disposed;
    public string ContentType { get; }

    private HttpRangeStream(HttpClient client, Uri source, long length, string contentType, EntityTagHeaderValue? versionTag)
    { this.client = client; this.source = source; this.length = length; ContentType = contentType; this.versionTag = versionTag; }

    // 接管专用 HttpClient，调用者不得复用它发送其他主机请求。
    public static async Task<HttpRangeStream> OpenAsync(HttpClient client, Uri source, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            CheckResponse(response, 0, 0);
            long size = response.Content.Headers.ContentRange!.Length!.Value;
            string type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (!type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && type != "application/octet-stream")
                throw new MusicApiException(MusicFailure.InvalidResponse);
            var tag = response.Headers.ETag;
            // Weak validators do not identify identical bytes. Bind only a strong
            // version; a Last-Modified date alone does not prove byte identity.
            return new HttpRangeStream(client, source, size, type, tag is { IsWeak: false } && tag.Tag != "*" ? tag : null);
        }
        catch { client.Dispose(); throw; }
    }

    private static void CheckResponse(HttpResponseMessage response, long from, long to, long? expectedLength = null, EntityTagHeaderValue? expectedVersion = null)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new MusicApiException(MusicFailure.Unauthorized);
        if ((int)response.StatusCode is >= 300 and < 400) throw new MusicApiException(MusicFailure.RedirectRejected);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != from || range.To != to ||
            range.Length is null || range.Length <= to || (expectedLength is not null && range.Length != expectedLength))
            throw new MusicApiException(MusicFailure.InvalidResponse);
        if (expectedVersion is not null && (response.Headers.ETag is not { IsWeak: false } actualVersion || actualVersion.Tag != expectedVersion.Tag))
            throw new MusicApiException(MusicFailure.InvalidResponse);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (buffer.IsEmpty || position >= length) return 0;
            long start = position / BlockSize * BlockSize;
            var cached = blocks.First;
            while (cached is not null && cached.Value.Offset != start) cached = cached.Next;
            if (cached is null)
            {
                long end = Math.Min(length - 1, start + BlockSize - 1);
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                request.Headers.Range = new RangeHeaderValue(start, end);
                // Continue the opened representation, never concatenate another
                // same-sized revision. Recheck the response even if a proxy ignores
                // the conditional request. No validator value is logged or stored.
                if (versionTag is not null) request.Headers.IfMatch.Add(versionTag);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                CheckResponse(response, start, end, length, versionTag);
                byte[] next = new byte[checked((int)(end - start + 1))];
                using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await input.ReadExactlyAsync(next, timeout.Token).ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(disposed, this);
                timeout.Token.ThrowIfCancellationRequested();
                // Only validated, complete, uncanceled data may evict a prior block.
                // Strong ETags pin all retained blocks to the opened representation.
                cached = blocks.AddFirst(new CachedBlock(start, next));
                if (blocks.Count > (versionTag is null ? 1 : 8)) blocks.RemoveLast();
            }
            ObjectDisposedException.ThrowIf(disposed, this);
            timeout.Token.ThrowIfCancellationRequested();
            if (cached != blocks.First) { blocks.Remove(cached); blocks.AddFirst(cached); }
            byte[] block = cached.Value.Bytes;
            int offset = checked((int)(position - cached.Value.Offset));
            int count = Math.Min(buffer.Length, block.Length - offset);
            block.AsMemory(offset, count).CopyTo(buffer);
            position += count;
            return count;
        }
        finally { if (disposed) blocks.Clear(); gate.Release(); }
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try { gate.Wait(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        { throw new ObjectDisposedException(nameof(HttpRangeStream)); }
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            long next = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
            if (next < 0) throw new IOException("Invalid seek");
            return position = next;
        }
        finally { if (disposed) blocks.Clear(); gate.Release(); }
    }
    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true; lifetime.Cancel(); client.Dispose();
            // Never wait behind native IO during stop. The holder clears on release.
            if (gate.Wait(0)) { try { blocks.Clear(); } finally { gate.Release(); } }
        }
        base.Dispose(disposing);
    }
}
