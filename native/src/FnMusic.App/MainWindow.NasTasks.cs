using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private sealed record BackgroundTaskRow(NasBackgroundTask Task, string Name);
    private sealed record BackgroundTaskIdentity(Guid Source, string Id, string Type, long CreatedAt, string? LibraryId)
    {
        public static BackgroundTaskIdentity From(NasBackgroundTask task) =>
            new(task.SourceInstanceId, task.Id, task.Type, task.CreatedAt, task.LibraryId);
    }
    private static (StackPanel Panel, Action UpdateButtons) CreateNasTasksPanel(NasApiClient api,
        CancellationToken ct, Func<bool> isAdmin, Func<Func<Task>, bool, Task> run)
    {
        var panel = new StackPanel { Spacing = 10 };
        var picker = new ComboBox { Header = "后台任务", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        var details = new TextBlock { Text = "点击刷新读取服务器后台任务。", TextWrapping = TextWrapping.Wrap };
        var refresh = new Button { Content = "刷新后台任务" };
        var confirmation = new CheckBox { Content = "确认取消所选任务或删除已结束任务记录" };
        var cancel = new Button { Content = "取消所选任务" };
        var retry = new Button { Content = "重试所选任务" };
        var delete = new Button { Content = "删除任务记录" };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        BackgroundTaskIdentity? selectedIdentity = null;
        bool rendering = false;
        void UpdateButtons()
        {
            var task = (picker.SelectedItem as BackgroundTaskRow)?.Task;
            cancel.IsEnabled = isAdmin() && task?.CanCancel == true && confirmation.IsChecked == true;
            retry.IsEnabled = isAdmin() && task?.CanRetry == true;
            delete.IsEnabled = isAdmin() && task?.CanDelete == true && confirmation.IsChecked == true;
            confirmation.IsEnabled = isAdmin();
        }
        void Select()
        {
            confirmation.IsChecked = false;
            var task = (picker.SelectedItem as BackgroundTaskRow)?.Task;
            details.Text = task is null ? picker.Items.Count > 0 ? "请明确选择要查看或操作的后台任务。" : "当前未返回后台任务；这不代表之前的扫描已成功完成。" :
                $"{task.Name}\n类型：{TaskType(task.Type)}\n状态：{TaskState(task)}\n总数 {task.Total} · 成功 {task.SuccessCount} · 失败 {task.FailCount} · 已取消 {task.CanceledCount}";
            UpdateButtons();
        }
        void Render(IReadOnlyList<NasBackgroundTask> tasks)
        {
            var rows = tasks.Select(task => new BackgroundTaskRow(task, $"{TaskType(task.Type)} · {TaskState(task)} · {task.Name}")).ToArray();
            rendering = true;
            try
            {
                picker.ItemsSource = rows;
                picker.SelectedItem = rows.FirstOrDefault(row => BackgroundTaskIdentity.From(row.Task) == selectedIdentity);
            }
            finally { rendering = false; }
            Select();
            if (picker.SelectedItem is null && selectedIdentity is not null)
                details.Text = "所选任务已不存在或身份已变化，请刷新恢复原任务或明确重新选择；这不代表之前的扫描已成功完成。";
        }
        void Invalidate()
        {
            Render([]);
            details.Text = "后台任务读取或操作失败，已清除可操作状态。请刷新重试；这不代表之前的扫描已成功完成。";
            message.Text = "";
        }
        async Task Refresh()
        {
            try
            {
                var tasks = await api.ListBackgroundTasksAsync(ct); ct.ThrowIfCancellationRequested();
                Render(tasks); message.Text = "已读取服务器当前任务状态。";
            }
            catch { Invalidate(); throw; }
        }
        async Task Change(NasTaskAction action)
        {
            if (picker.SelectedItem is not BackgroundTaskRow row || !isAdmin()) return;
            if (action is NasTaskAction.Cancel or NasTaskAction.Delete && confirmation.IsChecked != true) return;
            await run(async () =>
            {
                message.Text = "";
                try
                {
                    var tasks = await api.ChangeBackgroundTaskAsync(row.Task, action, ct); ct.ThrowIfCancellationRequested();
                    Render(tasks);
                }
                catch { Invalidate(); throw; }
                message.Text = action == NasTaskAction.Delete ? "任务记录已删除，音乐文件不受影响。" : "请求已提交。请刷新查看最终状态；请求成功不代表任务完成。";
            }, true);
        }
        picker.SelectionChanged += (_, _) =>
        {
            if (rendering) return;
            selectedIdentity = picker.SelectedItem is BackgroundTaskRow row ? BackgroundTaskIdentity.From(row.Task) : null;
            Select();
        };
        confirmation.Click += (_, _) => UpdateButtons();
        refresh.Click += async (_, _) => await run(Refresh, false);
        cancel.Click += async (_, _) => await Change(NasTaskAction.Cancel);
        retry.Click += async (_, _) => await Change(NasTaskAction.Retry);
        delete.Click += async (_, _) => await Change(NasTaskAction.Delete);
        foreach (var child in new UIElement[] { picker, details, refresh, confirmation, cancel, retry, delete, message }) panel.Children.Add(child);
        panel.Children.Add(new TextBlock { Text = "任务来自当前飞牛服务器。扫描、刮削、歌词下载可能分别运行；总数为零时不推算完成百分比。刷新只读取状态，不会再次扫描。", TextWrapping = TextWrapping.Wrap });
        UpdateButtons();
        return (panel, UpdateButtons);
    }
    private static string TaskType(string type) => type switch
    {
        "fileScan" => "文件扫描", "cloudScrape" => "元数据刮削", "cloudScrapeCorrection" => "元数据修正",
        "lyricDownload" => "歌词下载", "download" => "下载", _ => type
    };
    private static string TaskState(NasBackgroundTask task) => task.Cancelling ? "正在取消" : !task.Done ? "进行中" :
        task.Canceled ? "已取消" : task.FailCount > 0 ? "已结束，有失败项" : task.CanceledCount > 0 ? "已结束，有取消项" : "已完成";
}
