using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private int themedDialogCount;
    private bool themedDialogOpen => themedDialogCount > 0;
    private static void ApplyMonochromeResources(FrameworkElement element) =>
        element.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///MonochromeResources.xaml") });

    private async Task<ContentDialogResult> ShowThemedDialogAsync(ContentDialog dialog)
    {
        ApplyMonochromeResources(dialog);
        // ContentDialog lives in a popup tree, outside Root's inherited RequestedTheme.
        void UpdateTheme(FrameworkElement _, object args) => dialog.RequestedTheme = Root.ActualTheme;
        dialog.RequestedTheme = Root.ActualTheme;
        Root.ActualThemeChanged += UpdateTheme;
        FrameworkElement? surface = null;
        bool showing = true;
        using var motionLifetime = new CancellationTokenSource();
        void AnimateDialog(ContentDialog sender, ContentDialogOpenedEventArgs args)
            => _ = StartDialogMotionAsync(sender);
        async Task StartDialogMotionAsync(ContentDialog sender)
        {
            // The popup can reparent/retemplate after Opened. Start after that layout work.
            try
            {
                for (int frame = 0; frame < 4; frame++)
                {
                    await Task.Delay(16, motionLifetime.Token);
                    if (!showing || closed) return;
                    surface = FindDialogMotionSurface(sender);
                    if (surface is not { IsLoaded: true, Visibility: Visibility.Visible }) continue;
                    await AnimateUiAsync(surface, 20, 0, 250, fade: true, bounce: true);
                    return;
                }
            }
            catch (OperationCanceledException) when (motionLifetime.IsCancellationRequested) { }
        }
        dialog.Opened += AnimateDialog;
        themedDialogCount++;
        try { return await dialog.ShowAsync(); }
        finally
        {
            showing = false;
            motionLifetime.Cancel();
            if (surface is not null && uiMotions.TryGetValue(surface, out var motion)) motion.Finish(true);
            themedDialogCount--; Root.ActualThemeChanged -= UpdateTheme; dialog.Opened -= AnimateDialog;
        }
    }
    private static FrameworkElement? FindDialogMotionSurface(DependencyObject parent)
    {
        // WinUI's dialog surface includes its title, content and buttons; the smoke layer stays stationary.
        if (parent is Border { Name: "BackgroundElement" } surface) return surface;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindDialogMotionSurface(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
}
