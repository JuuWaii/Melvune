#if DEBUG
using System.Net;
using System.Text;
using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private sealed class SettingsFixtureHandler : HttpMessageHandler
    {
        public bool Admin = true;
        public string ServerName = "服务器夹具", Mode = "none";
        public string[] AccessIds = [];
        public int Writes;
        public bool TaskExists = true, TaskDone, TaskCancelling;
        public readonly Dictionary<string, (string Path, bool Lyric, string Metadata)> Libraries = new() { ["initial"] = ("/music/initial", false, "cloud_preferred") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object Library(string id) => new { guid = id, name = id, path = Libraries[id].Path, autoDownloadLyric = Libraries[id].Lyric, metadataPreference = Libraries[id].Metadata };
            string path = request.RequestUri!.AbsolutePath; object? data = null;
            if (request.Method == HttpMethod.Post)
            {
                Writes++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root = body.RootElement;
                if (path.EndsWith("settings/server")) ServerName = root.GetProperty("name").GetString()!;
                if (path.EndsWith("settings/user"))
                {
                    var access = root.GetProperty("defaultSharedLibraryAccess"); Mode = access.GetProperty("mode").GetString()!;
                    AccessIds = access.GetProperty("guids").EnumerateArray().Select(item => item.GetString()!).ToArray();
                }
                if (path.EndsWith("shared-library/create") || path.EndsWith("shared-library/edit"))
                {
                    string id = path.EndsWith("/create") ? "created" : root.GetProperty("guid").GetString()!;
                    Libraries[id] = (root.GetProperty("path").GetString()!, root.GetProperty("autoDownloadLyric").GetBoolean(), root.GetProperty("metadataPreference").GetString()!);
                    data = Library(id);
                }
                if (path.EndsWith("shared-library/delete")) Libraries.Remove(root.GetProperty("guid").GetString()!);
                if (path.EndsWith("task/cancel")) TaskCancelling = true;
                if (path.EndsWith("task/retry")) { TaskDone = false; TaskCancelling = false; }
                if (path.EndsWith("task/delete")) TaskExists = false;
            }
            else if (path.EndsWith("user/me")) data = new { role = Admin ? "admin" : "member" };
            else if (path.EndsWith("settings/server")) data = new { name = ServerName, lang = "zh-CN" };
            else if (path.EndsWith("settings/user")) data = new { defaultSharedLibraryAccess = new { mode = Mode, sharedLibraries = AccessIds.Select(id => new { guid = id }) } };
            else if (path.EndsWith("shared-library/detail")) data = Library(Uri.UnescapeDataString(request.RequestUri.Query[6..]));
            else if (path.EndsWith("shared-library/list")) data = new { list = Libraries.Keys.Select(Library).ToArray() };
            else if (path.EndsWith("task/list")) data = new { list = TaskExists ? new[] { new { id = "task-1", type = "fileScan", name = "扫描夹具", total = 10, successCount = 3, failCount = 1, canceledCount = 0, done = TaskDone, cancelling = TaskCancelling, canceled = false, retryable = true, createdAt = 1 } } : [] };
            else data = new { list = new[] { new { path = "/music/new" } } };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = 0, data }), Encoding.UTF8, "application/json") };
        }
    }
    private async Task VerifyNasSettingsWindowAsync(Action<string, bool> verify)
    {
        var handler = new SettingsFixtureHandler();
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), handler, Guid.NewGuid());
        settingsTestClient = api;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settingsDialogOpened = async dialog =>
        {
            try
            {
                var editor = (ScrollViewer)dialog.Content; var panel = (StackPanel)editor.Content;
                var tabs = panel.Children.OfType<Pivot>().Single();
                StackPanel Tab(int index) => (StackPanel)((PivotItem)tabs.Items[index]).Content;
                var library = Tab(0); var server = Tab(1); var access = Tab(2); var tasks = Tab(3);
                var status = panel.Children.OfType<TextBlock>().Last();
                var buttons = panel.Children.OfType<Button>().Concat(new[] { library, server, access, tasks }.SelectMany(p => p.Children.OfType<Button>())).ToArray();
                async Task Click(string label)
                {
                    var button = buttons.Single(item => item.Content.ToString() == label);
                    if (!button.IsEnabled) throw new InvalidOperationException("Fixture control disabled: " + label);
                    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Task.Delay(100);
                    for (int i = 0; i < 100 && !editor.IsEnabled; i++) await Task.Delay(50);
                    if (!editor.IsEnabled) throw new TimeoutException();
                }
                library.Children.OfType<ComboBox>().Single(item => item.Header?.ToString() == "元数据优先策略").SelectedIndex = 2;
                library.Children.OfType<CheckBox>().First().IsChecked = true;
                await Click("保存素材库设置");
                verify("nas_settings_library_options", handler.Libraries["initial"].Metadata == "local_only" && handler.Libraries["initial"].Lyric);
                library.Children.OfType<TextBox>().Single().Text = "/music/new";
                await Task.Delay(100);
                await Click("创建素材库");
                verify("nas_settings_library_create", handler.Libraries.Count == 2);
                var confirmation = library.Children.OfType<CheckBox>().Last();
                ((IToggleProvider)new CheckBoxAutomationPeer(confirmation).GetPattern(PatternInterface.Toggle)).Toggle();
                await Click("移除素材库");
                verify("nas_settings_library_remove", handler.Libraries.Count == 1 && handler.Libraries.ContainsKey("initial"));
                await Click("扫描所选素材库");
                verify("nas_settings_scan_request_only", status.Text.Contains("请求已提交"));
                tabs.SelectedIndex = 1; await Task.Delay(150);
                var name = server.Children.OfType<TextBox>().Single(); name.Text = "客户端名称";
                await Click("保存服务器名称"); verify("nas_settings_server_save", handler.ServerName == "客户端名称");
                int writes = handler.Writes; handler.ServerName = "网页名称"; name.Text = "过期名称";
                await Click("保存服务器名称");
                verify("nas_settings_server_conflict", handler.Writes == writes && status.Text.Contains("变化"));
                await Click("刷新飞牛设置"); verify("nas_settings_refresh", name.Text == "网页名称");
                tabs.SelectedIndex = 2; await Task.Delay(150);
                access.Children.OfType<ComboBox>().Single().SelectedIndex = 1;
                var choices = access.Children.OfType<ListView>().Single(); choices.SelectedItems.Add(choices.Items[0]);
                await Click("保存默认访问范围");
                verify("nas_settings_default_access", handler.Mode == "partial" && handler.AccessIds.SequenceEqual(new[] { "initial" }));
                tabs.SelectedIndex = 3; await Task.Delay(150);
                await Click("刷新后台任务");
                var taskDetails = tasks.Children.OfType<TextBlock>().First();
                verify("nas_tasks_running_state", taskDetails.Text.Contains("进行中") && !buttons.Single(button => button.Content.ToString() == "删除任务记录").IsEnabled);
                ((IToggleProvider)new CheckBoxAutomationPeer(tasks.Children.OfType<CheckBox>().Single()).GetPattern(PatternInterface.Toggle)).Toggle();
                await Click("取消所选任务");
                verify("nas_tasks_cancel_pending", taskDetails.Text.Contains("正在取消") && !buttons.Single(button => button.Content.ToString() == "取消所选任务").IsEnabled);
                handler.TaskDone = true; handler.TaskCancelling = false; await Click("刷新后台任务");
                verify("nas_tasks_failure_visible", taskDetails.Text.Contains("有失败项"));
                await Click("重试所选任务");
                verify("nas_tasks_retry_request", !handler.TaskDone && taskDetails.Text.Contains("进行中"));
                handler.TaskDone = true; await Click("刷新后台任务");
                ((IToggleProvider)new CheckBoxAutomationPeer(tasks.Children.OfType<CheckBox>().Single()).GetPattern(PatternInterface.Toggle)).Toggle();
                await Click("删除任务记录");
                verify("nas_tasks_delete_record", !handler.TaskExists && taskDetails.Text.Contains("不代表之前的扫描已成功完成"));
                handler.Admin = false; await Click("刷新飞牛设置");
                verify("nas_settings_member_readonly", !buttons.Single(button => button.Content.ToString() == "保存默认访问范围").IsEnabled
                    && !buttons.Single(button => button.Content.ToString() == "保存素材库设置").IsEnabled);
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { dialog.Hide(); }
        };
        try { await ShowNasSettingsAsync(api); await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { settingsTestClient = null; settingsDialogOpened = null; }
    }
}
#endif
