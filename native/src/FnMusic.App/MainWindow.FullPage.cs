using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private PlaylistPageView? fullPageView;
    private Control? fullPageReturnFocus;
    private bool fullPageNormalEnabled, fullPageLyricsEnabled;

    private bool MountFullPage(PlaylistPageView view)
    {
        if (closed || activePlaylistPage is { BlockNavigation: true } || !CloseFullPage() || !CloseQueuePage())
        { view.Dispose(); return false; }
        CloseTrackContextMenu();
        QueueModeButton.Flyout.Hide();
        OutputDeviceButton.Flyout.Hide();
        fullPageReturnFocus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        fullPageNormalEnabled = NormalContent.IsEnabled;
        fullPageLyricsEnabled = LyricsInteractionHost.IsEnabled;
        NormalContent.IsEnabled = LyricsInteractionHost.IsEnabled = false;
        fullPageView = view;
        FullPageHost.Content = view.Layout;
        FullPageHost.Visibility = Visibility.Visible;
        view.BackButton.Click += (_, _) => CloseFullPage();
        Root.UpdateLayout();
        view.Adapt(Root.ActualHeight);
        AnimateViewEntry(view.Layout);
        view.BackButton.Focus(FocusState.Programmatic);
        return true;
    }

    private bool CloseFullPage(bool force = false, bool restoreFocus = true)
    {
        if (fullPageView is not { } view) return true;
        if (!force && view.BlockNavigation)
        { view.Status.Text = "正在保存，请等待核对完成后返回。"; return false; }
        CloseTrackContextMenu();
        if (uiMotions.TryGetValue(view.Layout, out var motion)) motion.Finish(true);
        fullPageView = null;
        view.Dispose();
        FullPageHost.Content = null;
        FullPageHost.Visibility = Visibility.Collapsed;
        NormalContent.IsEnabled = fullPageNormalEnabled;
        LyricsInteractionHost.IsEnabled = fullPageLyricsEnabled;
        var focus = fullPageReturnFocus;
        fullPageReturnFocus = null;
        if (restoreFocus && !closed)
        {
            Root.UpdateLayout();
            if (focus is { IsLoaded: true, IsEnabled: true }) focus.Focus(FocusState.Programmatic);
            else if (LyricsPanel.Visibility == Visibility.Visible) NowPlayingBack.Focus(FocusState.Programmatic);
            else NowCoverButton.Focus(FocusState.Programmatic);
        }
        return true;
    }
}
