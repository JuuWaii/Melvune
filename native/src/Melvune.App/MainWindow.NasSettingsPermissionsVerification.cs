#if DEBUG
using System.Text.Json;
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private sealed record SettingsPermissionProfile(string Label, string Directory, bool Administrator, bool CanBrowse);

    // Existing encrypted, isolated profiles only. No login UI or NAS mutations.
    // Exercises production settings forms and refresh with real permission roles.
    private async Task VerifyNasSettingsPermissionsWindowAsync()
    {
        var checks = new Dictionary<string, bool>();
        var catalogCounts = new Dictionary<string, int>();
        string stage = "profiles", error = "";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        void Verify(string name, bool value)
        { stage = name; checks[name] = value; if (!value) throw new InvalidOperationException(); }
        try
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--verification-profiles");
            if (index < 0 || index + 1 >= args.Length || new FileInfo(args[index + 1]).Length > 16384) throw new ArgumentException();
            var profiles = JsonSerializer.Deserialize<SettingsPermissionProfile[]>(await File.ReadAllTextAsync(args[index + 1], timeout.Token))
                ?? throw new InvalidDataException();
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            Verify("profile_scope", profiles.Length == 3 && profiles.Select(p => p.Label).SequenceEqual(new[] { "admin", "full_access", "restricted" })
                && profiles.Select(p => p.Directory).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3
                && profiles.All(p => Path.IsPathFullyQualified(p.Directory) && Path.GetDirectoryName(Path.GetFullPath(p.Directory)) == parent
                    && Path.GetFileName(p.Directory).StartsWith("verification-session-", StringComparison.Ordinal))
                && profiles[0].Administrator && profiles[0].CanBrowse && !profiles[1].Administrator && profiles[1].CanBrowse
                && !profiles[2].Administrator && !profiles[2].CanBrowse);
            motionPreferenceOverride = false;
            var sources = new HashSet<Guid>();
            foreach (var profile in profiles)
            {
                stage = profile.Label + "_session";
                using var connection = new ViewModels.ConnectionViewModel(profile.Directory);
                await connection.InitializeAsync().WaitAsync(timeout.Token);
                var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
                Verify(stage, sources.Add(api.SourceInstanceId));
                Verify(profile.Label + "_real_role", await api.IsAdministratorAsync(timeout.Token) == profile.Administrator);
                var tracks = await api.ListTracksAsync(1, 1, timeout.Token);
                catalogCounts[profile.Label] = tracks.Total;
                // A changed server permission prerequisite must remain a failure,
                // but it must not prevent the independent settings-form checks.
                checks[profile.Label + "_real_music_access"] = profile.CanBrowse ? tracks.Total > 0 && tracks.Tracks.Count == 1
                    : tracks.Total == 0 && tracks.Tracks.Count == 0;
                settingsTestClient = api; remoteCapabilityTestSource = api.SourceInstanceId;
                ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api));
                try
                {
                    await ShowNasSettingsAsync(api).WaitAsync(timeout.Token);
                    var view = fullPageView ?? throw new InvalidOperationException();
                    Verify(profile.Label + "_settings_page", !view.IsDisposed && view.Editor.IsEnabled
                        && FullPageHost.Visibility == Visibility.Visible && view.RemoteSource == api.SourceInstanceId);
                    var tabs = view.Fields.Children.OfType<Pivot>().Single();
                    var buttons = view.Actions.Children.OfType<Button>().ToList();
                    for (int tab = 0; tab < tabs.Items.Count; tab++)
                    {
                        tabs.SelectedIndex = tab;
                        buttons.AddRange(view.Actions.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>()));
                    }
                    Button Button(string name) => buttons.Single(b => b.Content?.ToString() == name);
                    var library = (StackPanel)((PivotItem)tabs.Items[0]).Content;
                    var server = (StackPanel)((PivotItem)tabs.Items[1]).Content;
                    Verify(profile.Label + "_editing_fields", library.Children.OfType<ComboBox>().Single(c => c.Header?.ToString() == "元数据优先策略").IsEnabled == profile.Administrator
                        && library.Children.OfType<TextBox>().Single().IsEnabled == profile.Administrator
                        && server.Children.OfType<TextBox>().Single().IsEnabled == profile.Administrator);
                    if (profile.Administrator)
                        Verify("admin_snapshot_write_actions", Button("保存素材库设置").IsEnabled && Button("扫描所选素材库").IsEnabled
                            && Button("保存服务器名称").IsEnabled && Button("保存默认访问范围").IsEnabled);
                    else
                        Verify(profile.Label + "_all_write_actions_disabled", new[] { "保存素材库设置", "扫描所选素材库", "移除素材库", "创建素材库",
                            "保存服务器名称", "保存默认访问范围", "取消所选任务", "重试所选任务", "删除任务记录", "检查目录匹配", "重新关联素材库" }
                            .All(name => !Button(name).IsEnabled));
                    tabs.SelectedIndex = 0;
                    Verify(profile.Label + "_read_refresh_enabled", Button("刷新飞牛设置").IsEnabled && Button("刷新后台任务").IsEnabled);
                    await view.Reload!().WaitAsync(timeout.Token);
                    Verify(profile.Label + "_refresh_preserves_permission", ReferenceEquals(view, fullPageView) && view.Editor.IsEnabled
                        && Button("保存服务器名称").IsEnabled == profile.Administrator
                        && Button("保存素材库设置").IsEnabled == profile.Administrator);
                    ResetLibrary();
                    Verify(profile.Label + "_source_reset_closes_page", view.IsDisposed && fullPageView is null
                        && FullPageHost.Visibility == Visibility.Collapsed);
                }
                finally
                {
                    CloseFullPage(force: true);
                    ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
                    settingsTestClient = null; remoteCapabilityTestSource = null;
                }
            }
            stage = checks.Values.All(value => value) ? "complete" : "music_permission_prerequisite_changed";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally { motionPreferenceOverride = null; CloseFullPage(force: true); }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "nas-settings-permissions-window-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks, catalogCounts }));
    }
}
#endif
