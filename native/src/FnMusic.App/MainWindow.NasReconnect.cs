using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private static (StackPanel Panel, Action RefreshSelection, Action UpdateButtons) CreateNasReconnectPanel(NasApiClient api, CancellationToken ct,
        Func<bool> isAdmin, Func<NasSharedLibrary?> selection, Func<Func<Task>, Task> run, Func<string, Task> refreshSettings)
    {
        NasLibraryReconnectCheck? checkedPath = null;
        var panel = new StackPanel { Spacing = 10 };
        var library = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var path = new TextBox { Header = "重新关联到 NAS 目录", MaxLength = 4096, PlaceholderText = "填写移动后的 NAS 目录路径" };
        var check = new Button { Content = "检查目录匹配" };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var confirmation = new CheckBox { Content = new TextBlock { Text = "确认使用未完全匹配的目录，接受索引重新匹配可能影响原歌曲引用", TextWrapping = TextWrapping.Wrap }, Visibility = Visibility.Collapsed };
        var reconnect = new Button { Content = "重新关联素材库" };
        void Update()
        {
            bool available = isAdmin() && selection() is not null;
            path.IsEnabled = available; check.IsEnabled = available && !string.IsNullOrWhiteSpace(path.Text);
            confirmation.IsEnabled = available;
            reconnect.IsEnabled = available && checkedPath is not null && (!checkedPath.NeedsConfirmation || confirmation.IsChecked == true);
        }
        void Invalidate()
        {
            checkedPath = null; result.Text = "请先检查目录，再确认重关联。";
            confirmation.IsChecked = false; confirmation.Visibility = Visibility.Collapsed; Update();
        }
        void RefreshSelection()
        {
            var selected = selection();
            library.Text = selected is null ? "请先在素材库页选择要重新关联的库。" : $"当前素材库：{selected.Library.Name}\n原目录：{selected.Path}";
            Invalidate();
        }
        path.TextChanged += (_, _) => Invalidate();
        confirmation.Click += (_, _) => Update();
        check.Click += async (_, _) =>
        {
            if (selection() is not { } original || !isAdmin()) return;
            string target = path.Text.Trim(); Invalidate();
            await run(async () =>
            {
                checkedPath = await api.CheckLibraryReconnectAsync(original, target, ct); ct.ThrowIfCancellationRequested();
                result.Text = checkedPath.NeedsConfirmation ? "服务器未确认目录完全匹配，或要求额外确认。请核对目标目录；继续可能重新匹配索引。" : "服务器已接受目录匹配，可以重新关联。";
                confirmation.Visibility = checkedPath.NeedsConfirmation ? Visibility.Visible : Visibility.Collapsed; Update();
            });
        };
        reconnect.Click += async (_, _) =>
        {
            if (checkedPath is not { } current || !isAdmin()) return;
            bool confirmed = confirmation.IsChecked == true;
            Invalidate();
            await run(async () =>
            {
                var changed = await api.ReconnectLibraryAsync(current, confirmed, ct); ct.ThrowIfCancellationRequested();
                await refreshSettings(changed.Library.Library.Id);
                result.Text = changed.ScanStarted ? "目录重关联已回读确认，服务器已启动扫描；请到后台任务页查看进度。" : "目录重关联已回读确认；服务器未报告启动扫描。";
            });
        };
        foreach (var child in new UIElement[] { library, path, check, result, confirmation, reconnect }) panel.Children.Add(child);
        panel.Children.Add(new TextBlock { Text = "在素材库页选择要操作的库。本页保留素材库身份和现有歌词/元数据选项；目录必须属于 NAS 并已授权。文件索引能否保留取决于服务器匹配结果，客户端不会声称所有歌曲引用都已恢复。", TextWrapping = TextWrapping.Wrap });
        RefreshSelection();
        return (panel, RefreshSelection, Update);
    }
}
