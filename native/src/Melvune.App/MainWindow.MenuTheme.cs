using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private Style CreatePopupStyle(Type presenterType)
    {
        var style = new Style { TargetType = presenterType };
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, Root.ActualTheme));
        // Popup acrylic uses the window's system theme even when Root has an explicit theme.
        // Keep the native presenter/template, but provide an opaque matching surface.
        if (!new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            bool light = Root.ActualTheme == ElementTheme.Light;
            style.Setters.Add(new Setter(Control.BackgroundProperty, PopupBrush(light ? (byte)250 : (byte)44)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, PopupBrush(light ? (byte)32 : (byte)255)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, PopupBrush(light ? (byte)210 : (byte)70)));
        }
        return style;
    }

    private static SolidColorBrush PopupBrush(byte value) => new(global::Windows.UI.Color.FromArgb(255, value, value, value));

    private void ApplyMenuTheme(MenuFlyout menu)
    {
        menu.MenuFlyoutPresenterStyle = CreatePopupStyle(typeof(MenuFlyoutPresenter));
        bool highContrast = new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        void ApplyItem(MenuFlyoutItemBase item)
        {
            item.RequestedTheme = Root.ActualTheme;
            if (!item.Resources.MergedDictionaries.Any(dictionary => dictionary.Source?.OriginalString == "ms-appx:///MonochromeResources.xaml"))
                ApplyMonochromeResources(item);
            if (!highContrast)
            {
                bool light = Root.ActualTheme == ElementTheme.Light;
                item.Resources["MenuFlyoutPresenterBackground"] = PopupBrush(light ? (byte)250 : (byte)44);
                item.Resources["MenuFlyoutPresenterBorderBrush"] = PopupBrush(light ? (byte)210 : (byte)70);
            }
            if (item is MenuFlyoutSubItem sub) foreach (var child in sub.Items) ApplyItem(child);
        }
        foreach (var item in menu.Items) ApplyItem(item);
    }

    private void PlayerMenu_Opening(object? sender, object args)
    { if (sender is MenuFlyout menu) ApplyMenuTheme(menu); }

    private void PlayerOutput_Opening(object? sender, object args)
    { if (sender is Flyout flyout) flyout.FlyoutPresenterStyle = CreatePopupStyle(typeof(FlyoutPresenter)); _ = LoadDevicesAsync(); }
}
