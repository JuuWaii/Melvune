#if DEBUG
using System.Text.Json;
using Melvune.App.ViewModels;

namespace Melvune.App.Services;
internal static class SettingsVerification
{
    public static async Task RunAsync(bool discoverDirectories = false)
    {
        var checks = new Dictionary<string, bool>(); var skipped = new List<string>();
        string stage = "session", error = "";
        IReadOnlyList<string>? authorizedDirectories = null;
        IReadOnlyList<string>? libraryPaths = null;
        IReadOnlyList<string>? sampleTrackPaths = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            using var connection = PlaybackVerification.CreateConnection();
            await connection.InitializeAsync();
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException(); checks[stage] = true;
            stage = "account_role"; bool admin = await api.IsAdministratorAsync(timeout.Token); checks[stage] = true;
            stage = "shared_library_list"; var libraries = await api.ListSharedLibrariesAsync(timeout.Token); checks[stage] = true;
            if (discoverDirectories) libraryPaths = libraries.Select(item => item.Path).ToArray();
            if (discoverDirectories)
            {
                stage = "indexed_file_paths";
                sampleTrackPaths = (await api.ListTrackFilesForVerificationAsync(1, 3, timeout.Token)).Select(item => item.Path).ToArray();
                checks[stage] = true;
            }
            stage = "background_task_list"; await api.ListBackgroundTasksAsync(timeout.Token); checks[stage] = true;
            if (libraries.Count > 0)
            {
                stage = "shared_library_detail";
                var detail = await api.GetSharedLibraryAsync(libraries[0].Library, timeout.Token);
                checks[stage] = detail.Library.Id == libraries[0].Library.Id;
                checks["metadata_preference_known"] = detail.MetadataPreference is "cloud_preferred" or "local_preferred" or "local_only";
            }
            else skipped.Add("no_shared_library_for_detail");
            if (admin)
            {
                stage = "server_settings"; await api.GetServerSettingsAsync(timeout.Token); checks[stage] = true;
                stage = "default_library_access"; await api.GetDefaultLibraryAccessAsync(timeout.Token); checks[stage] = true;
                stage = "authorized_directories"; var directories = await api.ListAuthorizedDirectoriesAsync(timeout.Token); checks[stage] = true;
                if (discoverDirectories) authorizedDirectories = directories;
                if (libraries.Count > 0)
                {
                    stage = "reconnect_preflight_only";
                    await api.CheckLibraryReconnectAsync(libraries[0], libraries[0].Path, timeout.Token); checks[stage] = true;
                }
            }
            else skipped.Add("administrator_only_settings");
            stage = checks.Values.All(value => value) ? skipped.Count == 0 ? "complete" : "complete_with_skips" : "contract_mismatch";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        // Paths are opt-in local diagnostic data; never credentials or normal logs.
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, discoverDirectories ? "library-directory-discovery.json" : "settings-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks, skipped, authorizedDirectories, libraryPaths, sampleTrackPaths }));
    }
}
#endif
