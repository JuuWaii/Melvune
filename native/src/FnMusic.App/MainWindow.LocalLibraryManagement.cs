using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private async void ManageLocalLibraries_Click(object sender, RoutedEventArgs e)
    {
        if (localImportBusy) return;
        localImportBusy = true;
        try
        {
            var catalog = await localLibraryStore.LoadAsync(localLifetime.Token);
            if (closed) return;
            var picker = new ComboBox { Header = "选择音乐库", DisplayMemberPath = "Name", ItemsSource = catalog.Libraries, HorizontalAlignment = HorizontalAlignment.Stretch };
            var name = new TextBox { Header = "库名称", MaxLength = 120 };
            var group = new TextBox { Header = "分组（可选）", MaxLength = 120 };
            var path = new TextBox { Header = "文件夹路径", MaxLength = 4096 };
            var confirm = new CheckBox { Content = "确认移除所选音乐库" };
            var panel = new StackPanel { Spacing = 12, MinWidth = 300 };
            foreach (var child in new UIElement[] { picker, name, group, path, confirm }) panel.Children.Add(child);
            panel.Children.Add(new TextBlock { Text = "修改路径可重新关联移动后的文件夹，保留来源和相对路径相同的歌曲引用。移除仅取消导入，不删除音乐文件或歌单条目。", TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "本地音乐库管理", Content = new ScrollViewer { Content = panel, MaxHeight = 500 }, PrimaryButtonText = "保存", SecondaryButtonText = "移除", CloseButtonText = "取消", IsSecondaryButtonEnabled = false };
            void Select()
            {
                var item = picker.SelectedItem as LocalLibrary;
                name.Text = item?.Name ?? ""; group.Text = item?.Group ?? ""; path.Text = item?.Directory ?? "";
                confirm.IsChecked = false; dialog.IsPrimaryButtonEnabled = item is not null; dialog.IsSecondaryButtonEnabled = false;
            }
            picker.SelectionChanged += (_, _) => Select();
            confirm.Click += (_, _) => dialog.IsSecondaryButtonEnabled = confirm.IsChecked == true && picker.SelectedItem is LocalLibrary;
            picker.SelectedItem = catalog.Libraries.FirstOrDefault(item => item.SourceId == selectedLocalLibrary) ?? catalog.Libraries.FirstOrDefault();
            Select();
            var result = await ShowThemedDialogAsync(dialog);
            if (closed || result == ContentDialogResult.None || picker.SelectedItem is not LocalLibrary selected) return;
            LocalMusicSource? rescanned = null;
            LocalLibraryCatalog next;
            if (result == ContentDialogResult.Secondary)
            {
                if (confirm.IsChecked != true) return;
                next = catalog with { Libraries = catalog.Libraries.Where(item => item.SourceId != selected.SourceId).ToArray() };
            }
            else
            {
                if (!Path.IsPathFullyQualified(path.Text.Trim())) throw new ArgumentException();
                var updated = selected with { Name = name.Text.Trim(), Group = group.Text.Trim(), Directory = LocalLibraryStore.NormalizePath(path.Text.Trim()) };
                if (!string.Equals(updated.Directory, LocalLibraryStore.NormalizePath(selected.Directory), StringComparison.OrdinalIgnoreCase))
                    rescanned = await ScanLocalAsync(updated.Settings, localLifetime.Token);
                next = catalog with { Libraries = catalog.Libraries.Select(item => item.SourceId == selected.SourceId ? updated : item).ToArray() };
            }
            var saved = await localLibraryStore.SaveAsync(next, localLifetime.Token);
            if (closed) return;
            ApplyLocalCatalog(saved);
            if (rescanned is not null) ReplaceLocalSource(rescanned);
            PlaybackStatus.Text = result == ContentDialogResult.Secondary ? "已移除音乐库，音乐文件与歌单引用保留。" : "音乐库设置已保存。";
            if (localView) await LoadPageAsync(1);
        }
        catch (OperationCanceledException) { }
        catch (LocalLibraryConflictException) { if (!closed) PlaybackStatus.Text = "音乐库已在其他窗口修改，请重新打开管理后重试。"; }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "音乐库设置未保存，请检查名称、目录或重复路径。原配置保留。"; }
        finally { localImportBusy = false; }
    }
}
