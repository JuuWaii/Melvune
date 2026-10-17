#if DEBUG
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Melvune.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Melvune.App;
public sealed partial class MainWindow
{
    private async Task VerifyPlaylistGroupingLayoutAsync(Action<string, bool> check)
    {
        check("grouping_legacy_off_migrates_to_merged", !ReadPlaylistNavigationPreferences("{\"ShowNasPlaylists\":false}").SeparateNasPlaylists);
        check("grouping_legacy_on_migrates_to_separate", ReadPlaylistNavigationPreferences("{\"ShowNasPlaylists\":true}").SeparateNasPlaylists);
        check("grouping_new_value_overrides_legacy", !ReadPlaylistNavigationPreferences("{\"Version\":2,\"SeparateNasPlaylists\":false,\"ShowNasPlaylists\":true}").SeparateNasPlaylists);
        var local = LocalPlaylistNavigation.ItemsSource;
        var selected = sidebarSelectedPlaylistKey;
        var size = AppWindow.Size; var theme = Root.RequestedTheme;
        var remote = UnifiedPlaylistNavigation.Items.Cast<SidebarPlaylistRow>().Single(row => row.Value is NasPlaylist);
        try
        {
            sidebarSelectedPlaylistKey = remote.Key;
            foreach (bool separate in new[] { true, false })
            {
                SetNasPlaylistSidebarGrouping(separate); Root.UpdateLayout();
                check("grouping_both_modes_retain_remote_" + separate, UnifiedPlaylistNavigation.Items.Count == 3
                    && GroupedNasPlaylistNavigation.Items.Count == 1 && GroupedOtherPlaylistNavigation.Items.Count == 2);
                check("grouping_mode_visibility_" + separate, (SidebarGroupedPlaylists.Visibility == Visibility.Visible) == separate
                    && (UnifiedPlaylistNavigation.Visibility == Visibility.Visible) != separate && SeparateNasPlaylistsToggle.IsOn == separate);
                check("grouping_selection_keeps_provider_identity_" + separate, UnifiedPlaylistNavigation.SelectedItem is SidebarPlaylistRow a && a.Key == remote.Key
                    && GroupedNasPlaylistNavigation.SelectedItem is SidebarPlaylistRow b && b.Key == remote.Key && GroupedOtherPlaylistNavigation.SelectedItem is null);
            }
            foreach (var target in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                Root.RequestedTheme = target;
                foreach (bool separate in new[] { false, true })
                {
                    SetNasPlaylistSidebarGrouping(separate);
                    LocalPlaylistNavigation.ItemsSource = Enumerable.Range(0, 80).Select(i => new LocalPlaylist(Guid.NewGuid(), "长列表歌单 " + i, [])).ToArray();
                    RenderUnifiedPlaylistNavigation();
                    AppWindow.Resize(new global::Windows.Graphics.SizeInt32(820, 560)); await Task.Delay(100); Root.UpdateLayout();
                    var button = MusicManagementNavigation.TransformToVisual(SidebarHost).TransformPoint(new());
                    double gap = SidebarHost.ActualHeight - button.Y - MusicManagementNavigation.ActualHeight;
                    var footer = SidebarFooter.TransformToVisual(SidebarHost).TransformPoint(new());
                    var scroll = SidebarScroll.TransformToVisual(SidebarHost).TransformPoint(new());
                    check("sidebar_footer_fixed_bottom_" + target + separate, MusicManagementNavigation.IsLoaded && MusicManagementNavigation.ActualWidth > 100
                        && gap >= 15 && gap <= 17 && scroll.Y + SidebarScroll.ActualHeight <= footer.Y + 1);
                    SidebarScroll.ChangeView(null, double.MaxValue, null, true); await Task.Delay(80); Root.UpdateLayout();
                    var after = MusicManagementNavigation.TransformToVisual(SidebarHost).TransformPoint(new());
                    check("sidebar_long_list_scroll_does_not_move_footer_" + target + separate, Math.Abs(after.Y - button.Y) < .1
                        && SidebarScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled);
                    check("sidebar_group_watermark_below_text_" + target + separate, Root.ActualTheme == target
                        && GroupedNasPlaylistNavigation.Items.Count == 1 && UnifiedPlaylistNavigation.Items.Count == 81);
                }
            }
        }
        finally
        {
            Root.RequestedTheme = theme; AppWindow.Resize(size); LocalPlaylistNavigation.ItemsSource = local;
            sidebarSelectedPlaylistKey = selected; SetNasPlaylistSidebarGrouping(false);
            SidebarScroll.ChangeView(null, 0, null, true); await Task.Delay(100); Root.UpdateLayout();
        }
    }
    private async Task VerifyParallelNativeMetadataAsync(Action<string, bool> check)
    {
        string folder = Path.Combine(Services.AppDataDirectory.Resolve(), "parallel-native-waves");
        Directory.CreateDirectory(folder);
        using (var wave = Services.PlaybackVerification.CreateWave())
        {
            for (int i = 0; i < 4; i++)
            { wave.Position = 0; using var output = File.Create(Path.Combine(folder, "native-" + i + ".wav")); await wave.CopyToAsync(output); }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int probes = 0, onUi = 0;
        var source = await Melvune.Infrastructure.LocalMusicSource.ScanAsync(new(Guid.NewGuid(), folder), timeout.Token, async (path, token) =>
        {
            Interlocked.Increment(ref probes); if (DispatcherQueue.HasThreadAccess) Interlocked.Exchange(ref onUi, 1);
            return await Services.LocalMusicMetadataReader.ReadAsync(path, token);
        });
        check("parallel_actual_native_readers_complete_four_waves", probes == 4 && source.Tracks.Count == 4
            && source.Tracks.All(track => track.DurationSeconds >= 59 && track.DurationSeconds <= 61));
        check("parallel_actual_native_readers_stay_off_ui_thread", onUi == 0);
    }
    private async Task VerifyPlaylistWatermarkAsync(Action<string, bool> check)
    {
        var first = playlistWatermark ??= LoadPlaylistWatermarkAsync();
        var bitmap = await first;
        check("watermark_decode_shared_for_all_playlist_rows", ReferenceEquals(first, playlistWatermark));
        using var buffer = bitmap.PixelBuffer.AsStream();
        var bytes = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)]; await buffer.ReadExactlyAsync(bytes);
        bool neutral = true, translucent = false, visible = false;
        for (int i = 0; i < bytes.Length; i += 4)
        { neutral &= bytes[i] == bytes[i + 1] && bytes[i] == bytes[i + 2]; translucent |= bytes[i + 3] == 0; visible |= bytes[i + 3] > 0; }
        check("watermark_all_pixels_are_neutral_gray", neutral && translucent && visible);
        var file = await StorageFile.GetFileFromPathAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "FnOsMusic.png"));
        using var input = await file.OpenAsync(FileAccessMode.Read); var decoder = await BitmapDecoder.CreateAsync(input);
        var raw = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        check("watermark_preserves_official_size_and_exact_alpha", raw.Length == bytes.Length
            && Enumerable.Range(0, bytes.Length / 4).All(i => bytes[i * 4 + 3] == raw[i * 4 + 3]));
    }
}
#endif
