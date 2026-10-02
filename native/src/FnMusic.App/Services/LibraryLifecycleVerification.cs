#if DEBUG
using System.Text.Json;
using FnMusic.App.ViewModels;
using FnMusic.Core;
using FnMusic.Infrastructure;

namespace FnMusic.App.Services;

// Explicit, opt-in NAS write probe. The caller supplies two copied directories
// beneath one unique fixture root. Existing libraries and original files are
// never edited. Reports contain checks/counters, never session material.
internal static class LibraryLifecycleVerification
{
    private sealed record Fixture(string OriginalPath, string ReplacementPath, int FileCount);
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("Library fixture invariant failed."); }
    private static string ValidateFixture(Fixture fixture)
    {
        string suffix = "/original";
        Require(fixture.OriginalPath.StartsWith('/') && fixture.OriginalPath.EndsWith(suffix, StringComparison.Ordinal));
        string root = fixture.OriginalPath[..^suffix.Length];
        string leaf = root[(root.LastIndexOf('/') + 1)..];
        Require(leaf.StartsWith("fnmusic-verify-", StringComparison.Ordinal) && Guid.TryParseExact(leaf[15..], "N", out _));
        Require(root.Split('/').All(segment => segment is not ("." or "..") && !segment.Contains('\\') && !segment.Contains('\0')));
        Require(fixture.ReplacementPath == root + "/replacement" && fixture.FileCount is >= 1 and <= 20);
        return root;
    }
    private static bool Within(string path, string directory) => path == directory || path.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal);
    private static bool Owned(NasSharedLibrary library, Fixture fixture, HashSet<string> before) =>
        !before.Contains(library.Library.Id) && (library.Path == fixture.OriginalPath || library.Path == fixture.ReplacementPath)
        && library.MetadataPreference == "local_only";

    public static async Task RunAsync()
    {
        var checks = new Dictionary<string, bool>(); var scans = new List<object>();
        string stage = "fixture", error = ""; Fixture? fixture = null;
        NasSharedLibrary? owned = null; bool attempted = false, cleaned = false;
        IReadOnlyList<NasSharedLibrary>? before = null;
        var existingTaskIds = new HashSet<(string, long)>();
        using var connection = new ConnectionViewModel(); using var observer = new ConnectionViewModel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        void Verify(string name, bool value) { checks[name] = value; Require(value); }
        try
        {
            string config = Environment.GetEnvironmentVariable("FNMUSIC_LIBRARY_FIXTURE_CONFIG") ?? throw new InvalidOperationException();
            Require(new FileInfo(config).Length <= 16384);
            fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(config, timeout.Token)) ?? throw new InvalidOperationException();
            string root = ValidateFixture(fixture);
            stage = "session";
            await connection.InitializeAsync(); await observer.InitializeAsync();
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
            var other = observer.AuthenticatedClient ?? throw new InvalidOperationException();
            Verify(stage, api.SourceInstanceId == other.SourceInstanceId && await api.IsAdministratorAsync(timeout.Token));
            stage = "authorized_fixture_scope";
            var directories = await api.ListAuthorizedDirectoriesAsync(timeout.Token);
            Verify(stage, directories.Any(directory => Within(root, directory)));
            before = await api.ListSharedLibrariesAsync(timeout.Token);
            Verify("no_existing_library_overlap", before.All(library => !Within(library.Path, root) && !Within(root, library.Path)));
            var baseline = (await api.ListBackgroundTasksAsync(timeout.Token)).Select(task => (task.Id, task.CreatedAt)).ToHashSet();
            existingTaskIds = baseline;
            stage = "create_and_readback"; attempted = true;
            owned = await api.CreateSharedLibraryAsync(fixture.OriginalPath, false, "local_only", timeout.Token);
            Verify(stage, Owned(owned, fixture, before.Select(item => item.Library.Id).ToHashSet()));
            Verify("second_client_observes_created_library", await other.GetSharedLibraryAsync(owned.Library, timeout.Token) == owned);
            stage = "creation_scan_completed";
            scans.Add(await WaitForScanAsync(api, owned.Library.Id, baseline, fixture.FileCount, timeout.Token)); Verify(stage, true);

            stage = "option_change_readback";
            var stale = owned;
            owned = await api.UpdateSharedLibraryAsync(owned, true, "local_only", timeout.Token);
            Verify(stage, owned.AutoDownloadLyric && await other.GetSharedLibraryAsync(owned.Library, timeout.Token) == owned);
            stage = "stale_option_write_rejected";
            bool rejected = false;
            try { await other.UpdateSharedLibraryAsync(stale, false, "local_only", timeout.Token); }
            catch (RemoteSettingsConflictException) { rejected = true; }
            Verify(stage, rejected);
            owned = await api.UpdateSharedLibraryAsync(owned, false, "local_only", timeout.Token);
            Verify("fixture_options_restored", !owned.AutoDownloadLyric);

            baseline = (await api.ListBackgroundTasksAsync(timeout.Token)).Select(task => (task.Id, task.CreatedAt)).ToHashSet();
            stage = "explicit_scan_submitted";
            await api.ScanSharedLibraryAsync(owned.Library, timeout.Token); Verify(stage, true);
            stage = "explicit_scan_completed";
            scans.Add(await WaitForScanAsync(api, owned.Library.Id, baseline, fixture.FileCount, timeout.Token)); Verify(stage, true);

            stage = "replacement_preflight_matches";
            var preflight = await api.CheckLibraryReconnectAsync(owned, fixture.ReplacementPath, timeout.Token);
            // Do not silently confirm unmatched copies or server confirmation requirements.
            Verify(stage, preflight.MatchAccepted && !preflight.NeedsConfirmation);
            baseline = (await api.ListBackgroundTasksAsync(timeout.Token)).Select(task => (task.Id, task.CreatedAt)).ToHashSet();
            stage = "reconnect_and_readback";
            var result = await api.ReconnectLibraryAsync(preflight, false, timeout.Token);
            Verify(stage, result.Library.Library.Id == owned.Library.Id && result.Library.Path == fixture.ReplacementPath
                && result.Library.MetadataPreference == owned.MetadataPreference && result.Library.AutoDownloadLyric == owned.AutoDownloadLyric);
            owned = result.Library;
            Verify("second_client_observes_reconnected_library", await other.GetSharedLibraryAsync(owned.Library, timeout.Token) == owned);
            stage = "reconnect_scan_completed";
            if (!result.ScanStarted) await api.ScanSharedLibraryAsync(owned.Library, timeout.Token);
            scans.Add(await WaitForScanAsync(api, owned.Library.Id, baseline, fixture.FileCount, timeout.Token)); Verify(stage, true);
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally
        {
            if (attempted && fixture is not null && before is not null && connection.AuthenticatedClient is { } api)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    var beforeIds = before.Select(item => item.Library.Id).ToHashSet();
                    var candidates = (await api.ListSharedLibrariesAsync(cleanup.Token)).Where(item => Owned(item, fixture, beforeIds)).ToArray();
                    // Recover an uncertain create response by its unique path, without
                    // inferring ownership from a name or touching a pre-existing ID.
                    Require(candidates.Length <= 1 && (owned is null || candidates.All(item => item.Library.Id == owned.Library.Id)));
                    foreach (var candidate in candidates)
                    {
                        var active = (await api.ListBackgroundTasksAsync(cleanup.Token)).Where(task => task.LibraryId == candidate.Library.Id
                            && !existingTaskIds.Contains((task.Id, task.CreatedAt)) && !task.Done).ToArray();
                        foreach (var task in active.Where(task => task.CanCancel)) await api.ChangeBackgroundTaskAsync(task, NasTaskAction.Cancel, cleanup.Token);
                        while ((await api.ListBackgroundTasksAsync(cleanup.Token)).Any(task => task.LibraryId == candidate.Library.Id && !task.Done))
                            await Task.Delay(TimeSpan.FromSeconds(1), cleanup.Token);
                        await api.DeleteSharedLibraryAsync(candidate, cleanup.Token);
                    }
                    var remaining = await api.ListSharedLibrariesAsync(cleanup.Token);
                    cleaned = remaining.Count == before.Count && before.All(original => remaining.Any(item => item == original));
                }
                catch (Exception) { }
                checks["fixture_library_removed_existing_libraries_unchanged"] = cleaned;
                if (!cleaned) stage = "cleanup_incomplete";
            }
        }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "library-lifecycle-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks, scans }));
    }

    private static async Task<object> WaitForScanAsync(NasApiClient api, string libraryId,
        HashSet<(string, long)> baseline, int files, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            var tasks = (await api.ListBackgroundTasksAsync(bounded.Token)).Where(task => task.LibraryId == libraryId
                && task.Type == "fileScan" && !baseline.Contains((task.Id, task.CreatedAt))).ToArray();
            Require(tasks.Length <= 1);
            if (tasks.SingleOrDefault() is { Done: true } task)
            {
                Require(!task.Cancelling && !task.Canceled && task.FailCount == 0 && task.CanceledCount == 0 && task.Total == files && task.SuccessCount == files);
                return new { task.Type, task.Total, task.SuccessCount, task.FailCount, task.CanceledCount, task.Done };
            }
            await Task.Delay(TimeSpan.FromSeconds(2), bounded.Token);
        }
    }
}
#endif
