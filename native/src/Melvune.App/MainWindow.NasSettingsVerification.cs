#if DEBUG
using System.Net;
using System.Text;
using System.Text.Json;
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Melvune.App;
public sealed partial class MainWindow
{
    private sealed class SettingsFixtureHandler : HttpMessageHandler
    {
        public bool Admin = true;
        public string ServerName = "服务器夹具", Mode = "none";
        public string[] AccessIds = [];
        public int Writes;
        public bool TaskExists = true, TaskDone, TaskCancelling, SecondTask;
        public long TaskCreatedAt = 1, TaskSuccess = 3;
        public string TaskType = "fileScan", TaskLibrary = "initial";
        public bool ReconnectMatch = true;
        public string? FailedRead;
        public Task? PausedRead;
        public TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Dictionary<string, (string Path, bool Lyric, string Metadata)> Libraries = new() { ["initial"] = ("/music/initial", false, "cloud_preferred") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object Library(string id) => new { guid = id, name = id, path = Libraries[id].Path, autoDownloadLyric = Libraries[id].Lyric, metadataPreference = Libraries[id].Metadata };
            string path = request.RequestUri!.AbsolutePath; object? data = null;
            if (request.Method == HttpMethod.Get && path.EndsWith("user/me") && PausedRead is { } paused)
            { ReadEntered.TrySetResult(); await paused; }
            if (request.Method == HttpMethod.Get && FailedRead is not null && path.EndsWith(FailedRead))
                return new(HttpStatusCode.ServiceUnavailable);
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
                if (path.EndsWith("shared-library/reconnect/check")) data = new { matchAccepted = ReconnectMatch, requiresConfirmation = !ReconnectMatch };
                if (path.EndsWith("shared-library/reconnect"))
                {
                    string id = root.GetProperty("guid").GetString()!; var old = Libraries[id];
                    Libraries[id] = (root.GetProperty("path").GetString()!, old.Lyric, old.Metadata);
                    data = new { reconnected = true, scanStarted = true };
                }
            }
            else if (path.EndsWith("user/me")) data = new { role = Admin ? "admin" : "member" };
            else if (path.EndsWith("settings/server")) data = new { name = ServerName, lang = "zh-CN" };
            else if (path.EndsWith("settings/user")) data = new { defaultSharedLibraryAccess = new { mode = Mode, sharedLibraries = AccessIds.Select(id => new { guid = id }) } };
            else if (path.EndsWith("shared-library/detail")) data = Library(Uri.UnescapeDataString(request.RequestUri.Query[6..]));
            else if (path.EndsWith("shared-library/list")) data = new { list = Libraries.Keys.Select(Library).ToArray() };
            else if (path.EndsWith("task/list"))
            {
                var list = new List<object>();
                if (TaskExists) list.Add(new { id = "task-1", type = TaskType, name = "扫描夹具", total = 10, successCount = TaskSuccess, failCount = 1, canceledCount = 0, done = TaskDone, cancelling = TaskCancelling, canceled = false, retryable = true, createdAt = TaskCreatedAt, ext = new { libraryGUID = TaskLibrary } });
                if (SecondTask) list.Add(new { id = "task-2", type = "fileScan", name = "另一个任务", total = 10, successCount = 2, failCount = 0, canceledCount = 0, done = false, cancelling = false, canceled = false, retryable = true, createdAt = 2, ext = new { libraryGUID = "initial" } });
                data = new { list };
            }
            else data = new { list = new[] { new { path = "/music/new" } } };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = 0, data }), Encoding.UTF8, "application/json") };
        }
    }
    private async Task VerifyNasSettingsWindowAsync(Action<string, bool> verify)
    {
        var handler = new SettingsFixtureHandler { FailedRead = "settings/server" };
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), handler, Guid.NewGuid());
        settingsTestClient = api;
        ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settingsPageOpened = async view =>
        {
            try
            {
                var editor = view.Editor; var panel = view.Fields;
                var tabs = panel.Children.OfType<Pivot>().Single();
                StackPanel Tab(int index) => (StackPanel)((PivotItem)tabs.Items[index]).Content;
                var library = Tab(0); var server = Tab(1); var access = Tab(2); var tasks = Tab(3); var reconnect = Tab(4);
                var status = view.Status;
                var buttonTabs = new Dictionary<Button, int>();
                var buttons = view.Actions.Children.OfType<Button>().ToList();
                for (int index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    var tabButtons = view.Actions.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>()).ToArray();
                    foreach (var button in tabButtons) { buttons.Add(button); buttonTabs.Add(button, index); }
                }
                tabs.SelectedIndex = 0;
                verify("nas_settings_full_page_no_dialog", ReferenceEquals(fullPageView, view) && FullPageHost.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                    && !NormalContent.IsEnabled && !themedDialogOpen && view.IsForm && Microsoft.UI.Xaml.Controls.Grid.GetRow(view.Editor) == 2);
                await VerifyFullPageLayoutAsync(verify, view, "nas_settings");
                async Task Click(string label)
                {
                    var button = buttons.Single(item => item.Content.ToString() == label);
                    if (buttonTabs.TryGetValue(button, out int index)) { tabs.SelectedIndex = index; await Task.Delay(50); }
                    if (!button.IsEnabled) throw new InvalidOperationException("Fixture control disabled: " + label);
                    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Task.Delay(100);
                    for (int i = 0; i < 100 && !editor.IsEnabled; i++) await Task.Delay(50);
                    if (!editor.IsEnabled) throw new TimeoutException();
                }
                verify("nas_settings_initial_partial_failure_opens", status.Text.Contains("服务器设置")
                    && !buttons.Single(button => button.Content.ToString() == "保存服务器名称").IsEnabled
                    && buttons.Single(button => button.Content.ToString() == "保存素材库设置").IsEnabled);
                handler.FailedRead = null; await Click("刷新飞牛设置");
                verify("nas_settings_partial_retry_recovers", buttons.Single(button => button.Content.ToString() == "保存服务器名称").IsEnabled
                    && !status.Text.Contains("读取失败"));
                library.Children.OfType<ComboBox>().Single(item => item.Header?.ToString() == "元数据优先策略").SelectedIndex = 2;
                library.Children.OfType<CheckBox>().First().IsChecked = true;
                await Click("保存素材库设置");
                verify("nas_settings_library_options", handler.Libraries["initial"].Metadata == "local_only" && handler.Libraries["initial"].Lyric);
                library.Children.OfType<TextBox>().Single().Text = "/music/new";
                await Task.Delay(100);
                await Click("创建素材库");
                verify("nas_settings_library_create", handler.Libraries.Count == 2);
                var picker = library.Children.OfType<ComboBox>().Single(item => item.Header?.ToString() == "素材库");
                var preference = library.Children.OfType<ComboBox>().Single(item => item.Header?.ToString() == "元数据优先策略");
                handler.Libraries["created"] = ("/music/new", false, "cloud_preferred");
                int libraryWrites = handler.Writes;
                await Click("保存素材库设置");
                verify("nas_settings_library_conflict_invalidates_snapshot", handler.Writes == libraryWrites && status.Text.Contains("变化")
                    && !buttons.Single(button => button.Content.ToString() == "保存素材库设置").IsEnabled);
                await Click("刷新飞牛设置");
                verify("nas_settings_library_conflict_refresh_preserves_selection", (picker.SelectedItem as SharedLibraryRow)?.Value.Library.Id == "created"
                    && preference.SelectedIndex == 0 && library.Children.OfType<CheckBox>().First().IsChecked == false);
                handler.FailedRead = "shared-library/list"; await Click("刷新飞牛设置");
                verify("nas_settings_library_failed_refresh_clears_editable_selection", picker.SelectedItem is null
                    && !buttons.Single(button => button.Content.ToString() == "保存素材库设置").IsEnabled);
                handler.FailedRead = null; await Click("刷新飞牛设置");
                verify("nas_settings_library_failed_refresh_recovers_same_selection", (picker.SelectedItem as SharedLibraryRow)?.Value.Library.Id == "created");
                var removedLibrary = handler.Libraries["created"];
                handler.Libraries.Remove("created"); await Click("刷新飞牛设置");
                verify("nas_settings_library_disappearance_does_not_select_another", picker.SelectedItem is null
                    && library.Children.OfType<TextBlock>().First().Text.Contains("已不存在")
                    && !buttons.Single(button => button.Content.ToString() == "扫描所选素材库").IsEnabled);
                handler.Libraries["created"] = removedLibrary; await Click("刷新飞牛设置");
                verify("nas_settings_library_return_recovers_selection", (picker.SelectedItem as SharedLibraryRow)?.Value.Library.Id == "created");
                var confirmation = library.Children.OfType<CheckBox>().Last();
                ((IToggleProvider)new CheckBoxAutomationPeer(confirmation).GetPattern(PatternInterface.Toggle)).Toggle();
                await Click("移除素材库");
                verify("nas_settings_library_remove", handler.Libraries.Count == 1 && handler.Libraries.ContainsKey("initial"));
                verify("nas_settings_library_delete_requires_explicit_selection", picker.SelectedItem is null
                    && !buttons.Single(button => button.Content.ToString() == "扫描所选素材库").IsEnabled);
                picker.SelectedIndex = 0; await Task.Delay(150);
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
                var taskPicker = tasks.Children.OfType<ComboBox>().Single();
                bool NoTaskActions() => taskPicker.SelectedItem is null && new[] { "取消所选任务", "重试所选任务", "删除任务记录" }
                    .All(label => !buttons.Single(button => button.Content.ToString() == label).IsEnabled);
                verify("nas_tasks_initial_requires_explicit_selection", NoTaskActions());
                taskPicker.SelectedIndex = 0;
                handler.SecondTask = true; handler.TaskSuccess = 4; await Click("刷新后台任务");
                verify("nas_tasks_progress_preserves_identity", (taskPicker.SelectedItem as BackgroundTaskRow)?.Task.SuccessCount == 4);
                handler.TaskExists = false; await Click("刷新后台任务");
                verify("nas_tasks_disappearance_does_not_select_other", NoTaskActions() && taskPicker.Items.Count == 1);
                handler.TaskExists = true; await Click("刷新后台任务");
                verify("nas_tasks_reappearance_restores_identity", (taskPicker.SelectedItem as BackgroundTaskRow)?.Task.Id == "task-1");
                handler.TaskCreatedAt = 9; await Click("刷新后台任务");
                verify("nas_tasks_reused_id_created_at_rejected", NoTaskActions());
                handler.TaskCreatedAt = 1; handler.TaskType = "lyricDownload"; await Click("刷新后台任务");
                verify("nas_tasks_reused_id_type_rejected", NoTaskActions());
                handler.TaskType = "fileScan"; handler.TaskLibrary = "created"; await Click("刷新后台任务");
                verify("nas_tasks_reused_id_library_rejected", NoTaskActions());
                handler.TaskLibrary = "initial"; await Click("刷新后台任务");
                verify("nas_tasks_original_identity_recovers", (taskPicker.SelectedItem as BackgroundTaskRow)?.Task.Id == "task-1");
                handler.FailedRead = "task/list"; await Click("刷新后台任务");
                verify("nas_tasks_failed_read_clears_snapshot", NoTaskActions() && taskPicker.Items.Count == 0);
                handler.FailedRead = null; await Click("刷新后台任务");
                verify("nas_tasks_failed_read_restores_identity", (taskPicker.SelectedItem as BackgroundTaskRow)?.Task.Id == "task-1");
                handler.TaskExists = false; await Click("刷新后台任务");
                taskPicker.SelectedIndex = 0;
                verify("nas_tasks_explicit_other_selection", (taskPicker.SelectedItem as BackgroundTaskRow)?.Task.Id == "task-2"
                    && BackgroundTaskIdentity.From(((BackgroundTaskRow)taskPicker.SelectedItem).Task) != BackgroundTaskIdentity.From(new NasBackgroundTask(Guid.NewGuid(), "task-2", "fileScan", "", 10, 2, 0, 0, false, false, false, true, 2) { LibraryId = "initial" }));
                handler.TaskExists = true; await Click("刷新后台任务");
                taskPicker.SelectedIndex = 0; handler.SecondTask = false; await Click("刷新后台任务");
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
                // A failed operation also invalidates the settings editor's snapshots.
                // Restore those explicitly before continuing with library mutations.
                await Click("刷新飞牛设置");
                tabs.SelectedIndex = 4; await Task.Delay(150);
                var reconnectPath = reconnect.Children.OfType<TextBox>().Single();
                var reconnectButton = buttons.Single(button => button.Content.ToString() == "重新关联素材库");
                reconnectPath.Text = "/music/moved"; await Task.Delay(100);
                await Click("检查目录匹配");
                verify("nas_reconnect_matching_preflight", reconnectButton.IsEnabled && handler.Libraries["initial"].Path == "/music/initial");
                handler.ReconnectMatch = false;
                await Click("重新关联素材库");
                verify("nas_reconnect_changed_match_rejected", status.Text.Contains("变化") && handler.Libraries["initial"].Path == "/music/initial");
                await Click("刷新飞牛设置");
                await Click("检查目录匹配");
                verify("nas_reconnect_mismatch_confirmation", !reconnectButton.IsEnabled && reconnect.Children.OfType<CheckBox>().Single().Visibility == Microsoft.UI.Xaml.Visibility.Visible);
                ((IToggleProvider)new CheckBoxAutomationPeer(reconnect.Children.OfType<CheckBox>().Single()).GetPattern(PatternInterface.Toggle)).Toggle();
                await Click("重新关联素材库");
                verify("nas_reconnect_path_readback", handler.Libraries["initial"].Path == "/music/moved" && reconnect.Children.OfType<TextBlock>().Any(text => text.Text.Contains("已回读确认")));
                handler.FailedRead = "shared-library/list"; await Click("刷新飞牛设置");
                verify("nas_settings_library_failure_disables_dependent_writes", status.Text.Contains("素材库")
                    && !buttons.Single(button => button.Content.ToString() == "保存默认访问范围").IsEnabled
                    && !buttons.Single(button => button.Content.ToString() == "保存素材库设置").IsEnabled
                    && buttons.Single(button => button.Content.ToString() == "保存服务器名称").IsEnabled);
                tabs.SelectedIndex = 3; await Task.Delay(150); await Click("刷新后台任务");
                verify("nas_settings_partial_failure_keeps_tasks", taskDetails.Text.Contains("不代表之前的扫描已成功完成") && status.Text.Contains("素材库"));
                handler.FailedRead = null;
                ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
                ViewModel.PlaybackRouter.Register(new CapabilityPlaybackSource(new NasPlaybackSource(api), MusicSourceCapabilities.ReadSettings));
                int readonlyWrites = handler.Writes;
                await Click("刷新飞牛设置");
                verify("nas_settings_read_only_capability", !buttons.Single(button => button.Content.ToString() == "保存素材库设置").IsEnabled
                    && !buttons.Single(button => button.Content.ToString() == "保存服务器名称").IsEnabled
                    && !buttons.Single(button => button.Content.ToString() == "重新关联素材库").IsEnabled);
                await Click("刷新后台任务");
                verify("nas_settings_read_only_refresh_no_writes", handler.Writes == readonlyWrites && status.Text.Contains("操作已返回"));
                ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
                ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api));
                await Click("刷新飞牛设置");
                verify("nas_settings_write_capability_restored", buttons.Single(button => button.Content.ToString() == "保存素材库设置").IsEnabled);
                handler.Admin = false; await Click("刷新飞牛设置");
                verify("nas_settings_member_readonly", !buttons.Single(button => button.Content.ToString() == "保存默认访问范围").IsEnabled
                    && !buttons.Single(button => button.Content.ToString() == "保存素材库设置").IsEnabled);
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { CloseFullPage(); }
        };
        try
        {
            await ShowNasSettingsAsync(api); await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            settingsPageOpened = null;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.PausedRead = release.Task;
            handler.ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = ShowNasSettingsAsync(api);
            await handler.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var loading = fullPageView ?? throw new InvalidOperationException();
            verify("nas_settings_loading_full_page_can_return", !loading.Editor.IsEnabled && !loading.BlockNavigation && CloseFullPage() && loading.IsDisposed);
            ShowQueuePage();
            var nextPage = queuePage;
            release.TrySetResult(); await pending;
            verify("nas_settings_late_read_does_not_replace_next_page", ReferenceEquals(queuePage, nextPage) && nextPage is { IsDisposed: false } && fullPageView is null);
            CloseQueuePage();
            handler.PausedRead = null;
            await ShowNasSettingsAsync(api);
            var oldPage = fullPageView ?? throw new InvalidOperationException();
            oldPage.BlockNavigation = true;
            settingsTestClient = null;
            ResetLibrary();
            verify("nas_settings_account_reset_forces_page_close", oldPage.IsDisposed && fullPageView is null && FullPageHost.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed);
        }
        finally { CloseQueuePage(force: true); CloseFullPage(force: true); ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId); settingsTestClient = null; settingsPageOpened = null; }
    }
}
#endif
