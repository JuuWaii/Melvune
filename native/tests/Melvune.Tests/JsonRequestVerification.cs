using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Melvune.Core;
using Melvune.Infrastructure;

internal static class JsonRequestVerification
{
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("JSON request lifetime invariant failed."); }

    public static async Task BodyTimeoutAsync()
    {
        await using var server = new Server(TimeSpan.Zero);
        using var api = new NasApiClient(server.Endpoint, new SocketsHttpHandler { UseProxy = false });
        api.RestoreSession("fixture-session");
        using var cancel = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var pending = api.ListTracksAsync(1, 10, cancel.Token);
        await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            bool unavailable = false;
            try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(18)); }
            catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unavailable) { unavailable = true; }
            Require(unavailable && watch.Elapsed >= TimeSpan.FromSeconds(14) && server.Calls == 1);
            await server.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var user = await api.GetCurrentUserAsync(default);
            Require(user.Name == "fixture" && server.Calls == 2 && server.SessionHeadersValid);
        }
        finally { cancel.Cancel(); try { await pending; } catch (Exception) { } }
    }

    public static async Task TotalDeadlineAsync()
    {
        await using var server = new Server(TimeSpan.FromSeconds(8));
        using var api = new NasApiClient(server.Endpoint, new SocketsHttpHandler { UseProxy = false });
        api.RestoreSession("fixture-session");
        using var cancel = new CancellationTokenSource();
        var watch = Stopwatch.StartNew(); var pending = api.ListTracksAsync(1, 10, cancel.Token);
        try
        {
            await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(11));
            bool unavailable = false;
            try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unavailable) { unavailable = true; }
            Require(unavailable && watch.Elapsed >= TimeSpan.FromSeconds(14) && watch.Elapsed < TimeSpan.FromSeconds(20));
            await server.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(server.Calls == 1);
        }
        finally { cancel.Cancel(); try { await pending; } catch (Exception) { } }
    }

    public static async Task CallerCancellationAsync()
    {
        await using var server = new Server(TimeSpan.Zero);
        using var api = new NasApiClient(server.Endpoint, new SocketsHttpHandler { UseProxy = false });
        api.RestoreSession("fixture-session");
        using var cancel = new CancellationTokenSource();
        var pending = api.ListTracksAsync(1, 10, cancel.Token);
        await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        api.RestoreSession("replacement-session"); server.ExpectedSession = "replacement-session";
        cancel.Cancel(); bool canceled = false;
        try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { canceled = true; }
        await server.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(canceled && server.Calls == 1);
        Require((await api.GetCurrentUserAsync(default)).Name == "fixture" && server.SessionHeadersValid && server.Calls == 2);
    }

    public static async Task DisposalAsync()
    {
        await using var server = new Server(TimeSpan.Zero);
        using var api = new NasApiClient(server.Endpoint, new SocketsHttpHandler { UseProxy = false });
        api.RestoreSession("fixture-session");
        using var cleanup = new CancellationTokenSource();
        var pending = api.ListTracksAsync(1, 10, cleanup.Token);
        await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew(); api.Dispose(); api.Dispose();
        try
        {
            bool stopped = false;
            try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unavailable) { stopped = true; }
            await server.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(stopped && watch.Elapsed < TimeSpan.FromSeconds(3) && server.Calls == 1);
        }
        finally { cleanup.Cancel(); try { await pending; } catch (Exception) { } }
    }

    internal sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<Task> connections = [];
        private readonly Task accepting;
        private readonly TimeSpan headerDelay;
        private readonly bool cover;
        private readonly bool closeBody;
        internal static readonly byte[] CoverBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aKuoAAAAASUVORK5CYII=");
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public ServerEndpoint Endpoint { get; }
        public string ExpectedSession = "fixture-session";
        public bool SessionHeadersValid = true;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PeerClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Server(TimeSpan headerDelay, bool cover = false, bool closeBody = false)
        {
            this.headerDelay = headerDelay; this.cover = cover; this.closeBody = closeBody; listener.Start();
            Endpoint = ServerEndpoint.Parse($"http://{IPAddress.Loopback}:{((IPEndPoint)listener.LocalEndpoint).Port}/music/");
            accepting = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                    connections.Add(ServeAsync(await listener.AcceptTcpClientAsync(lifetime.Token)));
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
        }
        private async Task ServeAsync(TcpClient connection)
        {
            using (connection)
            using (var stream = connection.GetStream())
            {
                var bytes = new List<byte>(); var one = new byte[1];
                while (bytes.Count < 8192)
                {
                    Require(await stream.ReadAsync(one, lifetime.Token) == 1); bytes.Add(one[0]);
                    if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
                SessionHeadersValid &= lines.Any(line => string.Equals(line, "Cookie: music-token=" + ExpectedSession, StringComparison.Ordinal));
                int index = Interlocked.Increment(ref calls);
                Require(lines[0].StartsWith(cover ? "GET /music/api/v1/static/cover?coverId=fixture-cover&size="
                    : index == 1 ? "GET /music/api/v1/track/list?" : "GET /music/api/v1/user/me ", StringComparison.Ordinal));
                string body = index == 1 ? "{\"code\":0,\"data\":{\"list\":[],\"total\":0}}" : "{\"code\":0,\"data\":{\"name\":\"fixture\"}}";
                byte[] payload = cover ? CoverBytes : Encoding.UTF8.GetBytes(body);
                if (index == 1) await Task.Delay(headerDelay, lifetime.Token);
                string reply = $"HTTP/1.1 200 OK\r\nContent-Type: {(cover ? "image/png" : "application/json")}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), lifetime.Token);
                await stream.WriteAsync(index == 1 ? payload.AsMemory(0, 1) : payload.AsMemory(), lifetime.Token);
                if (index == 1)
                {
                    Entered.TrySetResult();
                    if (closeBody) { PeerClosed.TrySetResult(); return; }
                    try { Require(await stream.ReadAsync(one, lifetime.Token) == 0); }
                    catch (IOException) { /* Cancellation may reset the socket instead of sending FIN. */ }
                    PeerClosed.TrySetResult();
                }
            }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); listener.Stop(); await accepting;
            try { await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { lifetime.Dispose(); }
        }
    }
}
