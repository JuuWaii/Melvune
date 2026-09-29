#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using FnMusic.App.ViewModels;
using FnMusic.Core;
using FnMusic.Infrastructure;

namespace FnMusic.App.Services;

// Explicit account integration probe. Credentials arrive only through this process's environment.
// All configuration and encrypted sessions live in a unique fixture directory, never the user's profile.
internal static class AccountVerification
{
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException();
    public static async Task RunAsync()
    {
        var checks = new Dictionary<string, bool>(); string stage = "fixture", error = "";
        string directory = Path.Combine(AppContext.BaseDirectory, "account-fixture", Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            string server = Required("FNM_TEST_SERVER"), firstName = Required("FNM_TEST_ACCOUNT_A"), secondName = Required("FNM_TEST_ACCOUNT_B");
            string firstPassword = Required("FNM_TEST_PASSWORD_A"), secondPassword = Required("FNM_TEST_PASSWORD_B");
            Directory.CreateDirectory(directory);
            Guid firstSource;
            TrackReference firstTrack;
            using (var connection = new ConnectionViewModel(directory))
            {
                stage = "first_account_login";
                connection.ServerAddress = server; await connection.SaveConnectionAsync();
                connection.Username = firstName; await connection.LoginAsync(firstPassword);
                var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
                var page = await api.ListTracksAsync(1, 2, timeout.Token);
                checks[stage] = page.Total > 0 && page.Tracks.Count > 0;
                if (!checks[stage]) throw new InvalidOperationException();
                firstSource = api.SourceInstanceId; firstTrack = page.Tracks[0].Reference;
                checks["first_source_registered"] = connection.PlaybackRouter.CanPlay(firstTrack);
            }
            stage = "first_account_process_restart";
            checks[stage] = await VerifyRestartAsync(directory, firstSource, true, timeout.Token);
            using (var connection = new ConnectionViewModel(directory))
            {
                await connection.InitializeAsync();
                int changes = 0; connection.SessionChanging += () => changes++;
                stage = "switch_to_restricted_account";
                connection.Username = secondName; await connection.LoginAsync(secondPassword);
                var restricted = connection.AuthenticatedClient ?? throw new InvalidOperationException();
                var page = await restricted.ListTracksAsync(1, 2, timeout.Token);
                checks[stage] = restricted.SourceInstanceId != firstSource && page.Total == 0 && page.Tracks.Count == 0 && changes > 0;
                checks["previous_source_unregistered"] = !connection.PlaybackRouter.CanPlay(firstTrack);
                stage = "foreign_track_rejected_before_playback";
                try { await restricted.SetFavoriteAsync(firstTrack, true, timeout.Token); checks[stage] = false; }
                catch (MusicApiException ex) when (ex.Failure == MusicFailure.Rejected) { checks[stage] = true; }
                stage = "restricted_account_process_restart";
                checks[stage] = await VerifyRestartAsync(directory, restricted.SourceInstanceId, false, timeout.Token);
                stage = "switch_back_preserves_source_identity";
                connection.Username = firstName; await connection.LoginAsync(firstPassword);
                checks[stage] = connection.AuthenticatedClient?.SourceInstanceId == firstSource && connection.PlaybackRouter.CanPlay(firstTrack);
                // Reproduce an older connection file with no account binding while retaining only the fixture's encrypted session.
            }
            var store = new ConnectionSettingsStore(directory);
            var saved = await store.LoadAsync(timeout.Token) ?? throw new InvalidOperationException();
            await store.SaveAsync(saved with { SourceAccountKey = null }, timeout.Token);
            using (var legacy = new ConnectionViewModel(directory))
            {
                stage = "legacy_session_requires_reauthentication";
                await legacy.InitializeAsync();
                checks[stage] = legacy.AuthenticatedClient is null && legacy.Status.Contains("重新登录");
                stage = "legacy_reauthentication_keeps_identity";
                legacy.Username = firstName; await legacy.LoginAsync(firstPassword);
                checks[stage] = legacy.AuthenticatedClient?.SourceInstanceId == firstSource;
                await legacy.LogoutAsync();
            }
            using (var loggedOut = new ConnectionViewModel(directory))
            {
                stage = "logout_not_restored"; await loggedOut.InitializeAsync();
                checks[stage] = loggedOut.AuthenticatedClient is null;
            }
            stage = checks.Values.All(value => value) ? "complete" : "contract_mismatch";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally
        {
            // This exact random fixture is owned by the probe; no ancestor or user profile is removed.
            try
            {
                string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "account-fixture")) + Path.DirectorySeparatorChar;
                string resolved = Path.GetFullPath(directory);
                if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                    throw new InvalidOperationException();
                if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
                checks["fixture_removed"] = true;
            }
            catch (Exception) { checks["fixture_removed"] = false; stage = "cleanup_incomplete"; }
        }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "account-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks }));
    }

    private static async Task<bool> VerifyRestartAsync(string directory, Guid source, bool expectMusic, CancellationToken ct)
    {
        string report = Path.Combine(directory, "restore-verification.json");
        if (File.Exists(report)) File.Delete(report);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--verify-account-restore");
        foreach (string key in new[] { "FNM_TEST_SERVER", "FNM_TEST_ACCOUNT_A", "FNM_TEST_ACCOUNT_B", "FNM_TEST_PASSWORD_A", "FNM_TEST_PASSWORD_B" }) start.Environment.Remove(key);
        start.Environment["FNM_TEST_DIRECTORY"] = directory; start.Environment["FNM_TEST_SOURCE"] = source.ToString();
        start.Environment["FNM_TEST_HAS_MUSIC"] = expectMusic ? "true" : "false";
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        if (!File.Exists(report)) return false;
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report, ct));
        return json.RootElement.GetProperty("passed").GetBoolean();
    }

    public static async Task RestoreAsync()
    {
        string directory = Required("FNM_TEST_DIRECTORY"); bool passed = false;
        // Only accept an existing child of this executable's own fixture area.
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "account-fixture")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(directory)) return;
        try
        {
            using var connection = new ConnectionViewModel(directory);
            await connection.InitializeAsync();
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var page = await api.ListTracksAsync(1, 1, timeout.Token);
            bool expectMusic = Required("FNM_TEST_HAS_MUSIC") == "true";
            passed = api.SourceInstanceId == Guid.Parse(Required("FNM_TEST_SOURCE")) && (expectMusic ? page.Total > 0 && page.Tracks.Count == 1 : page.Total == 0 && page.Tracks.Count == 0);
        }
        catch (Exception) { }
        await File.WriteAllTextAsync(Path.Combine(directory, "restore-verification.json"), JsonSerializer.Serialize(new { passed }));
    }
}
#endif
