using System.Net;
using System.Net.Http.Headers;
using FnMusic.Infrastructure;

internal static class RangeStreamVerification
{
    private static readonly byte[] Song = Enumerable.Range(0, 100).Select(index => (byte)(index + 1)).ToArray();
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("Range stream lifecycle invariant failed."); }

    public static async Task CallerCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        using var stream = await HttpRangeStream.OpenAsync(new HttpClient(new Handler(request =>
        {
            int call = ++calls;
            return Response(request, call == 2 ? new CompletionStream(Song, cancellation.Cancel) : new MemoryStream(Song));
        })), new Uri("https://range.invalid/audio"), default);
        byte[] destination = Enumerable.Repeat((byte)0xcc, 10).ToArray();
        bool canceled = false;
        try { _ = await stream.ReadAsync(destination, cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Require(canceled && stream.Position == 0 && destination.All(value => value == 0xcc));
        Require(await stream.ReadAsync(destination) == 10 && destination.SequenceEqual(Song[..10]) && calls == 3);
    }

    public static async Task DisposalAsync()
    {
        HttpRangeStream? stream = null;
        int calls = 0;
        stream = await HttpRangeStream.OpenAsync(new HttpClient(new Handler(request =>
            Response(request, ++calls == 2 ? new CompletionStream(Song, () => stream!.Dispose()) : new MemoryStream(Song)))),
            new Uri("https://range.invalid/audio"), default);
        using (stream)
        {
            byte[] destination = Enumerable.Repeat((byte)0xcc, 10).ToArray();
            bool stopped = false;
            try { _ = await stream.ReadAsync(destination); }
            catch (OperationCanceledException) { stopped = true; }
            catch (ObjectDisposedException) { stopped = true; }
            Require(stopped && !stream.CanRead && !stream.CanSeek && stream.Position == 0 && destination.All(value => value == 0xcc));
        }
    }

    public static async Task QueuedSeekAsync()
    {
        using var body = new DeferredStream(Song);
        int calls = 0;
        using var stream = await HttpRangeStream.OpenAsync(new HttpClient(new Handler(request =>
            Response(request, ++calls == 2 ? body : new MemoryStream(Song)))), new Uri("https://range.invalid/audio"), default);
        var reading = stream.ReadAsync(new byte[10]).AsTask();
        var started = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seeking = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            started.TrySetResult(Thread.CurrentThread);
            try { stream.Seek(16, SeekOrigin.Begin); seeking.TrySetResult(null); }
            catch (Exception exception) { seeking.TrySetResult(exception); }
        }) { IsBackground = true };
        bool wokeBeforeBodyRelease = false;
        Exception? seekFailure = null;
        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            thread.Start();
            var worker = await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var waitLimit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while ((worker.ThreadState & ThreadState.WaitSleepJoin) == 0)
            { Require(!seeking.Task.IsCompleted); await Task.Delay(10, waitLimit.Token); }
            stream.Dispose();
            try
            {
                seekFailure = await seeking.Task.WaitAsync(TimeSpan.FromSeconds(2));
                wokeBeforeBodyRelease = true;
            }
            catch (TimeoutException) { }
        }
        finally
        {
            body.Release.TrySetResult();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            if ((thread.ThreadState & ThreadState.Unstarted) == 0 && !thread.Join(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("Synthetic seek worker did not stop.");
        }
        Require(wokeBeforeBodyRelease && seekFailure is ObjectDisposedException && stream.Position == 0);
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, Stream body)
    {
        var range = request.Headers.Range!.Ranges.Single();
        HttpContent content;
        if (range.From == 0 && range.To == 0) { body.Dispose(); content = new ByteArrayContent([Song[0]]); }
        else content = new StreamContent(body);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(range.From!.Value, range.To!.Value, Song.Length);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }

    private sealed class CompletionStream(byte[] bytes, Action completed) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int count = await base.ReadAsync(buffer, CancellationToken.None);
            completed();
            return count;
        }
    }

    private sealed class DeferredStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            // Deliberately delay return even after cancellation, to prove the queued
            // seek can stop independently and late bytes cannot be published.
            await Release.Task;
            return await base.ReadAsync(buffer, CancellationToken.None);
        }
    }
}
