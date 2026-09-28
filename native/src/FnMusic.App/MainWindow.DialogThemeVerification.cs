#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private async Task VerifyDialogThemeAsync(Action<string, bool> verify)
    {
        var original = Root.RequestedTheme;
        var content = new StackPanel();
        var checkbox = new CheckBox { Content = "弹窗主题验证", IsChecked = true };
        content.Children.Add(checkbox);
        Rectangle? FindCheckBackground(DependencyObject parent)
        {
            if (parent is Rectangle { Name: "NormalRectangle" } rectangle) return rectangle;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (FindCheckBackground(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
            return null;
        }
        bool IsMonochrome(byte expected) => FindCheckBackground(checkbox)?.Fill is SolidColorBrush brush &&
            brush.Color.R == expected && brush.Color.G == expected && brush.Color.B == expected;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "主题验证", Content = content, CloseButtonText = "关闭" };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(100);
                verify("dialog_initial_light_theme", dialog.ActualTheme == ElementTheme.Light && content.ActualTheme == ElementTheme.Light);
                verify("dialog_light_checkbox_monochrome", IsMonochrome(32));
                Root.RequestedTheme = ElementTheme.Dark;
                await Task.Delay(100);
                verify("dialog_follows_dark_notification", dialog.RequestedTheme == ElementTheme.Dark && content.ActualTheme == ElementTheme.Dark);
                verify("dialog_dark_checkbox_monochrome", IsMonochrome(229));
                Root.RequestedTheme = ElementTheme.Light;
                await Task.Delay(100);
                verify("dialog_follows_light_notification", dialog.RequestedTheme == ElementTheme.Light && content.ActualTheme == ElementTheme.Light);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { dialog.Hide(); }
        };
        try
        {
            Root.RequestedTheme = ElementTheme.Light;
            await ShowThemedDialogAsync(dialog);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Root.RequestedTheme = ElementTheme.Dark;
            verify("dialog_closed_theme_subscription_removed", dialog.RequestedTheme == ElementTheme.Light);
        }
        finally { Root.RequestedTheme = original; }
    }
}
#endif
