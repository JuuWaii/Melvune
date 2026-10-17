#if DEBUG
using System.Text.Json;
using Melvune.App.ViewModels;
using Melvune.Core;

namespace Melvune.App.Services;
internal static class SettingsWriteVerification
{
    public static async Task RunAsync()
    {
        var checks = new Dictionary<string, bool>(); string stage = "session", error = "";
        NasServerSettings? original = null;
        string temporary = "FN验证-" + Guid.NewGuid().ToString("N")[..16];
        bool attempted = false, restored = false;
        using var connection = new ConnectionViewModel(); using var observer = new ConnectionViewModel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await connection.InitializeAsync(); await observer.InitializeAsync();
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
            var other = observer.AuthenticatedClient ?? throw new InvalidOperationException();
            original = await api.GetServerSettingsAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(original.Name) || original.Name.Length > 32) throw new InvalidOperationException();
            checks[stage] = true;
            stage = "rename_and_readback"; attempted = true;
            var changed = await api.RenameServerAsync(original, temporary, timeout.Token);
            checks[stage] = changed.Name == temporary && changed.Language == original.Language;
            stage = "second_client_observes_settings";
            checks[stage] = await other.GetServerSettingsAsync(timeout.Token) == changed;
            stage = "stale_settings_write_rejected";
            try { await other.RenameServerAsync(original, original.Name, timeout.Token); throw new InvalidOperationException(); }
            catch (RemoteSettingsConflictException) { checks[stage] = true; }
            stage = "library_settings_same_value_write";
            var libraries = await api.ListSharedLibrariesAsync(timeout.Token);
            if (libraries.Count > 0)
            {
                var library = await api.GetSharedLibraryAsync(libraries[0].Library, timeout.Token);
                var same = await api.UpdateSharedLibraryAsync(library, library.AutoDownloadLyric, library.MetadataPreference, timeout.Token);
                checks[stage] = same == library;
            }
            stage = "default_access_same_value_write";
            var access = await api.GetDefaultLibraryAccessAsync(timeout.Token);
            var selectedLibraries = access.Mode == "partial" ? access.LibraryIds.Select(id => libraries.Single(item => item.Library.Id == id).Library).ToArray() : [];
            var confirmedAccess = await api.UpdateDefaultLibraryAccessAsync(access, access.Mode, selectedLibraries, timeout.Token);
            checks[stage] = confirmedAccess.Mode == access.Mode && confirmedAccess.LibraryIds.Order().SequenceEqual(access.LibraryIds.Order());
            stage = checks.Values.All(value => value) ? "complete" : "contract_mismatch";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally
        {
            if (attempted && original is not null && connection.AuthenticatedClient is { } api)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var current = await api.GetServerSettingsAsync(cleanup.Token);
                    if (current == original) restored = true;
                    else if (current.Name == temporary && current.Language == original.Language)
                        restored = await api.RenameServerAsync(current, original.Name, cleanup.Token) == original;
                }
                catch (Exception) { }
            }
            checks["original_server_settings_restored"] = restored;
            if (attempted && !restored) stage = "restore_incomplete";
        }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "settings-write-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks }));
    }
}
#endif
