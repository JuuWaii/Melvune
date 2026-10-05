using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private int settingsCategoryVersion;
    private void SettingsCategory_Changed(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (SettingsConnectionSection is null || SettingsAppearanceSection is null || SettingsPlaybackSection is null || SettingsLibrariesSection is null) return;
        var sections = new[] { SettingsConnectionSection, SettingsAppearanceSection, SettingsPlaybackSection, SettingsLibrariesSection };
        var categories = new[] { ConnectionCategory, AppearanceCategory, PlaybackCategory, LibrariesCategory };
        int index = Array.IndexOf(categories, sender.SelectedItem);
        if (index < 0) return;
        settingsCategoryVersion++;
        for (int i = 0; i < sections.Length; i++) sections[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.ChangeView(null, 0, null, true);
        AnimateViewEntry(sections[index]);
    }
    private void SettingsMute_Click(object sender, RoutedEventArgs e)
    { music.Player.IsMuted = SettingsMute.IsChecked == true; UpdateVolumeControl(); }
    private void SettingsMode_Click(object sender, RoutedEventArgs e)
    { if (sender is Button button) QueueModeButton.Flyout.ShowAt(button); }
    private async void SettingsOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        int categoryVersion = settingsCategoryVersion, navigationVersion = contentNavigationVersion;
        await LoadDevicesAsync();
        if (!closed && categoryVersion == settingsCategoryVersion && navigationVersion == contentNavigationVersion
            && fullPageView is null && queuePage is null && SettingsPanel.Visibility == Visibility.Visible
            && SettingsPlaybackSection.Visibility == Visibility.Visible) OutputDeviceButton.Flyout.ShowAt(button);
    }
}
