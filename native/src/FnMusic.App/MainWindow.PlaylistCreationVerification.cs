#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private async Task VerifyPlaylistCreationDialogAsync(Action<string, bool> verify, Func<Task> operation, string? enteredName, string prefix, int maxLength, Action? beforeAccept = null, string primaryLabel = "创建", Func<Task>? beforeSubmit = null)
    {
        ContentDialog? opened = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playlistNameDialogOpened = async (dialog, name) =>
        {
            opened = dialog;
            try
            {
                await Task.Delay(80);
                verify(prefix + "_dialog_follows_theme_and_name_limit", dialog.ActualTheme == Root.ActualTheme && name.MaxLength == maxLength);
                name.Text = "   ";
                await Task.Delay(80); Root.UpdateLayout();
                verify(prefix + "_blank_name_cannot_create", !dialog.IsPrimaryButtonEnabled);
                name.Text = enteredName ?? "";
                beforeAccept?.Invoke();
                if (beforeSubmit is not null) await beforeSubmit();
                Root.UpdateLayout();
                await Task.Delay(80);
                var button = FindSongVisual<Button>(dialog, b => Equals(b.Content, enteredName is null ? "取消" : primaryLabel))
                    ?? throw new InvalidOperationException();
                verify(prefix + "_action_rendered_and_enabled", button.IsLoaded && button.ActualWidth > 0 && button.IsEnabled);
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "playlist-dialog-diagnostic.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { prefix, Type = ex.GetType().Name, ex.HResult, ex.StackTrace }));
                completion.TrySetException(ex); dialog.Hide();
            }
        };
        try { await operation().WaitAsync(TimeSpan.FromSeconds(10)); await completion.Task; }
        finally { playlistNameDialogOpened = null; if (themedDialogOpen) opened?.Hide(); }
    }
    private async Task VerifyPlaylistDeleteDialogAsync(Func<Task> operation, bool accept, Action? beforeAccept = null)
    {
        ContentDialog? opened = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playlistDeleteDialogOpened = async dialog =>
        {
            opened = dialog;
            try
            {
                await Task.Delay(100);
                beforeAccept?.Invoke();
                var button = FindSongVisual<Button>(dialog, b => Equals(b.Content, accept ? "删除" : "取消"))!;
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); dialog.Hide(); }
        };
        try { await operation().WaitAsync(TimeSpan.FromSeconds(10)); await completion.Task; }
        finally { playlistDeleteDialogOpened = null; if (themedDialogOpen) opened?.Hide(); }
    }
}
#endif
