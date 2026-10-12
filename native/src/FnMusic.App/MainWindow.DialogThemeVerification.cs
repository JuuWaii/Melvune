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
        bool UsesSystemAccent(ElementTheme theme) => FindCheckBackground(checkbox)?.Fill is SolidColorBrush brush &&
            brush.Color == systemTheme.GetColorValue(theme == ElementTheme.Light
                ? global::Windows.UI.ViewManagement.UIColorType.AccentDark1
                : global::Windows.UI.ViewManagement.UIColorType.AccentLight2);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "主题验证", Content = content, CloseButtonText = "关闭" };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(100);
                verify("dialog_initial_light_theme", dialog.ActualTheme == ElementTheme.Light && content.ActualTheme == ElementTheme.Light);
                verify("dialog_light_checkbox_system_accent", UsesSystemAccent(ElementTheme.Light));
                await VerifyAccentPlayerStatesAsync(ElementTheme.Light, verify);
                Root.RequestedTheme = ElementTheme.Dark;
                await Task.Delay(100);
                verify("dialog_follows_dark_notification", dialog.RequestedTheme == ElementTheme.Dark && content.ActualTheme == ElementTheme.Dark);
                verify("dialog_dark_checkbox_system_accent", UsesSystemAccent(ElementTheme.Dark));
                await VerifyAccentPlayerStatesAsync(ElementTheme.Dark, verify);
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

    private async Task VerifyAccentPlayerStatesAsync(ElementTheme theme, Action<string, bool> verify)
    {
        var enabled = PlayPauseButton.IsEnabled;
        try
        {
            PlayPauseButton.IsEnabled = true;
            PlayPauseButton.ApplyTemplate(); Root.UpdateLayout();
            var expectedFill = systemTheme.GetColorValue(theme == ElementTheme.Light
                ? global::Windows.UI.ViewManagement.UIColorType.AccentDark1
                : global::Windows.UI.ViewManagement.UIColorType.AccentLight2);
            foreach (var state in new[] { "Normal", "PointerOver", "Pressed", "Disabled" })
            {
                if (state == "Disabled") PlayPauseButton.IsEnabled = false;
                VisualStateManager.GoToState(PlayPauseButton, state, false);
                await Task.Delay(100);
                var presenter = FindSongVisual<ContentPresenter>(PlayPauseButton, value => value.Name == "ContentPresenter");
                verify($"play_button_{theme}_{state}_icon_matches_presenter", presenter?.Foreground is SolidColorBrush text &&
                    PlayPauseIcon.Foreground is SolidColorBrush icon && icon.Color == text.Color);
                if (state == "Normal")
                    verify($"play_button_{theme}_system_accent", presenter?.Background is SolidColorBrush fill && fill.Color == expectedFill);
                if (state != "Disabled")
                    verify($"play_button_{theme}_{state}_contrast", presenter is { Background: SolidColorBrush background, Foreground: SolidColorBrush foreground }
                        && AccentContrast(background.Color, foreground.Color, theme) >= 3);
                else
                    verify($"play_button_{theme}_disabled_native_surface", !PlayPauseButton.IsEnabled &&
                        presenter is { Background: SolidColorBrush disabledFill, Foreground: SolidColorBrush disabledText } &&
                        disabledFill.Color.A is > 0 and < 255 && disabledText.Color.A > 0 && disabledFill.Color != disabledText.Color);
            }
            // Ordinary actions retain their original neutral foreground.
            verify($"player_regular_button_{theme}_neutral", PreviousTrackButton.Foreground is SolidColorBrush regular && regular.Color.R == regular.Color.G && regular.Color.G == regular.Color.B && (theme == ElementTheme.Light ? regular.Color.R < 70 : regular.Color.R > 230));
        }
        finally
        {
            PlayPauseButton.IsEnabled = enabled;
            VisualStateManager.GoToState(PlayPauseButton, enabled ? "Normal" : "Disabled", false);
        }
    }

    private static double AccentContrast(global::Windows.UI.Color background, global::Windows.UI.Color foreground, ElementTheme theme)
    {
        static double Linear(double channel) => channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        static double Luminance(double red, double green, double blue) => .2126 * Linear(red) + .7152 * Linear(green) + .0722 * Linear(blue);
        double surface = theme == ElementTheme.Light ? 1 : 0;
        double alpha = background.A / 255d;
        double red = background.R / 255d * alpha + surface * (1 - alpha);
        double green = background.G / 255d * alpha + surface * (1 - alpha);
        double blue = background.B / 255d * alpha + surface * (1 - alpha);
        double inkAlpha = foreground.A / 255d;
        double paper = Luminance(red, green, blue);
        double ink = Luminance(foreground.R / 255d * inkAlpha + red * (1 - inkAlpha),
            foreground.G / 255d * inkAlpha + green * (1 - inkAlpha), foreground.B / 255d * inkAlpha + blue * (1 - inkAlpha));
        return (Math.Max(paper, ink) + .05) / (Math.Min(paper, ink) + .05);
    }
}
#endif
