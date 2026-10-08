using System.Net;
using System.Net.Http.Headers;
using FnMusic.Core;
using FnMusic.Infrastructure;

internal static class RangeCacheVerification
{
    private const int Block = 256 * 1024;
    private static readonly byte[] Song = Enumerable.Range(0, Block * 10 + 33).Select(index => (byte)(index % 251)).ToArray();
    private static readonly EntityTagHeaderValue Version = new("\"cache-generation\"");
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Range cache condition failed"); }
    private static async Task Read(HttpRangeStream stream, int index)
    {
        stream.Position = index * Block;
        var bytes = new byte[17];
        Require(await stream.ReadAsync(bytes) == 17 && bytes.SequenceEqual(Song.AsSpan(index * Block, 17).ToArray()));
    }

    internal static async Task StrongCacheAsync()
    {
        int calls = 0;
        using var stream = await Open(request => { calls++; return Response(request, Version); });
        foreach (int index in new[] { 0, 1, 7, 0, 7 }) await Read(stream, index);
        Require(calls == 4); // One length probe and three different blocks.
        foreach (int index in new[] { 2, 3, 4, 5, 6, 0, 8, 0, 7 }) await Read(stream, index);
        Require(calls == 10); // Recently touched head and tail retained across ninth-block eviction.
        await Read(stream, 1);
        Require(calls == 11); // Oldest block was evicted, not every previously read block.
        stream.Position = Block - 8;
        byte[] crossing = new byte[25]; await stream.ReadExactlyAsync(crossing);
        Require(crossing.SequenceEqual(Song.AsSpan(Block - 8, 25).ToArray()) && calls == 11);
        stream.Position = Song.Length - 10;
        byte[] tail = new byte[32];
        Require(await stream.ReadAsync(tail) == 10 && tail.AsSpan(0, 10).SequenceEqual(Song.AsSpan(Song.Length - 10)));
        Require(await stream.ReadAsync(tail) == 0);
    }

    internal static async Task FailurePreservesCacheAsync()
    {
        foreach (bool canceled in new[] { true, false })
        {
            using var cancellation = new CancellationTokenSource();
            int calls = 0; bool reject = true;
            using var stream = await Open(request =>
            {
                calls++;
                bool failing = request.Headers.Range!.Ranges.Single().From == 8L * Block && reject;
                if (failing && canceled) return Response(request, Version, cancellation.Cancel);
                return Response(request, failing ? new EntityTagHeaderValue("\"changed\"") : Version);
            });
            for (int index = 0; index < 8; index++) await Read(stream, index);
            stream.Position = 8L * Block;
            byte[] destination = Enumerable.Repeat((byte)0xcc, 17).ToArray(); bool observed = false;
            try { _ = await stream.ReadAsync(destination, cancellation.Token); }
            catch (OperationCanceledException) when (canceled) { observed = true; }
            catch (MusicApiException ex) when (!canceled && ex.Failure == MusicFailure.InvalidResponse) { observed = true; }
            Require(observed && stream.Position == 8L * Block && destination.All(value => value == 0xcc) && calls == 10);
            await Read(stream, 0); Require(calls == 10); // Refused/late data must not evict an older validated block.
            reject = false; await Read(stream, 8); Require(calls == 11); // Refused block must be requested again.
        }
        int hitCalls = 0;
        using var hit = await Open(request => { hitCalls++; return Response(request, Version); });
        await Read(hit, 0); await Read(hit, 1); hit.Position = 0;
        using var alreadyCanceled = new CancellationTokenSource(); alreadyCanceled.Cancel();
        byte[] untouched = Enumerable.Repeat((byte)0xcc, 17).ToArray();
        bool rejectedHit = false;
        try { _ = await hit.ReadAsync(untouched, alreadyCanceled.Token); }
        catch (OperationCanceledException) { rejectedHit = true; }
        Require(rejectedHit && hit.Position == 0 && untouched.All(value => value == 0xcc) && hitCalls == 3);
        hit.Dispose(); bool rejectedClosed = false;
        try { _ = await hit.ReadAsync(untouched); }
        catch (ObjectDisposedException) { rejectedClosed = true; }
        Require(rejectedClosed && !hit.CanRead && !hit.CanSeek && hit.Position == 0 && hitCalls == 3);
    }

    internal static async Task UnversionedCacheAsync()
    {
        foreach (bool weak in new[] { false, true })
        {
            int calls = 0;
            using var stream = await Open(request =>
            {
                calls++; Require(request.Headers.IfMatch.Count == 0);
                return Response(request, weak ? new EntityTagHeaderValue(Version.Tag, true) : null);
            });
            foreach (int index in new[] { 0, 1, 0 }) await Read(stream, index);
            Require(calls == 4); // Without a strong representation, keep the previous single-block policy.
        }
    }

    private static Task<HttpRangeStream> Open(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        HttpRangeStream.OpenAsync(new HttpClient(new Handler(response)), new Uri("https://range.invalid/audio"), default);
    private static HttpResponseMessage Response(HttpRequestMessage request, EntityTagHeaderValue? version, Action? completed = null)
    {
        var range = request.Headers.Range!.Ranges.Single(); int from = checked((int)range.From!.Value), to = checked((int)range.To!.Value);
        if (!(from == 0 && to == 0) && version?.Tag == Version.Tag && !version.IsWeak)
            Require(request.Headers.IfMatch.Count == 1 && request.Headers.IfMatch.Single().Tag == Version.Tag);
        var data = Song[from..(to + 1)];
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        { Content = completed is null ? new ByteArrayContent(data) : new StreamContent(new CompletionStream(data, completed)) };
        response.Headers.ETag = version;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, Song.Length);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg"); return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request)); }
    private sealed class CompletionStream(byte[] data, Action completed) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { int count = await base.ReadAsync(buffer, CancellationToken.None); completed(); return count; }
    }
}
