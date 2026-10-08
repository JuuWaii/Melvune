using Microsoft.UI.Xaml;

namespace FnMusic.App;

public partial class App : Application
{
    private Window? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains("--verify-local-format-matrix"))
        { RunLocalFormatMatrixVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-embedded-lyrics"))
        { RunNativeEmbeddedLyricsVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-library-lifecycle"))
        { RunLibraryLifecycleVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-library-directory"))
        { RunLibraryDirectoryDiscovery(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-local-formats"))
        { RunLocalFormatVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-account-restore"))
        { RunAccountRestoreVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-accounts"))
        { RunAccountVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-favorites-write"))
        { RunFavoriteWriteVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-settings-write"))
        { RunSettingsWriteVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-settings"))
        { RunSettingsVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-playlist-write"))
        { RunPlaylistWriteVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-playlists"))
        { RunPlaylistVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-presentation"))
        { RunPresentationVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-favorites"))
        { RunFavoriteVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-collections"))
        { RunCollectionVerification(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-native-search"))
        { RunSearchVerification(); return; }
        if (Environment.GetCommandLineArgs().Any(a => a is "--verify-native-playback" or "--verify-synthetic-playback"))
        {
            RunPlaybackVerification();
            return;
        }
#endif
        window = new MainWindow();
        window.Activate();
    }
#if DEBUG
    private async void RunLocalFormatMatrixVerification()
    { await Services.LocalFormatMatrixVerification.RunAsync(); Exit(); }
    private async void RunNativeEmbeddedLyricsVerification()
    { await Services.NasEmbeddedLyricsVerification.RunAsync(); Exit(); }
    private async void RunLibraryLifecycleVerification()
    { await Services.LibraryLifecycleVerification.RunAsync(); Exit(); }
    private async void RunLibraryDirectoryDiscovery()
    { await Services.SettingsVerification.RunAsync(discoverDirectories: true); Exit(); }
    private async void RunLocalFormatVerification()
    { await Services.LocalFormatVerification.RunAsync(); Exit(); }
    private async void RunAccountRestoreVerification()
    { await Services.AccountVerification.RestoreAsync(); Exit(); }
    private async void RunAccountVerification()
    { await Services.AccountVerification.RunAsync(); Exit(); }
    private async void RunFavoriteWriteVerification()
    { await Services.FavoriteWriteVerification.RunAsync(); Exit(); }
    private async void RunSettingsWriteVerification()
    { await Services.SettingsWriteVerification.RunAsync(); Exit(); }
    private async void RunSettingsVerification()
    { await Services.SettingsVerification.RunAsync(); Exit(); }
    private async void RunPlaylistWriteVerification()
    { await Services.PlaylistWriteVerification.RunAsync(); Exit(); }
    private async void RunPlaylistVerification()
    { await Services.PlaylistVerification.RunAsync(); Exit(); }
    private async void RunPresentationVerification()
    { await Services.PresentationVerification.RunAsync(); Exit(); }
    private async void RunFavoriteVerification()
    { await Services.FavoriteVerification.RunAsync(); Exit(); }
    private async void RunCollectionVerification()
    { await Services.CollectionVerification.RunAsync(); Exit(); }
    private async void RunSearchVerification()
    { await Services.SearchVerification.RunAsync(); Exit(); }
    private async void RunPlaybackVerification()
    {
        await Services.PlaybackVerification.RunAsync(Environment.GetCommandLineArgs().Contains("--verify-synthetic-playback"));
        Exit();
    }
#endif
}
