using System.Text.Json;
using Melvune.Core;
using Melvune.Infrastructure;

internal static class BrandIdentityVerification
{
    // Independently calculated fixed digests prevent application and consumer renames
    // from silently selecting different account or credential identities.
    private const string AccountKey = "399aa7f046d64a5d87a1961397b45c360c2fe74c239a85c606fd528993816e2e";
    private const string NavidromeKey = "e36232e42770262d0422f26c46bc9fe935bdfd2632473db1d9988a420af973a9";
    private const string EmbyKey = "acd8dac0aa4a20a7adea7e75308791636e3e701a6c4496627106edf50b578fff";
    private static readonly Guid SourceId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    public static async Task RunAsync()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "melvune-brand-" + Guid.NewGuid().ToString("N")));
        try
        {
            Directory.CreateDirectory(root);
            VerifyProfileSelection(root);
            await VerifyIdentityAsync(root);
            await VerifyCredentialSlotsAsync(root);
            VerifyPipeNames();
        }
        finally
        {
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("melvune-brand-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid brand fixture cleanup target.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Require(bool value, string context)
    { if (!value) throw new InvalidOperationException("Brand identity verification failed: " + context); }

    private static void VerifyProfileSelection(string root)
    {
        string local = Path.Combine(root, "local");
        string current = Path.Combine(local, "Melvune.Native");
        string unrelated = Path.Combine(local, "another-player");
        Directory.CreateDirectory(unrelated);
        string sentinel = Path.Combine(unrelated, "existing-state.json");
        File.WriteAllText(sentinel, "preserve-existing-state");
        Require(ProductIdentity.ResolveDataDirectory(local) == current, "default profile belongs to Melvune");
        Require(!Directory.Exists(current), "resolving a profile does not create or migrate files");
        Directory.CreateDirectory(current);
        Require(ProductIdentity.ResolveDataDirectory(local) == current, "existing Melvune profile remains selected");
        string explicitProfile = Path.Combine(root, "portable", "Data");
        Require(ProductIdentity.ResolveDataDirectory(local, explicitProfile) == explicitProfile, "explicit portable or test profile wins");
        Require(!Directory.Exists(explicitProfile), "explicit selection does not create a profile");
        foreach (string invalid in new[] { "", "relative-profile" })
        {
            bool rejected = false;
            try { _ = ProductIdentity.ResolveDataDirectory(local, invalid); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "relative and empty explicit profiles are rejected");
        }
        Require(File.ReadAllText(sentinel) == "preserve-existing-state", "other application profiles remain untouched");
    }

    private static async Task VerifyIdentityAsync(string root)
    {
        var endpoint = ServerEndpoint.Parse("https://nas.example.invalid/music/");
        Require(MusicSourceRegistry.AccountKey(endpoint, " sample-account ") == AccountKey,
            "account digest uses the Melvune identity and normalized account");
        string directory = Path.Combine(root, "sources");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "sources.json");
        string original = JsonSerializer.Serialize(new Dictionary<string, Guid> { [AccountKey] = SourceId });
        await File.WriteAllTextAsync(file, original);
        var registry = new MusicSourceRegistry(directory);
        Require(await registry.GetOrCreateAsync(AccountKey, default) == SourceId, "saved Melvune source ID is stable");
        Require(await File.ReadAllTextAsync(file) == original, "loading identity does not replace the registry");
    }

    private static async Task VerifyCredentialSlotsAsync(string root)
    {
        string directory = Path.Combine(root, "connections");
        Directory.CreateDirectory(directory);
        const string navidromeDocument = """
            {"Version":1,"Connections":[{"Connection":{"SourceId":"11111111-1111-4111-8111-111111111111","Name":"Fixture source","ServerAddress":"https://navidrome.example.invalid/","Username":"fixture-user"},"CredentialId":"22222222-2222-4222-8222-222222222222"}]}
            """;
        const string embyDocument = """
            {"Version":1,"Connections":[{"Connection":{"SourceId":"11111111-1111-4111-8111-111111111111","Name":"Fixture source","ServerAddress":"https://emby.example.invalid/emby/","Username":"fixture-user","UserId":"fixture-user-id"},"CredentialId":"22222222-2222-4222-8222-222222222222"}]}
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, "navidrome-connections.json"), navidromeDocument);
        await File.WriteAllTextAsync(Path.Combine(directory, "emby-connections.json"), embyDocument);
        var navidromeCredentials = new NavidromeCredentials("fixture-user", new string('a', 32), "fixture-salt");
        var embyCredentials = new EmbyCredentials("fixture-user-id", "fixture-access-token");
        var vault = new FixtureVault(new Dictionary<string, string>
        {
            [NavidromeKey] = JsonSerializer.Serialize(navidromeCredentials),
            [EmbyKey] = JsonSerializer.Serialize(embyCredentials)
        });
        var navidrome = new NavidromeConnectionStore(directory, vault);
        var emby = new EmbyConnectionStore(directory, vault);
        Require((await navidrome.LoadAsync()).Single().SourceId == SourceId, "Navidrome source identity is retained");
        Require((await emby.LoadAsync()).Single().SourceId == SourceId, "Emby source identity is retained");
        Require(await navidrome.LoadCredentialsAsync(SourceId) == navidromeCredentials, "Navidrome uses its Melvune credential slot");
        Require(await emby.LoadCredentialsAsync(SourceId) == embyCredentials, "Emby uses its Melvune credential slot");
        Require(vault.Reads.SequenceEqual(new[] { NavidromeKey, EmbyKey }), "stores request the exact branded DPAPI keys");
        Require(await File.ReadAllTextAsync(Path.Combine(directory, "navidrome-connections.json")) == navidromeDocument
            && await File.ReadAllTextAsync(Path.Combine(directory, "emby-connections.json")) == embyDocument,
            "reading credentials does not rewrite indexes or rotate credentials");
    }

    private static void VerifyPipeNames()
    {
        string valid = "melvune-info-v1-11111111111141118111111111111111";
        Require(ProductIdentity.IsPlaybackPipeName(valid), "consumer recognizes the Melvune v1 transport");
        foreach (string? invalid in new[] { null, "", valid + "0", "other-info-v1-11111111111141118111111111111111",
            ProductIdentity.PlaybackPipePrefix + "not-a-guid", @"\\server\pipe\" + valid })
            Require(!ProductIdentity.IsPlaybackPipeName(invalid), "discovery rejects other brands malformed or remote pipe names");
    }

    private sealed class FixtureVault(Dictionary<string, string> values) : ISessionVault
    {
        public List<string> Reads { get; } = [];
        public Task<string?> LoadAsync(string connectionKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add(connectionKey);
            return Task.FromResult(values.GetValueOrDefault(connectionKey));
        }
        public Task SaveAsync(string connectionKey, string token, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Reading state must not create new credential slots.");
        public Task ClearAsync(string connectionKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Reading state must not remove credential slots.");
    }
}
