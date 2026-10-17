using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Melvune.Infrastructure;

internal static class LoopbackRangeVerification
{
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("Loopback Range invariant failed."); }
    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false });

    public static async Task DisposalAsync()
    {
        await using var server = new Server(Stall.Body);
        using var stream = await HttpRangeStream.OpenAsync(Client(), server.Address, default);
        var destination = Enumerable.Repeat((byte)0xcc, 10).ToArray();
        var reading = stream.ReadAsync(destination).AsTask();
        await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(!reading.IsCompleted);
        var watch = Stopwatch.StartNew();
        stream.Dispose();
        bool canceled = false;
        try { _ = await reading.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { canceled = true; }
        catch (ObjectDisposedException) { canceled = true; }
        await server.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(canceled && watch.Elapsed < TimeSpan.FromSeconds(3) && stream.Position == 0
            && destination.All(value => value == 0xcc));
    }

    public static async Task BodyTimeoutAsync()
    {
        await using var server = new Server(Stall.Body);
        using var stream = await HttpRangeStream.OpenAsync(Client(), server.Address, default);
        var destination = Enumerable.Repeat((byte)0xcc, 10).ToArray();
        var watch = Stopwatch.StartNew();
        bool canceled = false;
        try { _ = await stream.ReadAsync(destination).AsTask().WaitAsync(TimeSpan.FromSeconds(22)); }
        catch (OperationCanceledException) { canceled = true; }
        await server.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(canceled && watch.Elapsed >= TimeSpan.FromSeconds(14) && stream.Position == 0
            && destination.All(value => value == 0xcc) && stream.CanRead);
        server.Resume();
        Require(await stream.ReadAsync(destination) == 10 && destination.SequenceEqual(Server.Song[..10]) && stream.Position == 10);
    }

    public static async Task OpenTimeoutAsync()
    {
        await using var server = new Server(Stall.Headers);
        var watch = Stopwatch.StartNew();
        var opening = HttpRangeStream.OpenAsync(Client(), server.Address, default);
        await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        bool canceled = false;
        try { using var unexpected = await opening.WaitAsync(TimeSpan.FromSeconds(22)); }
        catch (OperationCanceledException) { canceled = true; }
        await server.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(canceled && watch.Elapsed >= TimeSpan.FromSeconds(14));
    }

    private enum Stall { None, Headers, Body }
    private sealed class Server : IAsyncDisposable
    {
        public static readonly byte[] Song = Enumerable.Range(0, 100).Select(index => (byte)(index + 1)).ToArray();
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<Task> connections = [];
        private readonly Task accepting;
        private int stall;
        public Uri Address { get; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PeerClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Server(Stall mode)
        {
            stall = (int)mode;
            listener.Start();
            Address = new UriBuilder("http", "localhost", ((IPEndPoint)listener.LocalEndpoint).Port, "audio").Uri;
            accepting = AcceptAsync();
        }

        public void Resume() => Volatile.Write(ref stall, (int)Stall.None);
        private async Task AcceptAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var connection = await listener.AcceptTcpClientAsync(lifetime.Token);
                    connections.Add(ServeAsync(connection));
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient connection)
        {
            using (connection)
            using (var stream = connection.GetStream())
            {
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 8192)
                {
                    Require(await stream.ReadAsync(one, lifetime.Token) == 1);
                    header.Add(one[0]);
                    if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                Require(lines[0].StartsWith("GET /audio HTTP/", StringComparison.Ordinal));
                string rangeText = lines.Single(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                var range = RangeHeaderValue.Parse(rangeText).Ranges.Single();
                Require(range.From == 0 && range.To is 0 or 99);
                var mode = (Stall)Volatile.Read(ref stall);
                if (mode == Stall.Headers) { await WaitForPeerAsync(stream); return; }
                int count = checked((int)(range.To!.Value + 1));
                string reply = $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes 0-{count - 1}/{Song.Length}\r\nContent-Length: {count}\r\nContent-Type: audio/flac\r\nConnection: close\r\n\r\n";
                int bodyCount = count == 1 || mode != Stall.Body ? count : 10;
                var headerBytes = Encoding.ASCII.GetBytes(reply);
                var replyBytes = new byte[headerBytes.Length + bodyCount];
                headerBytes.CopyTo(replyBytes, 0); Song.AsSpan(0, bodyCount).CopyTo(replyBytes.AsSpan(headerBytes.Length));
                // Submit the probe's headers and single body byte together, so a
                // headers-only consumer closing promptly cannot race a second write.
                await stream.WriteAsync(replyBytes, lifetime.Token);
                if (count > 1 && mode == Stall.Body) await WaitForPeerAsync(stream);
            }
        }

        private async Task WaitForPeerAsync(NetworkStream stream)
        {
            Entered.TrySetResult();
            try { Require(await stream.ReadAsync(new byte[1], lifetime.Token) == 0); }
            catch (IOException) { /* The canceled HTTP client may reset rather than FIN. */ }
            PeerClosed.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); listener.Stop();
            await accepting;
            try { await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { lifetime.Dispose(); }
        }
    }
}
