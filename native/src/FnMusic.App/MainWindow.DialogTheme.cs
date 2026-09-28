using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private static void ApplyMonochromeResources(FrameworkElement element) =>
        element.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///MonochromeResources.xaml") });

    private async Task<ContentDialogResult> ShowThemedDialogAsync(ContentDialog dialog)
    {
        ApplyMonochromeResources(dialog);
        // ContentDialog lives in a popup tree, outside Root's inherited RequestedTheme.
        void UpdateTheme(FrameworkElement _, object args) => dialog.RequestedTheme = Root.ActualTheme;
        dialog.RequestedTheme = Root.ActualTheme;
        Root.ActualThemeChanged += UpdateTheme;
        try { return await dialog.ShowAsync(); }
        finally { Root.ActualThemeChanged -= UpdateTheme; }
    }
}
