using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;

int passed = 0;
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
async Task Test(string name, Func<Task> run) { await run(); passed++; Console.WriteLine("PASS " + name); }
string directory = Path.Combine(Path.GetTempPath(), "FnMusic.Information.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var hub = new PlaybackSnapshotHub();
    Guid generation = Guid.NewGuid();
    hub.SetLyrics(generation, PlaybackLyricsKind.Timed, new(new[] { new LyricLine(0, "独立消费者歌词"), new LyricLine(1, "第二行") }, true));
    hub.Publish(hub.Current with { TrackGeneration = generation, Title = "独立进程歌曲", Artist = "测试歌手", Album = "公开元数据",
        State = PlaybackStatus.Playing, LyricsKind = PlaybackLyricsKind.Timed, PositionSeconds = 1.25, DurationSeconds = 180,
        CurrentLineIndex = 1, CurrentLine = "第二行" });
    await using var server = new PlaybackInformationServer(hub, directory);
    await server.StartAsync();
    await Test("discovery contains exactly version, random instance and random local pipe", async () =>
    {
        using var record = JsonDocument.Parse(await File.ReadAllBytesAsync(server.DiscoveryPath));
        Check(record.RootElement.EnumerateObject().Count() == 3, "discovery field count");
        Check(record.RootElement.GetProperty("instanceId").GetGuid() == hub.InstanceId, "instance mismatch");
        Check(record.RootElement.GetProperty("pipeName").GetString() == server.PipeName, "pipe mismatch");
        await using var client = await ConnectAsync(server.PipeName);
        var rules = client.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>();
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        Check(rules.Any(rule => rule.IdentityReference.Equals(network) && rule.AccessControlType == AccessControlType.Deny
            && (rule.PipeAccessRights & PipeAccessRights.ReadData) != 0), "network logon must be denied by ACL");
    });
    await Test("real independent process reads snapshot, anonymous track and lyrics", async () =>
    {
        var snapshot = await ConsumerAsync(directory, "snapshot");
        using var parsed = JsonDocument.Parse(snapshot.Single());
        var data = parsed.RootElement.GetProperty("data");
        Check(data.GetProperty("title").GetString() == "独立进程歌曲", "snapshot title");
        Check(data.GetProperty("trackGeneration").GetGuid() == generation, "anonymous track");
        Check(data.GetProperty("positionSeconds").GetDouble() == 1.25, "actual clock");
        Check(!snapshot[0].Contains("sourceInstanceId", StringComparison.OrdinalIgnoreCase)
            && !snapshot[0].Contains("trackReference", StringComparison.OrdinalIgnoreCase), "private fields");
        using var lyrics = JsonDocument.Parse((await ConsumerAsync(directory, "lyrics")).Single());
        Check(lyrics.RootElement.GetProperty("data").GetProperty("lines")[0].GetProperty("text").GetString() == "独立消费者歌词", "lyrics read");
    });
    await Test("independent process receives initial subscription and later actual states", async () =>
    {
        var consumer = ConsumerAsync(directory, "subscribe", "--count", "3");
        for (int i = 0; i < 80 && !consumer.IsCompleted; i++)
        {
            await Task.Delay(25);
            hub.Publish(hub.Current with { PositionSeconds = 2 + i, State = i % 2 == 0 ? PlaybackStatus.Paused : PlaybackStatus.Playing });
        }
        var messages = await consumer;
        Check(messages.Length == 3, "subscription count");
        var sequences = messages.Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.GetProperty("data").GetProperty("sequence").GetInt64(); }).ToArray();
        Check(sequences[2] > sequences[0], "subscription progression");
    });
    await Test("unknown controls and unsupported versions fail without leaking request or exception", async () =>
    {
        using var control = await RequestAsync(server.PipeName, new { version = 1, command = "play", id = "control" });
        Check(control.RootElement.GetProperty("error").GetString() == "unknownCommand", "readonly commands");
        using var version = await RequestAsync(server.PipeName, new { version = 99, command = "snapshot" });
        Check(version.RootElement.GetProperty("error").GetString() == "unsupportedVersion", "version rejection");
        using var malformed = await RawRequestAsync(server.PipeName, new byte[] { 255, 10 });
        Check(malformed.RootElement.GetProperty("error").GetString() == "invalidRequest", "invalid utf8 rejection");
    });
    await Test("lyrics escaped-byte paging, text and total bounds, changed-track isolation", async () =>
    {
        string longLine = new('歌', 2100);
        hub.SetLyrics(generation, PlaybackLyricsKind.Timed,
            new(Enumerable.Range(0, 2010).Select(i => new LyricLine(i, longLine)).ToArray(), true));
        using var page = await RequestAsync(server.PipeName, new { version = 1, command = "lyrics", limit = 64 });
        var data = page.RootElement.GetProperty("data");
        Check(data.GetProperty("totalLines").GetInt32() == 2000 && data.GetProperty("isLimited").GetBoolean(), "total/limited bound");
        var lines = data.GetProperty("lines");
        Check(lines.GetArrayLength() is > 0 and < 64, "byte-driven page");
        Check(lines[0].GetProperty("text").GetString()!.Length == 2048, "line text bound");
        Check(Encoding.UTF8.GetByteCount(page.RootElement.GetRawText()) <= 65536, "frame byte bound");
        int next = data.GetProperty("nextOffset").GetInt32();
        using var following = await RequestAsync(server.PipeName, new { version = 1, command = "lyrics", offset = next, limit = 64, trackGeneration = generation });
        Check(following.RootElement.GetProperty("data").GetProperty("lines")[0].GetProperty("seconds").GetDouble() == next, "page continuity");
        Guid newGeneration = Guid.NewGuid();
        hub.Publish(hub.Current with { TrackGeneration = newGeneration, LyricsKind = PlaybackLyricsKind.Loading });
        using var changed = await RequestAsync(server.PipeName, new { version = 1, command = "lyrics", trackGeneration = generation });
        Check(changed.RootElement.GetProperty("error").GetString() == "trackChanged", "changed generation");
        using var pending = await RequestAsync(server.PipeName, new { version = 1, command = "lyrics" });
        Check(pending.RootElement.GetProperty("data").GetProperty("lines").GetArrayLength() == 0, "late previous lyrics excluded");
    });
    await Test("plain and empty lyrics retain their understandable state", async () =>
    {
        hub.SetLyrics(hub.Current.TrackGeneration, PlaybackLyricsKind.Plain, new(new[] { new LyricLine(0, "纯文本") }, false));
        hub.Publish(hub.Current with { LyricsKind = PlaybackLyricsKind.Plain });
        using var plain = await RequestAsync(server.PipeName, new { version = 1, command = "lyrics" });
        Check(plain.RootElement.GetProperty("data").GetProperty("kind").GetString() == "plain", "plain kind");
        hub.SetLyrics(hub.Current.TrackGeneration, PlaybackLyricsKind.Empty, Lyrics.Empty);
        hub.Publish(hub.Current with { LyricsKind = PlaybackLyricsKind.Empty });
        using var empty = await RequestAsync(server.PipeName, new { version = 1, command = "lyrics" });
        Check(empty.RootElement.GetProperty("data").GetProperty("totalLines").GetInt32() == 0, "empty lyrics");
    });
    await Test("oversized request is disconnected and healthy requests still work", async () =>
    {
        await using var pipe = await ConnectAsync(server.PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 4097) + "\n"), timeout.Token);
            Check(await pipe.ReadAsync(new byte[1], timeout.Token) == 0, "oversize disconnect");
        }
        catch (IOException) { }
        using var healthy = await RequestAsync(server.PipeName, new { version = 1, command = "snapshot" });
        Check(healthy.RootElement.GetProperty("ok").GetBoolean(), "healthy recovery");
    });
    await Test("four slow subscribers bound slots and never block publication; timed-out slots recover", async () =>
    {
        // Allow the earlier independent subscriber's heartbeat to notice its closed connection.
        await Task.Delay(5500);
        var clients = new List<NamedPipeClientStream>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var client = await ConnectAsync(server.PipeName);
                clients.Add(client);
                await client.WriteAsync(Encoding.UTF8.GetBytes("{\"version\":1,\"command\":\"subscribe\"}\n"));
            }
            await Task.Delay(100);
            await using var fifth = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var unavailable = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            bool bounded = false;
            try { await fifth.ConnectAsync(unavailable.Token); }
            catch (OperationCanceledException) { bounded = true; }
            Check(bounded, "fifth client must wait");
            hub.Publish(hub.Current with { Title = new string('歌', 2048), Artist = new string('手', 2048), Album = new string('集', 2048), CurrentLine = new string('词', 2048) });
            await Task.Delay(100);
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++) hub.Publish(hub.Current with { PositionSeconds = i });
            Check(clock.Elapsed < TimeSpan.FromSeconds(2), "publisher blocked by client");
            await Task.Delay(4000);
            using var recovered = await RequestAsync(server.PipeName, new { version = 1, command = "snapshot" });
            Check(recovered.RootElement.GetProperty("ok").GetBoolean(), "slow client slot recovery");
        }
        finally { foreach (var client in clients) client.Dispose(); }
    });
    await Test("multi-instance disable/exit only removes owned discovery and re-enable gets fresh pipe", async () =>
    {
        var otherHub = new PlaybackSnapshotHub();
        await using var other = new PlaybackInformationServer(otherHub, directory);
        await other.StartAsync();
        string oldPipe = server.PipeName;
        await server.DisposeAsync();
        Check(!File.Exists(server.DiscoveryPath) && File.Exists(other.DiscoveryPath), "owned discovery cleanup");
        await using var restarted = new PlaybackInformationServer(hub, directory);
        await restarted.StartAsync();
        Check(restarted.PipeName != oldPipe && File.Exists(restarted.DiscoveryPath), "fresh pipe after enable");
        using var snapshot = await RequestAsync(restarted.PipeName, new { version = 1, command = "snapshot" });
        Check(snapshot.RootElement.GetProperty("ok").GetBoolean(), "restart readable");
    });
    Console.WriteLine($"Information checks: {passed} passed.");
    return 0;
}
finally
{
    // The target was created by this test and stays under the checked temporary root.
    string full = Path.GetFullPath(directory);
    string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
    if (full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(full).StartsWith("FnMusic.Information.Tests-", StringComparison.Ordinal)) Directory.Delete(full, true);
}

static async Task<NamedPipeClientStream> ConnectAsync(string name)
{
    var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try { await pipe.ConnectAsync(timeout.Token); return pipe; }
    catch { pipe.Dispose(); throw; }
}
static Task<JsonDocument> RequestAsync(string name, object request) =>
    RawRequestAsync(name, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n"));
static async Task<JsonDocument> RawRequestAsync(string name, byte[] request)
{
    await using var pipe = await ConnectAsync(name);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await pipe.WriteAsync(request, timeout.Token);
    var response = await PlaybackInformationProtocol.ReadFrameAsync(pipe, 65536, timeout.Token);
    Check(response is not null, "missing response");
    return JsonDocument.Parse(response!);
}
static async Task<string[]> ConsumerAsync(string directory, params string[] arguments)
{
    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "info-client", "FnMusic.InfoClient.dll"));
    start.ArgumentList.Add("--data-directory"); start.ArgumentList.Add(directory);
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("consumer launch failed");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { process.Kill(entireProcessTree: true); throw; }
    _ = await error;
    Check(process.ExitCode == 0, "independent consumer failed");
    return (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
