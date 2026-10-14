using FnMusic.App.ViewModels;
using Microsoft.UI.Xaml;

namespace FnMusic.App;

public sealed partial class MainWindow : Window
{
    public ConnectionViewModel ViewModel { get; } = new();
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FnMusic.ico"));
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = global::Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        SetTitleBar(AppTitleBar);
        ApplyMonochromeResources(WindowRoot);
        ApplyMonochromeResources(Root);
        SettingsCategories.SelectedItem = ConnectionCategory;
        ConfigureSongList(TrackList);
        QueueModeButton.Flyout.Opening += PlayerMenu_Opening;
        OutputDeviceButton.Flyout.Opening += PlayerOutput_Opening;
        Title = "飞牛音乐 · 0.2.0-rc.4";
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1360, 900));
        InitializePlayback();
        InitializePresentation();
        InitializeDesktopIntegration();
        PlayerBar.SizeChanged += (_, _) => { UpdateQueuePaneLayout(); UpdateLibraryHeaderLayout(); };
        ViewModel.SessionChanging += ResetLibrary;
        ViewModel.PropertyChanged += ConnectionCapabilitiesChanged;
        UpdateSourceCommands();
        Closed += (_, _) => { ViewModel.PropertyChanged -= ConnectionCapabilitiesChanged; CloseTrackContextMenu(); ClosePlayback(); ViewModel.Dispose(); };
    }
    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= Root_Loaded;
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains("--verify-httpjson-window") && IsSyntheticPreview)
        { await VerifyHttpJsonWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-catalog-revocation-window") && IsSyntheticPreview)
        { await VerifyCatalogRevocationWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-subsonic-window") && IsSyntheticPreview)
        { await RunSubsonicWindowVerificationAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-playback-resume-window") && IsSyntheticPreview)
        { await VerifyPlaybackResumeWindowAsync(Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--resume-phase=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "save"); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-emby-window") && IsSyntheticPreview)
        { await VerifyEmbyWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-navidrome-window") && IsSyntheticPreview)
        { await VerifyNavidromeWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-desktop-integration") && IsSyntheticPreview)
        { await VerifyDesktopIntegrationAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-nas-settings-permissions-window") && IsSyntheticPreview)
        { await VerifyNasSettingsPermissionsWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-nas-search-window") && IsSyntheticPreview)
        { await VerifyNasSearchWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-local-scale-window") && IsSyntheticPreview)
        { await VerifyLocalScaleWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-local-cache-window") && IsSyntheticPreview)
        { await VerifyLocalIndexWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-embedded-lyrics-window") && IsSyntheticPreview)
        { await VerifyEmbeddedLyricsWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-metadata-window") && IsSyntheticPreview)
        { await VerifyMetadataStandaloneWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-all-music-window") && IsSyntheticPreview)
        { await VerifyAllMusicStandaloneWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-artwork-window") && IsSyntheticPreview)
        { await VerifyArtworkWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-collection-window") && IsSyntheticPreview)
        { await VerifyCollectionWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-ui-motion") && IsSyntheticPreview)
        { await ShowSyntheticPreviewAsync(); await VerifyUiMotionWindowAsync(); Close(); return; }
        if (IsSyntheticPreview) { await ShowSyntheticPreviewAsync(); await OpenTrayInteractionPreviewAsync(); return; }
#endif
        await ViewModel.InitializeAsync();
        await ShowHomeAsync();
        await RestorePlaybackResumeAsync();
        await RestoreLocalIndexesAtStartupAsync();
        _ = RestoreNavidromeAtStartupAsync();
        _ = RestoreEmbyAtStartupAsync();
        _ = RestoreHttpJsonAtStartupAsync();
        await RefreshPlaylistNavigationAsync();
        await LoadDevicesAsync();
    }
    private async void Connect_Click(object sender, RoutedEventArgs e) => await ViewModel.SaveConnectionAsync();
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        string password = PasswordInput.Password;
        PasswordInput.Password = "";
        await ViewModel.LoginAsync(password);
    }
    private async void Logout_Click(object sender, RoutedEventArgs e) => await ViewModel.LogoutAsync();
}
