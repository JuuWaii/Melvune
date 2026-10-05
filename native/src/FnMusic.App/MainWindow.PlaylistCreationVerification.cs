#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private async Task VerifyPlaylistCreationDialogAsync(Action<string, bool> verify, Func<Task> operation, string? enteredName, string prefix, int maxLength, Action? beforeAccept = null)
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
                verify(prefix + "_blank_name_cannot_create", !dialog.IsPrimaryButtonEnabled);
                name.Text = enteredName ?? "";
                beforeAccept?.Invoke();
                Root.UpdateLayout();
                await Task.Delay(80);
                var button = FindSongVisual<Button>(dialog, b => Equals(b.Content, enteredName is null ? "取消" : "创建"))
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
}
#endif
