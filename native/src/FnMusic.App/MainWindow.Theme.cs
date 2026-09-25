using FnMusic.Infrastructure;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private readonly ThemeSettingsStore themeSettings = new(ThemeSettingsDirectory());
    private ThemePreference themePreference;
    private bool updatingThemeChoice = true;

    private static string ThemeSettingsDirectory()
    {
#if DEBUG
        // Synthetic UI verification must never replace the user's saved preference.
        if (Environment.GetCommandLineArgs().Contains("--preview-synthetic"))
            return Path.Combine(AppContext.BaseDirectory, "synthetic-settings");
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FnMusic.Native");
    }

    private void InitializeThemeChoice()
    {
        try { themePreference = themeSettings.Load(); }
        catch (Exception) { themePreference = ThemePreference.System; ThemeStatus.Text = "外观设置无法读取，暂时跟随系统。"; }
        ThemePicker.SelectedIndex = (int)themePreference;
        updatingThemeChoice = false;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updatingThemeChoice || ThemePicker.SelectedIndex is < 0 or > 2) return;
        var requested = (ThemePreference)ThemePicker.SelectedIndex;
        try
        {
            themeSettings.Save(requested);
            themePreference = requested;
            ThemeStatus.Text = "";
            ApplySystemTheme();
        }
        catch (Exception)
        {
            updatingThemeChoice = true;
            ThemePicker.SelectedIndex = (int)themePreference;
            updatingThemeChoice = false;
            ThemeStatus.Text = "外观设置保存失败，请重试。";
        }
    }
}
