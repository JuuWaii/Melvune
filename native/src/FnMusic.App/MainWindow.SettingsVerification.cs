#if DEBUG
using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private static void InvokeSettingsButton(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private async Task VerifyCategorizedSettingsWindowAsync(Action<string, bool> verify)
    {
        var audio = music.Player.Source; var current = queue.Current?.Reference;
        var savedMode = queue.Mode; double savedVolume = VolumeSlider.Value; bool savedMute = music.Player.IsMuted;
        int savedTheme = ThemePicker.SelectedIndex; var size = AppWindow.Size;
        try
        {
            InvokeSettingsButton(TitleBarSettings);
            for (int i = 0; i < 100 && SettingsPanel.Visibility != Visibility.Visible; i++) await Task.Delay(50);
            await Task.Delay(100); Root.UpdateLayout();
            var buttonOrigin = TitleBarSettings.TransformToVisual(WindowRoot).TransformPoint(new global::Windows.Foundation.Point());
            double scale = Root.XamlRoot.RasterizationScale;
            verify("settings_native_caption_height_matches_titlebar", AppWindow.TitleBar.PreferredHeightOption == global::Microsoft.UI.Windowing.TitleBarHeightOption.Tall
                && Math.Abs(AppWindow.TitleBar.Height / scale - AppTitleBar.ActualHeight) < 2);
            verify("settings_titlebar_button_precedes_caption_buttons", ExtendsContentIntoTitleBar && AppTitleBar.IsLoaded && TitleBarSettings.IsLoaded
                && TitleBarSettings.ActualWidth > 0 && buttonOrigin.Y >= 0 && buttonOrigin.Y + TitleBarSettings.ActualHeight <= AppTitleBar.ActualHeight + 1
                && buttonOrigin.X + TitleBarSettings.ActualWidth <= WindowRoot.ActualWidth - AppWindow.TitleBar.RightInset / scale);
            verify("settings_unified_entry_has_four_categories", SettingsPanel.Visibility == Visibility.Visible && SettingsCategories.Items.Count == 4
                && SettingsCategories.SelectedItem == ConnectionCategory && SettingsConnectionSection.Visibility == Visibility.Visible
                && FindSongVisual<ComboBox>(SidebarHost, box => ReferenceEquals(box, ThemePicker)) is null
                && FindSongVisual<Button>(SidebarHost, button => ReferenceEquals(button, RemoteSettingsNavigation)) is null);
            SettingsCategories.SelectedItem = AppearanceCategory; await Task.Delay(100); Root.UpdateLayout();
            verify("settings_appearance_contains_original_theme_picker", SettingsAppearanceSection.Visibility == Visibility.Visible
                && SettingsConnectionSection.Visibility == Visibility.Collapsed && ThemePicker.IsLoaded && ThemePicker.ActualWidth > 0);
            foreach (int theme in new[] { 1, 2 })
            {
                ThemePicker.SelectedIndex = theme; await Task.Delay(50);
                verify("settings_titlebar_theme_" + theme, AppTitleBar.ActualTheme == Root.ActualTheme
                    && (theme == 1 ? Root.ActualTheme == ElementTheme.Light : Root.ActualTheme == ElementTheme.Dark));
            }
            ThemePicker.SelectedIndex = savedTheme;
            SettingsCategories.SelectedItem = PlaybackCategory; await Task.Delay(100); Root.UpdateLayout();
            SettingsVolume.Value = 31; await Task.Delay(50);
            verify("settings_volume_uses_same_player_control", SettingsPlaybackSection.Visibility == Visibility.Visible && SettingsVolume.IsLoaded
                && VolumeSlider.Value == 31 && Math.Abs(music.Player.Volume - .31) < .001);
            music.Player.IsMuted = false; UpdateVolumeControl(); await Task.Delay(50);
            var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(SettingsMute).GetPattern(PatternInterface.Toggle);
            toggle.Toggle(); await Task.Delay(50);
            verify("settings_mute_updates_shared_speaker", music.Player.IsMuted && MuteToggle.IsChecked == true && SettingsMute.IsChecked == true);
            toggle.Toggle(); await Task.Delay(50);
            verify("settings_unmute_preserves_volume", !music.Player.IsMuted && MuteToggle.IsChecked == false && VolumeSlider.Value == 31);
            InvokeSettingsButton(SettingsModeButton); await Task.Delay(100);
            ((IToggleProvider)new ToggleMenuFlyoutItemAutomationPeer(RepeatOneModeItem).GetPattern(PatternInterface.Toggle)).Toggle();
            await Task.Delay(50); QueueModeButton.Flyout.Hide();
            verify("settings_mode_uses_same_queue_menu", queue.Mode == QueueMode.RepeatOne && SettingsModeSummary.Text == "单曲循环" && RepeatOneModeItem.IsChecked);
            InvokeSettingsButton(SettingsOutputButton);
            for (int i = 0; i < 100 && !OutputDevices.IsLoaded; i++) await Task.Delay(50);
            verify("settings_output_uses_original_device_picker", OutputDevices.IsLoaded && OutputDevices.Items.Count > 0 && ReferenceEquals(music.Player.Source, audio));
            OutputDeviceButton.Flyout.Hide();
            SettingsCategories.SelectedItem = LibrariesCategory; await Task.Delay(100); Root.UpdateLayout();
            verify("settings_libraries_share_existing_permission_guard", SettingsLibrariesSection.Visibility == Visibility.Visible && RemoteSettingsNavigation.IsLoaded
                && !RemoteSettingsNavigation.IsEnabled && TitleBarSettings.IsEnabled);
            var manage = FindSongVisual<Button>(SettingsLibrariesSection, button => button.Content.ToString() == "管理音乐库") ?? throw new InvalidOperationException();
            InvokeSettingsButton(manage);
            for (int i = 0; i < 100 && (fullPageView is null || !fullPageView.Editor.IsEnabled); i++) await Task.Delay(50);
            var page = fullPageView ?? throw new InvalidOperationException(); page.BlockNavigation = true;
            InvokeSettingsButton(TitleBarSettings); await Task.Delay(50);
            verify("settings_titlebar_cannot_abandon_management_write", ReferenceEquals(fullPageView, page) && !page.IsDisposed && page.Status.Text.Contains("保存"));
            page.BlockNavigation = false; CloseFullPage();
            verify("settings_library_return_keeps_category_and_audio", SettingsPanel.Visibility == Visibility.Visible && SettingsCategories.SelectedItem == LibrariesCategory
                && ReferenceEquals(music.Player.Source, audio) && queue.Current?.Reference == current);
            SettingsCategories.SelectedItem = AppearanceCategory; await Task.Delay(100);
            await ShowHomeAsync(); await ShowSettingsPanelAsync();
            verify("settings_generic_entry_keeps_selected_category", SettingsCategories.SelectedItem == AppearanceCategory && SettingsAppearanceSection.Visibility == Visibility.Visible);
            await ShowSettingsPanelAsync(ConnectionCategory);
            verify("settings_connection_shortcut_selects_connection", SettingsCategories.SelectedItem == ConnectionCategory && SettingsConnectionSection.Visibility == Visibility.Visible);
            SettingsCategories.SelectedItem = PlaybackCategory; await Task.Delay(100);
            InvokeSettingsButton(SettingsModeButton); await Task.Delay(100);
            await ShowHomeAsync(); await Task.Delay(50);
            verify("settings_navigation_closes_mode_popup", !RepeatOneModeItem.IsLoaded);
            await ShowSettingsPanelAsync(AppearanceCategory);
            await OpenNowPlayingAsync(); await CloseNowPlayingAsync();
            verify("settings_lyrics_return_keeps_category", SettingsPanel.Visibility == Visibility.Visible && SettingsCategories.SelectedItem == AppearanceCategory
                && SettingsAppearanceSection.Visibility == Visibility.Visible && ReferenceEquals(music.Player.Source, audio));
            AppWindow.Resize(new global::Windows.Graphics.SizeInt32(820, 560)); await Task.Delay(100); Root.UpdateLayout();
            verify("settings_compact_category_controls_visible", SettingsCategories.IsLoaded && ThemePicker.ActualWidth > 100 && TitleBarSettings.ActualWidth > 0
                && SettingsCategories.Items.All(item => item.ActualWidth > 0) && SettingsPanel.ViewportWidth > 400);
        }
        finally
        {
            QueueModeButton.Flyout.Hide(); OutputDeviceButton.Flyout.Hide(); CloseFullPage(force: true);
            ThemePicker.SelectedIndex = savedTheme; VolumeSlider.Value = savedVolume; music.Player.IsMuted = savedMute; UpdateVolumeControl();
            queue.Mode = savedMode; UpdateQueueModeControl(); SettingsCategories.SelectedItem = ConnectionCategory;
            AppWindow.Resize(size); await Task.Delay(100); Root.UpdateLayout();
        }
    }
}
#endif
