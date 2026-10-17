#if DEBUG
using Melvune.App.Services;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using System.Text.Json;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private async Task VerifyCloseChoiceWindowAsync()
    {
        // This suite mutates only an explicitly isolated preview profile and closes its own window.
        string profile = Path.GetFullPath(AppDataDirectory.Resolve());
        if (!IsSyntheticPreview || !Environment.GetCommandLineArgs().Contains("--verify-close-choice-window")
            || !profile.StartsWith(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Close-choice verification requires an isolated child profile.");
        var checks = new Dictionary<string, bool>();
        string stage = "initial", error = "";
        int errorCode = 0;
        string report = Path.Combine(profile, "close-choice-verification.json");
        Directory.CreateDirectory(profile);
        void Check(string name, bool value)
        {
            stage = name; checks[name] = value;
            if (!value) throw new InvalidOperationException(name);
        }
        void WriteReport() => File.WriteAllText(report, JsonSerializer.Serialize(new { stage, error, errorCode, checks }));
        static void Invoke(ContentDialog dialog, string caption)
        {
            dialog.UpdateLayout();
            var button = FindSongVisual<Button>(dialog, candidate => Equals(candidate.Content, caption))
                ?? throw new InvalidOperationException("Close-choice dialog button missing.");
            if (!button.IsLoaded || button.ActualWidth <= 0 || !button.IsEnabled)
                throw new InvalidOperationException("Close-choice dialog button unavailable.");
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
        async Task WithChoice(Func<Task> operation, string caption, bool remember, string prefix, Func<ContentDialog, Task>? whileOpen = null)
        {
            ContentDialog? opened = null;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            closeChoiceOpened = async (dialog, checkbox) =>
            {
                opened = dialog;
                try
                {
                    await Task.Delay(100);
                    Check(prefix + "_dialog_theme_and_buttons", dialog.ActualTheme == Root.ActualTheme
                        && dialog.PrimaryButtonText == "收起到托盘" && dialog.SecondaryButtonText == "退出应用"
                        && dialog.CloseButtonText == "取消" && dialog.DefaultButton == ContentDialogButton.Close);
                    Check(prefix + "_remember_checked_by_default", checkbox.IsChecked == true);
                    checkbox.IsChecked = remember;
                    if (whileOpen is not null) await whileOpen(dialog);
                    Invoke(dialog, caption);
                    completed.TrySetResult();
                }
                catch (Exception exception) { completed.TrySetException(exception); dialog.Hide(); }
            };
            try
            {
                await operation().WaitAsync(TimeSpan.FromSeconds(15));
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { closeChoiceOpened = null; if (themedDialogOpen) opened?.Hide(); }
        }
        bool exitObserverAttached = false;
        void VerificationWindowClosed(object sender, WindowEventArgs args)
        {
            Closed -= VerificationWindowClosed;
            exitObserverAttached = false;
            // Window.Close() intentionally bypasses AppWindow.Closing. The ordinary Closed handler
            // registered by MainWindow runs first and tears down playback and linked services.
            bool passed = explicitDesktopExit && closingIntegration && closed && closeChoiceDialog is null;
            checks["explicit_tray_exit_completes_window_teardown"] = passed;
            if (error.Length == 0) stage = passed ? "complete" : "explicit_tray_exit_completes_window_teardown";
            WriteReport();
        }
        try
        {
            await Task.Delay(200);
            Check("fresh_install_asks_and_remember_is_off", NeedsMainWindowCloseChoice
                && !integrationSettings.CloseChoiceRemembered && !closeChoiceMadeThisSession);
            Check("legacy_tray_true_migrates", ReadDesktopIntegrationSettings("{\"CloseToTray\":true}").CloseChoiceRemembered);
            Check("legacy_tray_false_still_asks", !ReadDesktopIntegrationSettings("{\"CloseToTray\":false}").CloseChoiceRemembered);
            Check("new_unremembered_tray_is_not_legacy", !ReadDesktopIntegrationSettings("{\"CloseToTray\":true,\"CloseChoiceRemembered\":false}").CloseChoiceRemembered);
            byte[]? previousFile = File.Exists(IntegrationSettingsFile) ? await File.ReadAllBytesAsync(IntegrationSettingsFile) : null;
            var initial = integrationSettings;
            await WithChoice(HandleMainWindowCloseAsync, "取消", false, "cancel", async dialog =>
            {
                await HandleMainWindowCloseAsync();
                Check("reentrant_close_keeps_one_dialog", ReferenceEquals(closeChoiceDialog, dialog)
                    && themedDialogCount == 1 && handlingMainWindowClose && AppWindow.IsVisible);
            });
            Check("cancel_preserves_window_and_decision", AppWindow.IsVisible && !hiddenToTray && !handlingMainWindowClose
                && closeChoiceDialog is null && !themedDialogOpen && NeedsMainWindowCloseChoice && integrationSettings == initial);
            Check("cancel_does_not_write_preferences", previousFile is null ? !File.Exists(IntegrationSettingsFile)
                : Enumerable.SequenceEqual(previousFile, await File.ReadAllBytesAsync(IntegrationSettingsFile)));

            // An unrelated modal must remain the only modal; closing does not discard it.
            var editor = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "保留编辑", CloseButtonText = "取消" };
            var editorCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            editor.Opened += async (_, _) =>
            {
                try
                {
                    await Task.Delay(100);
                    await HandleMainWindowCloseAsync();
                    Check("existing_editor_blocks_close_question", themedDialogCount == 1 && closeChoiceDialog is null
                        && AppWindow.IsVisible && !handlingMainWindowClose && NeedsMainWindowCloseChoice);
                    Invoke(editor, "取消"); editorCompletion.TrySetResult();
                }
                catch (Exception exception) { editorCompletion.TrySetException(exception); editor.Hide(); }
            };
            await ShowThemedDialogAsync(editor).WaitAsync(TimeSpan.FromSeconds(15));
            await editorCompletion.Task;

            MainWindowCloseChoice? exitChoice = null;
            await WithChoice(async () => { exitChoice = await AskMainWindowCloseChoiceAsync(); }, "退出应用", false, "exit_unremembered");
            Check("exit_button_returns_unremembered_choice", exitChoice is { ToTray: false, Remember: false }
                && integrationSettings == initial && !closed);
            MainWindowCloseChoice? trayChoice = null;
            await WithChoice(async () => { trayChoice = await AskMainWindowCloseChoiceAsync(); }, "收起到托盘", true, "tray_unavailable");
            closeChoiceTrayAvailability = () => false;
            Check("unavailable_tray_rejects_choice_and_keeps_window", trayChoice is not null && !ApplyMainWindowCloseChoice(trayChoice)
                && integrationSettings == initial && AppWindow.IsVisible && !closeChoiceMadeThisSession && NeedsMainWindowCloseChoice);
            Check("unavailable_tray_does_not_write_preferences", previousFile is null ? !File.Exists(IntegrationSettingsFile)
                : Enumerable.SequenceEqual(previousFile, await File.ReadAllBytesAsync(IntegrationSettingsFile)));
            closeChoiceTrayAvailability = null;
            Check("native_tray_is_available_for_real_hide", systemTray?.IsAvailable == true);

            await WithChoice(HandleMainWindowCloseAsync, "收起到托盘", false, "tray_session");
            var session = ReadDesktopIntegrationSettings(await File.ReadAllTextAsync(IntegrationSettingsFile));
            Check("unremembered_tray_hides_and_updates_settings", hiddenToTray && !AppWindow.IsVisible && !closed
                && !closingIntegration && !NeedsMainWindowCloseChoice && ShellCloseToTray.IsOn && ShellTrayEnabled.IsOn
                && session.CloseToTray && session.TrayEnabled && !session.CloseChoiceRemembered);
            await ExecuteDesktopShellCommandAsync(DesktopShellCommand.ShowMain);
            Check("tray_restore_recovers_same_window", AppWindow.IsVisible && !hiddenToTray && !closed);
            int repeatedQuestions = 0;
            closeChoiceOpened = (dialog, _) => { repeatedQuestions++; dialog.Hide(); };
            await HandleMainWindowCloseAsync();
            closeChoiceOpened = null;
            Check("unremembered_choice_is_reused_this_run", repeatedQuestions == 0 && hiddenToTray && !AppWindow.IsVisible && !handlingMainWindowClose);
            await ExecuteDesktopShellCommandAsync(DesktopShellCommand.ShowMain);
            closeChoiceMadeThisSession = false;
            integrationSettings = session;
            Check("unremembered_saved_choice_asks_next_run", NeedsMainWindowCloseChoice);

            await WithChoice(HandleMainWindowCloseAsync, "收起到托盘", true, "tray_remember");
            var remembered = ReadDesktopIntegrationSettings(await File.ReadAllTextAsync(IntegrationSettingsFile));
            Check("remembered_tray_persists_and_hides", remembered.CloseToTray && remembered.CloseChoiceRemembered
                && remembered.TrayEnabled && hiddenToTray && !AppWindow.IsVisible && ShellCloseToTray.IsOn);
            await ExecuteDesktopShellCommandAsync(DesktopShellCommand.ShowMain);
            integrationSettings = remembered; closeChoiceMadeThisSession = false;
            Check("remembered_saved_choice_skips_next_question", !NeedsMainWindowCloseChoice);
            closeChoiceTrayAvailability = () => false;
            await HandleMainWindowCloseAsync();
            Check("remembered_tray_loss_preserves_visible_window", AppWindow.IsVisible && !hiddenToTray && !closed
                && !closingIntegration && ShellStatus.Text.Contains("窗口已保留") && ShellCloseToTray.IsEnabled);
            closeChoiceTrayAvailability = null;

            ShellCloseToTray.IsOn = false;
            var exitSettings = ReadDesktopIntegrationSettings(await File.ReadAllTextAsync(IntegrationSettingsFile));
            Check("settings_close_switch_explicitly_updates_remembered_choice", !exitSettings.CloseToTray
                && exitSettings.CloseChoiceRemembered && !NeedsMainWindowCloseChoice);
            ShellTrayEnabled.IsOn = false;
            Check("disabled_tray_never_hides_window", !TryHideMainWindowToTray() && AppWindow.IsVisible && !ShellCloseToTray.IsEnabled);
            ShellTrayEnabled.IsOn = true;
            integrationSettings = integrationSettings with { CloseChoiceRemembered = false };
            closeChoiceMadeThisSession = false;
            explicitDesktopExit = true;
            Check("explicit_exit_never_asks_first_close_question", !NeedsMainWindowCloseChoice);
            explicitDesktopExit = false;

            // Exercise the actual Exit dialog button and remember preference before the final shutdown.
            MainWindowCloseChoice? finalChoice = null;
            await WithChoice(async () => { finalChoice = await AskMainWindowCloseChoiceAsync(); }, "退出应用", true, "exit_remember");
            Check("remembered_exit_applies", finalChoice is { ToTray: false, Remember: true } && ApplyMainWindowCloseChoice(finalChoice));
            var finalSettings = ReadDesktopIntegrationSettings(await File.ReadAllTextAsync(IntegrationSettingsFile));
            Check("remembered_exit_syncs_file_and_controls", finalSettings.CloseChoiceRemembered && !finalSettings.CloseToTray
                && !ShellCloseToTray.IsOn && !NeedsMainWindowCloseChoice && !hiddenToTray && AppWindow.IsVisible);
            Closed += VerificationWindowClosed;
            exitObserverAttached = true;
            stage = "awaiting-explicit-exit";
            WriteReport();
            await ExecuteDesktopShellCommandAsync(DesktopShellCommand.Exit);
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name; errorCode = exception.HResult;
            WriteReport();
            closeChoiceOpened = null; closeChoiceTrayAvailability = null;
            await RequestDesktopExitAsync();
        }
        finally
        {
            closeChoiceOpened = null; closeChoiceTrayAvailability = null;
            if (exitObserverAttached && closed) Closed -= VerificationWindowClosed;
        }
    }
}
#endif
