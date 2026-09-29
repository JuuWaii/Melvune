#if DEBUG
using System.Text.Json;
using FnMusic.App.ViewModels;

namespace FnMusic.App.Services;
internal static class SettingsVerification
{
    public static async Task RunAsync()
    {
        var checks = new Dictionary<string, bool>(); var skipped = new List<string>();
        string stage = "session", error = "";
        using var connection = new ConnectionViewModel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await connection.InitializeAsync();
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException(); checks[stage] = true;
            stage = "account_role"; bool admin = await api.IsAdministratorAsync(timeout.Token); checks[stage] = true;
            stage = "shared_library_list"; var libraries = await api.ListSharedLibrariesAsync(timeout.Token); checks[stage] = true;
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
                stage = "authorized_directories"; await api.ListAuthorizedDirectoriesAsync(timeout.Token); checks[stage] = true;
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
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "settings-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks, skipped }));
    }
}
#endif
