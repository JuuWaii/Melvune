#if DEBUG
using FnMusic.Core;
using FnMusic.Infrastructure;
using FnMusic.App.ViewModels;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private static async Task VerifyStartupRecoveryAsync(Action<string, bool> verify)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "startup-fixture");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "connection.json");
        if (File.Exists(file)) File.Delete(file);
        using var connection = new ConnectionViewModel(directory);
        await connection.InitializeAsync();
        verify("startup_empty_profile_ready_without_login", connection.IsReady && connection.AuthenticatedClient is null && connection.ServerAddress == "");
        await File.WriteAllTextAsync(file, "{invalid");
        await connection.InitializeAsync();
        verify("startup_corrupt_settings_preserved_and_retry_enabled", connection.IsReady && connection.Status.Contains("无法读取")
            && await File.ReadAllTextAsync(file) == "{invalid");
        var settings = new ConnectionSettings("https://nas.example.invalid/music/", Guid.NewGuid().ToString("N"), false);
        await new ConnectionSettingsStore(directory).SaveAsync(settings, CancellationToken.None);
        await connection.InitializeAsync();
        verify("startup_settings_retry_restores_without_network_or_session", connection.IsReady && connection.ServerAddress == settings.ServerAddress
            && connection.AuthenticatedClient is null && (await new ConnectionSettingsStore(directory).LoadAsync(CancellationToken.None)) == settings);
        connection.ServerAddress = "invalid-address";
        await connection.SaveConnectionAsync();
        verify("startup_invalid_address_can_retry_without_replacing_saved_settings", connection.IsReady && connection.Status.Contains("地址格式")
            && (await new ConnectionSettingsStore(directory).LoadAsync(CancellationToken.None)) == settings);
        await connection.InitializeAsync();
        verify("startup_failure_then_restore_recovers", connection.IsReady && connection.ServerAddress == settings.ServerAddress);
        verify("startup_all_stores_share_isolated_data_directory", ThemeSettingsDirectory() == Services.AppDataDirectory.Resolve()
            && ThemeSettingsDirectory().StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }
}
#endif
