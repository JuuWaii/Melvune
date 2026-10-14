using System.Reflection;
using System.Text.Json;
using FnMusic.Infrastructure;

internal static class HttpJsonConnectionVerification
{
    public static async Task RunAsync()
    {
        Normalize();
        await RoundtripAsync();
        await AtomicCommitAsync();
        await LockAsync();
        await CorruptAsync();
        await LimitsAsync();
    }

    private static HttpJsonConnection Connection => new(Guid.NewGuid(), "Fixture catalog",
        "https://catalog.example.invalid/proxy", new HttpJsonDefinition());
    private static void Require(bool condition, string context)
    { if (!condition) throw new InvalidOperationException("HTTP JSON connection verification failed: " + context); }
    private static async Task RejectAsync<T>(Func<Task> action, string context) where T : Exception
    {
        bool rejected = false;
        try { await action(); } catch (T) { rejected = true; }
        Require(rejected, context);
    }
    private static string DirectoryPath() => Path.Combine(Path.GetTempPath(), "fnmusic-http-json-store-" + Guid.NewGuid().ToString("N"));
    private static void Cleanup(string directory)
    {
        string target = Path.GetFullPath(directory);
        string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(target).StartsWith("fnmusic-http-json-store-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid fixture cleanup target.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }

    private static void Normalize()
    {
        var normalized = HttpJsonConnectionStore.NormalizeConnection(Connection with { Name = "  Named  " });
        Require(normalized.Name == "Named" && normalized.ServerAddress == "https://catalog.example.invalid/proxy/",
            "name and reverse proxy address normalized");
        foreach (var invalid in new[] { Connection with { SourceId = Guid.Empty }, Connection with { Name = "" },
            Connection with { Name = "name\n" }, Connection with { Name = new string('x', 121) },
            Connection with { Definition = null! }, Connection with { ServerAddress = "file:///catalog" },
            Connection with { ServerAddress = "https://catalog.example.invalid/?api_key=private" },
            Connection with { ServerAddress = string.Concat("https://", "user:private@", "catalog.example.invalid") } })
        {
            bool rejected = false;
            try { HttpJsonConnectionStore.NormalizeConnection(invalid); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "invalid or credential-bearing connection rejected");
        }
    }

    private static async Task RoundtripAsync()
    {
        string directory = DirectoryPath();
        try
        {
            var store = new HttpJsonConnectionStore(directory); var connection = Connection;
            Require((await store.LoadAsync()).Count == 0, "fresh store empty");
            await store.SaveAsync(connection);
            var restart = new HttpJsonConnectionStore(directory);
            Require((await restart.LoadAsync()).Single() == HttpJsonConnectionStore.NormalizeConnection(connection),
                "persistent identity and mapping survive reconstruction");
            string index = Path.Combine(directory, "http-json-connections.json"), original = await File.ReadAllTextAsync(index);
            Require(!original.Contains("Password", StringComparison.Ordinal) && !original.Contains("AccessToken", StringComparison.Ordinal) &&
                !original.Contains("PlaybackUrl", StringComparison.Ordinal), "index contains catalog descriptions only");
            await RejectAsync<InvalidOperationException>(() => store.SaveAsync(connection with { ServerAddress = "https://other.example.invalid" }),
                "server replacement needs a new source");
            await RejectAsync<InvalidOperationException>(() => store.SaveAsync(connection with { Definition = new HttpJsonDefinition { ListPath = "alternate" } }),
                "catalog mapping replacement needs a new source");
            await RejectAsync<InvalidOperationException>(() => store.SaveAsync(connection with { SourceId = Guid.NewGuid(), Name = "Duplicate" }),
                "duplicate anonymous endpoint rejected");
            await RejectAsync<ArgumentException>(() => store.RemoveAsync(Guid.Empty), "empty removal identity rejected");
            Require(await File.ReadAllTextAsync(index) == original, "invalid edits preserve saved index");
            await store.SaveAsync(connection with { Name = "Renamed" });
            Require((await restart.LoadAsync()).Single() is var renamed && renamed.SourceId == connection.SourceId && renamed.Name == "Renamed",
                "rename preserves playlist identity");
            await store.SaveAsync(connection with
            {
                Name = "Canonical edit", ServerAddress = " https://catalog.example.invalid/proxy/ ",
                Definition = connection.Definition with { ListPath = " api/tracks ", DurationUnit = " SECONDS " }
            });
            Require((await restart.LoadAsync()).Single().Definition == HttpJsonDefinition.Normalize(connection.Definition),
                "equivalent normalized mapping can be saved without breaking identity");
            await store.RemoveAsync(connection.SourceId);
            Require((await restart.LoadAsync()).Count == 0, "removal persists");
            await restart.RemoveAsync(connection.SourceId);
        }
        finally { Cleanup(directory); }
    }

    private static async Task AtomicCommitAsync()
    {
        string directory = DirectoryPath();
        try
        {
            var store = new HttpJsonConnectionStore(directory); var connection = Connection;
            await store.SaveAsync(connection);
            string index = Path.Combine(directory, "http-json-connections.json"), original = await File.ReadAllTextAsync(index);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await RejectAsync<OperationCanceledException>(() => store.SaveAsync(connection with { Name = "Canceled" }, cancellation.Token), "pre-canceled save");
                await RejectAsync<OperationCanceledException>(() => store.RemoveAsync(connection.SourceId, cancellation.Token), "pre-canceled removal");
            }
            Require(await File.ReadAllTextAsync(index) == original, "pre-cancellation leaves index intact");
#if DEBUG
            var boundary = typeof(HttpJsonConnectionStore).GetField("commitBoundary", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing atomic commit diagnostic seam.");
            using (var cancellation = new CancellationTokenSource())
            {
                boundary.SetValue(store, new Action<bool>(committed => { if (!committed) cancellation.Cancel(); }));
                await RejectAsync<OperationCanceledException>(() => store.SaveAsync(connection with { Name = "Canceled before replacement" }, cancellation.Token),
                    "cancellation after temporary flush preserves previous save");
                boundary.SetValue(store, null);
            }
            Require(await File.ReadAllTextAsync(index) == original && Directory.GetFiles(directory, "*.tmp").Length == 0,
                "canceled save keeps index and cleans temporary file");
            using (var cancellation = new CancellationTokenSource())
            {
                boundary.SetValue(store, new Action<bool>(committed => { if (!committed) cancellation.Cancel(); }));
                await RejectAsync<OperationCanceledException>(() => store.RemoveAsync(connection.SourceId, cancellation.Token),
                    "cancellation after temporary flush preserves previous removal");
                boundary.SetValue(store, null);
            }
            Require(await File.ReadAllTextAsync(index) == original, "canceled removal cannot erase index");
            using (var cancellation = new CancellationTokenSource())
            {
                boundary.SetValue(store, new Action<bool>(committed => { if (committed) cancellation.Cancel(); }));
                await store.SaveAsync(connection with { Name = "Committed despite cancellation" }, cancellation.Token);
                boundary.SetValue(store, null);
                Require(cancellation.IsCancellationRequested && (await store.LoadAsync()).Single().Name == "Committed despite cancellation",
                    "postcommit cancellation does not misreport save failure");
            }
#endif
            string committedIndex = await File.ReadAllTextAsync(index);
            using (var reader = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool rejected = false;
                try { await store.SaveAsync(connection with { Name = "Blocked replacement" }); }
                catch (IOException) { rejected = true; }
                catch (UnauthorizedAccessException) { rejected = true; }
                Require(rejected, "blocked atomic replacement rejected");
            }
            Require(await File.ReadAllTextAsync(index) == committedIndex && Directory.GetFiles(directory, "*.tmp").Length == 0,
                "atomic replacement failure retains previous catalog");
#if DEBUG
            using (var cancellation = new CancellationTokenSource())
            {
                boundary.SetValue(store, new Action<bool>(committed => { if (committed) cancellation.Cancel(); }));
                await store.RemoveAsync(connection.SourceId, cancellation.Token);
                boundary.SetValue(store, null);
                Require(cancellation.IsCancellationRequested && (await store.LoadAsync()).Count == 0,
                    "postcommit cancellation does not resurrect removed source");
            }
#endif
        }
        finally { Cleanup(directory); }
    }

    private static async Task LockAsync()
    {
        string directory = DirectoryPath();
        try
        {
            var store = new HttpJsonConnectionStore(directory); var connection = Connection;
            await store.SaveAsync(connection);
            string index = Path.Combine(directory, "http-json-connections.json"), original = await File.ReadAllTextAsync(index);
            using (var writer = new FileStream(index + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
                await RejectAsync<OperationCanceledException>(() => store.SaveAsync(connection with { Name = "Locked edit" }, cancellation.Token),
                    "exclusive writer lock remains cancellable");
            Require(await File.ReadAllTextAsync(index) == original, "waiting writer never changes index");
            var left = connection with { SourceId = Guid.NewGuid(), ServerAddress = "https://left.example.invalid" };
            var right = connection with { SourceId = Guid.NewGuid(), ServerAddress = "https://right.example.invalid" };
            var second = new HttpJsonConnectionStore(directory);
            await Task.WhenAll(store.SaveAsync(left), second.SaveAsync(right));
            Require((await second.LoadAsync()).Count == 3, "concurrent instances retain both writers");
        }
        finally { Cleanup(directory); }
    }

    private static async Task CorruptAsync()
    {
        string directory = DirectoryPath();
        try
        {
            var store = new HttpJsonConnectionStore(directory); var connection = Connection;
            await store.SaveAsync(connection);
            string index = Path.Combine(directory, "http-json-connections.json"), original = await File.ReadAllTextAsync(index);
            string normalizedJson = JsonSerializer.Serialize(HttpJsonConnectionStore.NormalizeConnection(connection));
            var invalidDocuments = new[] { "not JSON", "null", "{\"Version\":2,\"Connections\":[]}",
                "{\"Version\":1,\"Connections\":[null]}", "{\"Version\":1,\"version\":1,\"Connections\":[]}",
                "{\"Version\":1,\"Connections\":[],\"Authorization\":\"private\"}",
                "{\"Version\":1,\"Connections\":[" + normalizedJson + "," + normalizedJson + "]}",
                original.Replace("\"Definition\":{", "\"Definition\":{\"script\":\"ignored\",", StringComparison.Ordinal),
                original.Replace("\"Definition\":{", "\"Definition\":{\"authorization\":\"ignored\",", StringComparison.Ordinal) };
            foreach (string invalid in invalidDocuments)
            {
                await File.WriteAllTextAsync(index, invalid);
                bool rejected = false;
                try { await store.LoadAsync(); } catch (InvalidDataException) { rejected = true; } catch (JsonException) { rejected = true; }
                Require(rejected, "corrupt or unknown fields rejected");
                bool saveRejected = false;
                try { await store.SaveAsync(connection); } catch (InvalidDataException) { saveRejected = true; } catch (JsonException) { saveRejected = true; }
                Require(saveRejected && await File.ReadAllTextAsync(index) == invalid, "save never silently resets corrupt index");
            }
            await File.WriteAllTextAsync(index, original);
            Require((await store.LoadAsync()).Single().SourceId == connection.SourceId, "inspection leaves recoverable original identity");
        }
        finally { Cleanup(directory); }
    }

    private static async Task LimitsAsync()
    {
        string directory = DirectoryPath();
        try
        {
            var store = new HttpJsonConnectionStore(directory); var connection = Connection;
            var entries = Enumerable.Range(0, 100).Select(i => HttpJsonConnectionStore.NormalizeConnection(connection with
            { SourceId = Guid.NewGuid(), ServerAddress = $"https://catalog-{i}.example.invalid" })).ToArray();
            Directory.CreateDirectory(directory);
            string index = Path.Combine(directory, "http-json-connections.json");
            await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new { Version = 1, Connections = entries }));
            Require((await store.LoadAsync()).Count == 100, "connection limit accepts existing one hundred sources");
            string original = await File.ReadAllTextAsync(index);
            await RejectAsync<InvalidOperationException>(() => store.SaveAsync(connection), "connection limit rejects one hundred first source");
            Require(await File.ReadAllTextAsync(index) == original, "capacity rejection leaves index intact");
            await store.SaveAsync(entries[0] with { Name = "Renamed at capacity" });
            Require((await store.LoadAsync()).Count == 100, "editing existing source at capacity remains supported");
            await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new { Version = 1, Connections = entries.Append(connection).ToArray() }));
            await RejectAsync<InvalidDataException>(() => store.LoadAsync(), "oversized saved connection array rejected");
            using (var output = new FileStream(index, FileMode.Create, FileAccess.Write)) output.SetLength(1024 * 1024 + 1);
            await RejectAsync<InvalidDataException>(() => store.LoadAsync(), "oversized index rejected before parsing");
        }
        finally { Cleanup(directory); }
    }
}
