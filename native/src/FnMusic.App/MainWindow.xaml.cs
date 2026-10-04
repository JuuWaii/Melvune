using FnMusic.App.ViewModels;
using Microsoft.UI.Xaml;

namespace FnMusic.App;

public sealed partial class MainWindow : Window
{
    public ConnectionViewModel ViewModel { get; } = new();
    public MainWindow()
    {
        InitializeComponent();
        ApplyMonochromeResources(Root);
        Title = "飞牛音乐 · 原生预览";
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1360, 900));
        InitializePlayback();
        InitializePresentation();
        PlayerBar.SizeChanged += (_, _) => UpdateQueuePaneLayout();
        ViewModel.SessionChanging += ResetLibrary;
        ViewModel.PropertyChanged += ConnectionCapabilitiesChanged;
        UpdateSourceCommands();
        Closed += (_, _) => { ViewModel.PropertyChanged -= ConnectionCapabilitiesChanged; CloseTrackContextMenu(); ClosePlayback(); ViewModel.Dispose(); };
    }
    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= Root_Loaded;
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains("--verify-collection-window") && IsSyntheticPreview)
        { await VerifyCollectionWindowAsync(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-ui-motion") && IsSyntheticPreview)
        { await ShowSyntheticPreviewAsync(); await VerifyUiMotionWindowAsync(); Close(); return; }
        if (IsSyntheticPreview) { await ShowSyntheticPreviewAsync(); return; }
#endif
        await ViewModel.InitializeAsync();
        await ShowHomeAsync();
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
