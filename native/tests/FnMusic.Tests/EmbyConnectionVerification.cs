using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;

internal static class EmbyConnectionVerification
{
    public static async Task RunAsync()
    {
        Normalize();
        await RoundtripAsync();
        await RollbackAsync();
        await LockAsync();
        await CorruptAsync();
    }

    private static void Require(bool value, string context)
    { if (!value) throw new InvalidOperationException("Emby connection verification failed: " + context); }
    private static EmbyConnection Connection => new(Guid.NewGuid(), "Fixture Emby", "https://emby.example.invalid/proxy", "fixture-user", "fixture-user-id");
    private static EmbyCredentials Credentials => new("fixture-user-id", "fixture-access-token");
    private static string DirectoryPath() => Path.Combine(Path.GetTempPath(), "fnmusic-emby-store-" + Guid.NewGuid().ToString("N"));
    private static void Cleanup(string directory)
    {
        string target = Path.GetFullPath(directory);
        string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("fnmusic-emby-store-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid fixture cleanup target");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
    private static async Task RejectAsync<T>(Func<Task> action, string context) where T : Exception
    {
        bool rejected = false;
        try { await action(); } catch (T) { rejected = true; }
        Require(rejected, context);
    }

    private static void Normalize()
    {
        Require(EmbyConnectionStore.NormalizeServerAddress(" https://emby.example.invalid ") == "https://emby.example.invalid/emby/", "root API base");
        Require(EmbyConnectionStore.NormalizeServerAddress("http://emby") == "http://emby/emby/", "hostname matching API suffix is retained");
        Require(EmbyConnectionStore.NormalizeServerAddress("https://emby.example.invalid/proxy/") == "https://emby.example.invalid/proxy/emby/", "proxy subpath");
        Require(EmbyConnectionStore.NormalizeServerAddress("https://emby.example.invalid/proxy/emby/") == "https://emby.example.invalid/proxy/emby/", "API suffix once");
        Require(EmbyConnectionStore.NormalizeServerAddress("https://emby.example.invalid/proxy/EMBY") == "https://emby.example.invalid/proxy/emby/", "canonical API suffix");
        foreach (string invalid in new[] { "", "file:///private", string.Concat("https://", "user:secret@", "emby.example.invalid"), "https://emby.example.invalid/?api_key=secret", "https://emby.example.invalid/#private", "https://emby.example.invalid/\n", new string('x', 2049) })
        {
            bool rejected = false;
            try { EmbyConnectionStore.NormalizeServerAddress(invalid); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "unsafe server address rejected");
        }
        var connection = EmbyConnectionStore.NormalizeConnection(Connection with { Name = "  Named  ", Username = " fixture-user " });
        Require(connection.Name == "Named" && connection.Username == "fixture-user", "display fields trimmed");
    }

    private static async Task RoundtripAsync()
    {
        string directory = DirectoryPath();
        try
        {
            var vault = new Vault(); var store = new EmbyConnectionStore(directory, vault);
            var connection = Connection; var credentials = Credentials;
            Require((await store.LoadAsync()).Count == 0, "fresh store empty");
            await store.SaveAsync(connection, credentials);
            var restart = new EmbyConnectionStore(directory, vault);
            var loaded = (await restart.LoadAsync()).Single();
            Require(loaded.SourceId == connection.SourceId && loaded.UserId == connection.UserId && loaded.ServerAddress == "https://emby.example.invalid/proxy/emby/", "persistent source identity");
            Require(await restart.LoadCredentialsAsync(connection.SourceId) == credentials && vault.Values.Count == 1, "vault token survives reconstruction");
            string index = Path.Combine(directory, "emby-connections.json"); string original = await File.ReadAllTextAsync(index);
            Require(!original.Contains(credentials.AccessToken, StringComparison.Ordinal) && !original.Contains("AccessToken", StringComparison.Ordinal) && !original.Contains("Password", StringComparison.Ordinal), "public index has no token or password fields");
            await RejectAsync<InvalidOperationException>(() => store.SaveAsync(connection with { ServerAddress = "https://other.example.invalid" }, credentials), "server change needs new identity");
            await RejectAsync<InvalidOperationException>(() => store.SaveAsync(connection with { Username = "other-user" }, credentials), "username change needs new identity");
            await RejectAsync<InvalidOperationException>(() => store.SaveAsync(connection with { UserId = "other-id" }, new("other-id", "fixture-other-token")), "user ID change needs new identity");
            await RejectAsync<InvalidOperationException>(() => store.SaveAsync(connection with { SourceId = Guid.NewGuid() }, credentials), "duplicate account rejected");
            await RejectAsync<InvalidOperationException>(() => store.SaveAsync(connection with { SourceId = Guid.NewGuid(), Username = "renamed-user" }, credentials), "account aliases cannot duplicate same server user ID");
            await RejectAsync<InvalidDataException>(() => store.SaveAsync(connection, new("other-id", credentials.AccessToken)), "token user ID must match");
            foreach (string invalid in new[] { "", "  ", "token\n", " token", new string('x', 4097) })
                await RejectAsync<InvalidDataException>(() => store.SaveAsync(connection, new(connection.UserId, invalid)), "invalid access token rejected");
            foreach (var invalid in new[] { connection with { SourceId = Guid.Empty }, connection with { Name = "" }, connection with { Username = "" }, connection with { UserId = "" }, connection with { UserId = " id " } })
                await RejectAsync<ArgumentException>(() => store.SaveAsync(invalid, credentials), "invalid connection rejected");
            Require(await File.ReadAllTextAsync(index) == original && vault.Values.Count == 1, "invalid edits do not mutate store");
            await store.SaveAsync(connection with { Name = "Renamed" }, new(connection.UserId, "fixture-refreshed-token"));
            Require((await restart.LoadAsync()).Single().Name == "Renamed" && (await restart.LoadCredentialsAsync(connection.SourceId))!.AccessToken == "fixture-refreshed-token" && vault.Values.Count == 1, "rename and token refresh preserve identity and clean previous slot");
            Require(await restart.LoadCredentialsAsync(Guid.NewGuid()) is null, "unknown source has no credentials");
            await RejectAsync<ArgumentException>(() => store.LoadCredentialsAsync(Guid.Empty), "empty lookup identity rejected");
            await RejectAsync<ArgumentException>(() => store.RemoveAsync(Guid.Empty), "empty removal identity rejected");
            await store.RemoveAsync(connection.SourceId);
            Require((await restart.LoadAsync()).Count == 0 && vault.Values.Count == 0 && await restart.LoadCredentialsAsync(connection.SourceId) is null, "removal commits and clears credential slot");
            await restart.RemoveAsync(connection.SourceId);
        }
        finally { Cleanup(directory); }
    }

    private static async Task RollbackAsync()
    {
        string directory = DirectoryPath();
        try
        {
            var vault = new Vault(); var store = new EmbyConnectionStore(directory, vault);
            var connection = Connection; var credentials = Credentials;
            await store.SaveAsync(connection, credentials);
            string index = Path.Combine(directory, "emby-connections.json"), original = await File.ReadAllTextAsync(index);
            vault.FailSave = true;
            await RejectAsync<IOException>(() => store.SaveAsync(connection with { Name = "Failed edit" }, new(connection.UserId, "fixture-replacement-token")), "failure after vault writes propagates");
            vault.FailSave = false;
            Require(await File.ReadAllTextAsync(index) == original && await store.LoadCredentialsAsync(connection.SourceId) == credentials && vault.Values.Count == 1, "failed vault write rolls new slot back");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await RejectAsync<OperationCanceledException>(() => store.SaveAsync(connection with { Name = "Canceled before write" }, credentials, canceled.Token), "pre-canceled save");
                await RejectAsync<OperationCanceledException>(() => store.RemoveAsync(connection.SourceId, canceled.Token), "pre-canceled remove");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                vault.Saved = cancellation.Cancel;
                await RejectAsync<OperationCanceledException>(() => store.SaveAsync(connection with { Name = "Canceled replacement" }, credentials, cancellation.Token), "cancellation after vault write");
                vault.Saved = null;
            }
            Require(await File.ReadAllTextAsync(index) == original && await store.LoadCredentialsAsync(connection.SourceId) == credentials && vault.Values.Count == 1, "canceled index commit preserves previous slot");
            using (var reader = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool rejected = false;
                try { await store.SaveAsync(connection with { Name = "Blocked index replacement" }, new(connection.UserId, "fixture-blocked-token")); }
                catch (IOException) { rejected = true; }
                catch (UnauthorizedAccessException) { rejected = true; }
                Require(rejected, "blocked atomic replacement rejected");
            }
            Require(await File.ReadAllTextAsync(index) == original && await store.LoadCredentialsAsync(connection.SourceId) == credentials && vault.Values.Count == 1 && Directory.GetFiles(directory, "*.tmp").Length == 0, "index failure rolls credentials back and removes temporary file");
            vault.FailClear = true;
            await store.SaveAsync(connection with { Name = "Committed cleanup failure" }, new(connection.UserId, "fixture-committed-token"));
            Require((await store.LoadAsync()).Single().Name == "Committed cleanup failure" && (await store.LoadCredentialsAsync(connection.SourceId))!.AccessToken == "fixture-committed-token", "postcommit cleanup failure does not report save failure");
            await store.RemoveAsync(connection.SourceId);
            Require((await store.LoadAsync()).Count == 0 && await store.LoadCredentialsAsync(connection.SourceId) is null, "cleanup failure cannot resurrect removed account");
        }
        finally { Cleanup(directory); }
    }

    private static async Task LockAsync()
    {
        string directory = DirectoryPath();
        try
        {
            var vault = new Vault(); var store = new EmbyConnectionStore(directory, vault);
            var connection = Connection; await store.SaveAsync(connection, Credentials);
            string index = Path.Combine(directory, "emby-connections.json"), original = await File.ReadAllTextAsync(index);
            using (var writer = new FileStream(index + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
                await RejectAsync<OperationCanceledException>(() => store.SaveAsync(connection with { Name = "Locked edit" }, Credentials, cancellation.Token), "exclusive writer lock is cancellable");
            Require(await File.ReadAllTextAsync(index) == original && vault.Values.Count == 1, "lock cancellation writes neither index nor vault");
            var left = connection with { SourceId = Guid.NewGuid(), Username = "left-user", UserId = "left-id" };
            var right = connection with { SourceId = Guid.NewGuid(), Username = "right-user", UserId = "right-id" };
            var second = new EmbyConnectionStore(directory, vault);
            await Task.WhenAll(store.SaveAsync(left, new(left.UserId, "fixture-left-token")), second.SaveAsync(right, new(right.UserId, "fixture-right-token")));
            Require((await second.LoadAsync()).Count == 3 && vault.Values.Count == 3, "concurrent store writers preserve all sources");
        }
        finally { Cleanup(directory); }
    }

    private static async Task CorruptAsync()
    {
        string directory = DirectoryPath();
        try
        {
            var vault = new Vault(); var store = new EmbyConnectionStore(directory, vault);
            var connection = Connection; await store.SaveAsync(connection, Credentials);
            string index = Path.Combine(directory, "emby-connections.json"), original = await File.ReadAllTextAsync(index);
            foreach (string invalid in new[] { "not JSON", "null", "{\"Version\":2,\"Connections\":[]}", "{\"Version\":1,\"Connections\":[null]}" })
            {
                await File.WriteAllTextAsync(index, invalid);
                bool rejected = false;
                try { await store.LoadAsync(); } catch (InvalidDataException) { rejected = true; } catch (JsonException) { rejected = true; }
                Require(rejected && await File.ReadAllTextAsync(index) == invalid && vault.Values.Count == 1, "corrupt index rejected without mutation");
            }
            using (var output = new FileStream(index, FileMode.Create, FileAccess.Write)) output.SetLength(1024 * 1024 + 1);
            await RejectAsync<InvalidDataException>(() => store.LoadAsync(), "oversized index rejected before deserialization");
            await File.WriteAllTextAsync(index, original);
            string credentialKey = vault.Values.Keys.Single(); string saved = vault.Values[credentialKey];
            foreach (string invalid in new[] { "null", JsonSerializer.Serialize(new EmbyCredentials("wrong-user-id", "fixture-token")), new string('x', 32 * 1024 + 1) })
            {
                vault.Values[credentialKey] = invalid;
                await RejectAsync<InvalidDataException>(() => store.LoadCredentialsAsync(connection.SourceId), "invalid credential slot rejected");
            }
            vault.Values[credentialKey] = saved;
            Require(await store.LoadCredentialsAsync(connection.SourceId) == Credentials, "corrupt credential inspection retains recoverable prior slot");
        }
        finally { Cleanup(directory); }
    }

    private sealed class Vault : ISessionVault
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool FailSave { get; set; }
        public bool FailClear { get; set; }
        public Action? Saved { get; set; }
        public Task<string?> LoadAsync(string key, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Values.GetValueOrDefault(key)); }
        public Task SaveAsync(string key, string token, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Values[key] = token; if (FailSave) throw new IOException("Injected vault failure"); Saved?.Invoke(); return Task.CompletedTask; }
        public Task ClearAsync(string key, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (FailClear) throw new IOException("Injected cleanup failure"); Values.Remove(key); return Task.CompletedTask; }
    }
}
