#if DEBUG
using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private async Task VerifyLocalPlaylistWindowAsync(Action<string, bool> verify, MusicTrack track)
    {
        var available = LocalPlaylistEntry.FromTrack(track);
        var offline = LocalPlaylistEntry.FromTrack(track with { SourceInstanceId = Guid.NewGuid(), Title = "离线夹具" });
        var saved = await playlistStore.LoadAsync();
        await playlistStore.SaveAsync(saved with { Playlists = [new(Guid.NewGuid(), "窗口夹具", [available, offline])] });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playlistDialogOpened = async dialog =>
        {
            try
            {
                var editor = (ScrollViewer)dialog.Content;
                var panel = (StackPanel)editor.Content;
                var picker = panel.Children.OfType<ComboBox>().Single();
                var name = panel.Children.OfType<TextBox>().Single();
                var list = panel.Children.OfType<ListView>().Single();
                var buttons = panel.Children.OfType<Button>().Concat(panel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>())).ToArray();
                async Task Click(string label)
                {
                    var button = buttons.Single(b => b.Content.ToString() == label);
                    if (!button.IsEnabled) throw new InvalidOperationException("Disabled fixture control");
                    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Task.Delay(100);
                    for (int i = 0; i < 100 && !editor.IsEnabled; i++) await Task.Delay(50);
                    if (!editor.IsEnabled) throw new TimeoutException();
                }
                verify("playlist_offline_retained", list.Items.Count == 2 && ((PlaylistRow)list.Items[1]).Text.Contains("离线"));
                list.SelectedIndex = 1;
                verify("playlist_offline_play_disabled", !dialog.IsPrimaryButtonEnabled);
                await Click("上移");
                verify("playlist_move_persisted", (await playlistStore.LoadAsync()).Playlists[0].Entries[0].Id == offline.Id);
                await Click("下移");
                await Click("移除歌曲");
                verify("playlist_remove_persisted", (await playlistStore.LoadAsync()).Playlists[0].Entries.Count == 1);
                name.Text = "已改名"; await Click("改名");
                verify("playlist_rename_persisted", (await playlistStore.LoadAsync()).Playlists[0].Name == "已改名");
                name.Text = "新建夹具"; await Click("新建");
                verify("playlist_create_selected", picker.SelectedItem is LocalPlaylist { Name: "新建夹具" } && list.Items.Count == 0);
                await Click("加入列表中所选歌曲");
                await Click("加入列表中所选歌曲");
                verify("playlist_add_deduplicated", (await playlistStore.LoadAsync()).Playlists[1].Entries.Count == 1);
                panel.Children.OfType<CheckBox>().Single().IsChecked = true;
                // Checkbox Click normally updates the button; invoke its automation toggle twice to fire UI events.
                var check = panel.Children.OfType<CheckBox>().Single();
                var toggle = (IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle);
                toggle.Toggle(); toggle.Toggle();
                await Click("删除歌单");
                verify("playlist_delete_only_catalog", (await playlistStore.LoadAsync()).Playlists.Count == 1 && PlaylistTrackAvailable(available));
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { dialog.Hide(); }
        };
        try { await ShowLocalPlaylistsAsync(); await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { playlistDialogOpened = null; }
        var final = (await playlistStore.LoadAsync()).Playlists.Single();
        await PlayLocalPlaylistAsync(final with { Entries = [available, offline] }, available);
        for (int i = 0; i < 80 && !mediaReady; i++) await Task.Delay(100);
        verify("playlist_play_skips_offline", queue.Count == 1 && queue.Current?.Reference == track.Reference && mediaReady);
        verify("playlist_play_lyrics", currentLyrics.Lines.Count == 3);
        await LoadArtworkAsync(NowCover);
        verify("playlist_play_cover", NowCover.Source is not null);
        Stop_Click(this, new RoutedEventArgs());
    }
}
#endif
