using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private bool handlingMainWindowClose, closeChoiceMadeThisSession;
    private ContentDialog? closeChoiceDialog;
    private sealed record MainWindowCloseChoice(bool ToTray, bool Remember);
#if DEBUG
    private Action<ContentDialog, CheckBox>? closeChoiceOpened;
    private Func<bool>? closeChoiceTrayAvailability;
#endif
    private bool MainWindowTrayAvailable
    {
        get
        {
#if DEBUG
            if (closeChoiceTrayAvailability is { } availability) return availability();
#endif
            return systemTray?.IsAvailable == true;
        }
    }

    private bool NeedsMainWindowCloseChoice => !explicitDesktopExit && !integrationSettings.CloseChoiceRemembered
        && !closeChoiceMadeThisSession && !BypassCloseChoiceForVerification();

    private bool BypassCloseChoiceForVerification()
    {
#if DEBUG
        // Only known unattended window suites bypass the first-close question. Ordinary synthetic
        // preview and all Release builds use the same first-close experience as installed clients.
        if (IsSyntheticPreview)
            return Environment.GetCommandLineArgs().Any(argument => argument is
                "--verify-queue-startup-window" or "--verify-queue-session-window" or "--verify-httpjson-window"
                or "--verify-catalog-revocation-window" or "--verify-subsonic-window" or "--verify-playback-resume-window"
                or "--verify-emby-window" or "--verify-navidrome-window" or "--verify-desktop-integration"
                or "--verify-nas-settings-permissions-window" or "--verify-nas-search-window" or "--verify-local-scale-window"
                or "--verify-local-cache-window" or "--verify-embedded-lyrics-window" or "--verify-metadata-window"
                or "--verify-all-music-window" or "--verify-artwork-window" or "--verify-collection-window" or "--verify-ui-motion" or "--verify-music-management-window");
#endif
        return false;
    }

    private async Task HandleMainWindowCloseAsync()
    {
        if (handlingMainWindowClose || closed || closingIntegration) return;
        handlingMainWindowClose = true;
        try
        {
            if (NeedsMainWindowCloseChoice)
            {
                // WinUI permits one ContentDialog per XamlRoot. Closing during an editor must not
                // silently discard that editor or accidentally exit after ShowAsync fails.
                if (themedDialogOpen || Root.XamlRoot is null)
                {
                    ShellStatus.Text = "请先完成或取消当前对话框，再关闭窗口。";
                    return;
                }
                var choice = await AskMainWindowCloseChoiceAsync();
                if (choice is null || closed || closingIntegration || !ApplyMainWindowCloseChoice(choice)) return;
            }
            if (TryHideMainWindowToTray()) return;
            // A remembered tray choice is not consent to exit when Explorer/the icon is unavailable.
            // Keep a recoverable window; the dialog's Exit button and tray Exit remain explicit exits.
            if (integrationSettings.CloseToTray && !explicitDesktopExit)
            {
                RestoreMainWindow();
                ShellStatus.Text = "系统托盘暂不可用，窗口已保留。可在设置中关闭“关闭窗口时收起到托盘”后退出。";
                return;
            }
            await RequestDesktopExitAsync();
        }
        catch (Exception)
        {
            if (!closed && !closingIntegration)
            {
                RestoreMainWindow();
                ShellStatus.Text = "关闭操作未完成，窗口已保留。请重试，或从托盘选择退出应用。";
            }
        }
        finally { handlingMainWindowClose = false; }
    }

    private async Task<MainWindowCloseChoice?> AskMainWindowCloseChoiceAsync()
    {
        var remember = new CheckBox { Content = "记住我的选择", IsChecked = true };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "关闭窗口后要如何处理？收起到托盘可继续播放，并可从托盘恢复窗口。",
            TextWrapping = TextWrapping.Wrap });
        content.Children.Add(remember);
        content.Children.Add(new TextBlock { Text = "不勾选时，本次运行采用此选择，下次启动后会再次询问。可随时在设置中更改关闭行为。",
            TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "关闭 Melvune",
            Content = content, PrimaryButtonText = "收起到托盘", SecondaryButtonText = "退出应用",
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        closeChoiceDialog = dialog;
#if DEBUG
        dialog.Opened += (_, _) => closeChoiceOpened?.Invoke(dialog, remember);
#endif
        try
        {
            var result = await ShowThemedDialogAsync(dialog);
            return result switch
            {
                ContentDialogResult.Primary => new(true, remember.IsChecked == true),
                ContentDialogResult.Secondary => new(false, remember.IsChecked == true),
                _ => null
            };
        }
        finally { if (ReferenceEquals(closeChoiceDialog, dialog)) closeChoiceDialog = null; }
    }

    private bool ApplyMainWindowCloseChoice(MainWindowCloseChoice choice)
    {
        if (closed || closingIntegration) return false;
        var previous = integrationSettings;
        if (choice.ToTray)
        {
            integrationSettings = integrationSettings with { TrayEnabled = true };
            ApplyDesktopShellSettings();
            if (!MainWindowTrayAvailable)
            {
                integrationSettings = previous;
                ApplyDesktopShellSettings();
                ShellStatus.Text = "系统托盘暂不可用，窗口已保留。请重试，或选择退出应用。";
                return false;
            }
        }
        integrationSettings = integrationSettings with { CloseToTray = choice.ToTray, CloseChoiceRemembered = choice.Remember };
        closeChoiceMadeThisSession = true;
        updatingIntegration = true;
        try { ShellTrayEnabled.IsOn = integrationSettings.TrayEnabled; ShellCloseToTray.IsOn = choice.ToTray; }
        finally { updatingIntegration = false; }
        SaveDesktopIntegration();
        ApplyDesktopShellSettings();
        return true;
    }
}
