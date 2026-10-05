using System.Diagnostics;
using FnMusic.Core;
using FnMusic.Infrastructure;

internal static class CoverRequestVerification
{
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("Cover request lifetime invariant failed."); }
    public static async Task BodyTimeoutAsync()
    {
        await using var server = new JsonRequestVerification.Server(TimeSpan.Zero, cover: true);
        using var api = new NasApiClient(server.Endpoint, new SocketsHttpHandler { UseProxy = false });
        api.RestoreSession("fixture-session");
        using var cancel = new CancellationTokenSource(); var watch = Stopwatch.StartNew();
        var pending = api.GetCoverAsync("fixture-cover", NasCoverSize.Large, cancel.Token);
        await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            bool unavailable = false;
            try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(18)); }
            catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unavailable) { unavailable = true; }
            Require(unavailable && watch.Elapsed >= TimeSpan.FromSeconds(14) && server.Calls == 1);
            await server.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var retried = await api.GetCoverAsync("fixture-cover", NasCoverSize.Large, default);
            Require(retried!.SequenceEqual(JsonRequestVerification.Server.CoverBytes) && server.Calls == 2 && server.SessionHeadersValid);
        }
        finally { cancel.Cancel(); try { await pending; } catch (Exception) { } }
    }
    public static async Task TotalDeadlineAsync()
    {
        await using var server = new JsonRequestVerification.Server(TimeSpan.FromSeconds(8), cover: true);
        using var api = new NasApiClient(server.Endpoint, new SocketsHttpHandler { UseProxy = false });
        api.RestoreSession("fixture-session");
        using var cancel = new CancellationTokenSource(); var watch = Stopwatch.StartNew();
        var pending = api.GetCoverAsync("fixture-cover", NasCoverSize.Medium, cancel.Token);
        try
        {
            await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(11));
            bool unavailable = false;
            try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unavailable) { unavailable = true; }
            Require(unavailable && watch.Elapsed >= TimeSpan.FromSeconds(14) && watch.Elapsed < TimeSpan.FromSeconds(20));
            await server.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3)); Require(server.Calls == 1);
        }
        finally { cancel.Cancel(); try { await pending; } catch (Exception) { } }
    }
    public static async Task CallerCancellationAsync()
    {
        await using var server = new JsonRequestVerification.Server(TimeSpan.Zero, cover: true);
        using var api = new NasApiClient(server.Endpoint, new SocketsHttpHandler { UseProxy = false });
        api.RestoreSession("fixture-session"); using var cancel = new CancellationTokenSource();
        var pending = api.GetCoverAsync("fixture-cover", NasCoverSize.Medium, cancel.Token);
        await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        api.RestoreSession("replacement-session"); server.ExpectedSession = "replacement-session";
        cancel.Cancel(); bool canceled = false;
        try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { canceled = true; }
        await server.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3)); Require(canceled && server.Calls == 1);
        var retried = await api.GetCoverAsync("fixture-cover", NasCoverSize.Medium, default);
        Require(retried!.SequenceEqual(JsonRequestVerification.Server.CoverBytes) && server.SessionHeadersValid && server.Calls == 2);
    }
    public static async Task DisconnectedBodyAsync()
    {
        await using var server = new JsonRequestVerification.Server(TimeSpan.Zero, cover: true, closeBody: true);
        using var api = new NasApiClient(server.Endpoint, new SocketsHttpHandler { UseProxy = false });
        api.RestoreSession("fixture-session"); bool unavailable = false;
        try { _ = await api.GetCoverAsync("fixture-cover", default).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unavailable) { unavailable = true; }
        Require(unavailable && server.Calls == 1);
        Require((await api.GetCoverAsync("fixture-cover", default))!.SequenceEqual(JsonRequestVerification.Server.CoverBytes)
            && server.Calls == 2 && server.SessionHeadersValid);
    }
    public static async Task DisposalAsync()
    {
        await using var server = new JsonRequestVerification.Server(TimeSpan.Zero, cover: true);
        using var api = new NasApiClient(server.Endpoint, new SocketsHttpHandler { UseProxy = false });
        api.RestoreSession("fixture-session"); using var cleanup = new CancellationTokenSource();
        var pending = api.GetCoverAsync("fixture-cover", NasCoverSize.Large, cleanup.Token);
        await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew(); api.Dispose(); api.Dispose();
        try
        {
            bool stopped = false;
            try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unavailable) { stopped = true; }
            await server.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(stopped && watch.Elapsed < TimeSpan.FromSeconds(3) && server.Calls == 1);
            bool rejected = false;
            try { _ = await api.GetCoverAsync("fixture-cover", default); }
            catch (ObjectDisposedException) { rejected = true; }
            Require(rejected && server.Calls == 1);
        }
        finally { cleanup.Cancel(); try { await pending; } catch (Exception) { } }
    }
}
