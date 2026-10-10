using System.Text;
using FnMusic.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace FnMusic.App;

public sealed record DesktopLyricsOptions(bool Topmost = true, bool Locked = false,
    double FontSize = 28, string Theme = "system", int X = 100, int Y = 100,
    int Width = 720, int Height = 180);

/// <summary>A view of the real playback clock. Closing this window only hides it.</summary>
public sealed class DesktopLyricsWindow : Window, IDisposable
{
    private readonly PlaybackSnapshotHub hub;
    private readonly Grid root = new() { Padding = new Thickness(16, 6, 16, 8), RowSpacing = 4 };
    private readonly StackPanel controls = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock lyricText = new() { TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock statusText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
        TextAlignment = TextAlignment.Center, Opacity = .7 };
    private readonly ScrollViewer lyricScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled };
    private readonly UISettings systemTheme = new();
    private readonly Button topmostButton;
    private DesktopLyricsOptions options;
    private bool disposed, applyingOptions, visible;
    private Guid? plainGeneration;
    private PlaybackLyricsSnapshot? plainLyrics;
    private string plainText = "";

    public event Action<DesktopLyricsOptions>? OptionsChanged;
    public event Action? Hidden;
    public DesktopLyricsOptions Options => options;
    public bool IsLyricsVisible => visible;

    public DesktopLyricsWindow(PlaybackSnapshotHub hub, DesktopLyricsOptions options)
    {
        this.hub = hub;
        this.options = Normalize(options);
        Title = "飞牛音乐 · 桌面歌词";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FnMusic.ico"));
        root.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("ms-appx:///MonochromeResources.xaml") });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topmostButton = AddButton("置顶", () => ChangeOptions(this.options with { Topmost = !this.options.Topmost }));
        AddButton("锁定", () => ChangeOptions(this.options with { Locked = true }));
        AddButton("A−", () => ChangeOptions(this.options with { FontSize = this.options.FontSize - 2 }));
        AddButton("A+", () => ChangeOptions(this.options with { FontSize = this.options.FontSize + 2 }));
        AddButton("隐藏", HideLyrics);
        lyricScroll.Content = lyricText;
        Grid.SetRow(lyricScroll, 1); Grid.SetRow(statusText, 2);
        root.Children.Add(controls); root.Children.Add(lyricScroll); root.Children.Add(statusText);
        Content = root;
        root.ActualThemeChanged += RootThemeChanged;
        systemTheme.ColorValuesChanged += SystemThemeChanged;
        AppWindow.Closing += WindowClosing;
        AppWindow.Changed += WindowChanged;
        hub.Changed += SnapshotChanged;
        ApplyOptions(this.options);
        RenderSnapshot();
    }

    private Button AddButton(string title, Action action)
    {
        var button = new Button { Content = title, Padding = new Thickness(8, 2, 8, 2), MinHeight = 24, FontSize = 12 };
        button.Click += (_, _) => { if (!disposed) action(); };
        controls.Children.Add(button);
        return button;
    }

    public void ShowLyrics()
    {
        if (disposed) return;
        RecoverPosition();
        RenderSnapshot();
        visible = true;
        Activate();
    }

    public void HideLyrics()
    {
        if (disposed || !visible) return;
        AppWindow.Hide();
        visible = false;
        Hidden?.Invoke();
    }

    public void ApplyOptions(DesktopLyricsOptions value)
    {
        if (disposed) return;
        applyingOptions = true;
        try
        {
            options = Normalize(value);
            var presenter = (OverlappedPresenter)AppWindow.Presenter;
            presenter.IsAlwaysOnTop = options.Topmost;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = !options.Locked;
            presenter.SetBorderAndTitleBar(!options.Locked, !options.Locked);
            controls.Visibility = options.Locked ? Visibility.Collapsed : Visibility.Visible;
            topmostButton.Content = options.Topmost ? "取消置顶" : "置顶";
            lyricText.FontSize = options.FontSize;
            ApplyTheme();
            AppWindow.MoveAndResize(BoundPosition(options));
            CapturePosition();
        }
        finally { applyingOptions = false; }
        RenderSnapshot();
    }

    public void RecoverPosition()
    {
        if (disposed) return;
        applyingOptions = true;
        try { AppWindow.MoveAndResize(BoundPosition(options)); CapturePosition(); }
        finally { applyingOptions = false; }
        OptionsChanged?.Invoke(options);
    }

    // Positions and sizes use AppWindow physical pixels, so saved geometry is also valid across DPI changes.
    private RectInt32 BoundPosition(DesktopLyricsOptions value)
    {
        var area = DisplayArea.GetFromPoint(new PointInt32(value.X, value.Y), DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min(value.Width, Math.Max(1, area.Width));
        int height = Math.Min(value.Height, Math.Max(1, area.Height));
        return new RectInt32(Math.Clamp(value.X, area.X, area.X + area.Width - width),
            Math.Clamp(value.Y, area.Y, area.Y + area.Height - height), width, height);
    }

    private static DesktopLyricsOptions Normalize(DesktopLyricsOptions value) => value with
    {
        FontSize = double.IsFinite(value.FontSize) ? Math.Clamp(value.FontSize, 16, 64) : 28,
        Theme = value.Theme is "light" or "dark" ? value.Theme : "system",
        X = Math.Clamp(value.X, -100000, 100000), Y = Math.Clamp(value.Y, -100000, 100000),
        Width = Math.Clamp(value.Width, 320, 2400), Height = Math.Clamp(value.Height, 140, 1200)
    };

    private void ChangeOptions(DesktopLyricsOptions value)
    { ApplyOptions(value); OptionsChanged?.Invoke(options); }

    private void WindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    { if (disposed) return; args.Cancel = true; HideLyrics(); }

    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (disposed || applyingOptions || !(args.DidPositionChange || args.DidSizeChange)) return;
        CapturePosition(); OptionsChanged?.Invoke(options);
    }

    private void CapturePosition() => options = options with
    { X = AppWindow.Position.X, Y = AppWindow.Position.Y, Width = AppWindow.Size.Width, Height = AppWindow.Size.Height };

    private void SnapshotChanged(PlaybackSnapshot _) => DispatcherQueue.TryEnqueue(() =>
    { if (!disposed) RenderSnapshot(); });

    private void RenderSnapshot()
    {
        var snapshot = hub.Current;
        lyricScroll.VerticalScrollMode = snapshot.LyricsKind == PlaybackLyricsKind.Plain && !options.Locked
            ? ScrollMode.Enabled : ScrollMode.Disabled;
        lyricScroll.VerticalScrollBarVisibility = snapshot.LyricsKind == PlaybackLyricsKind.Plain && !options.Locked
            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        lyricText.Text = snapshot.State == PlaybackStatus.Stopped ? "尚未播放" : snapshot.LyricsKind switch
        {
            PlaybackLyricsKind.Loading => "正在读取歌词…",
            PlaybackLyricsKind.Empty => "暂无歌词",
            PlaybackLyricsKind.Unavailable => "歌词暂不可用",
            PlaybackLyricsKind.Plain => ReadPlainLyrics(snapshot),
            _ => string.IsNullOrWhiteSpace(snapshot.CurrentLine) ? "等待歌词…" : snapshot.CurrentLine
        };
        string state = snapshot.State switch
        { PlaybackStatus.Loading => "正在加载", PlaybackStatus.Playing => "播放中", PlaybackStatus.Paused => "已暂停",
            PlaybackStatus.Buffering => "缓冲中", _ => "已停止" };
        string kind = snapshot.LyricsKind == PlaybackLyricsKind.Plain ? " · 纯文本歌词（无时间轴）" : "";
        statusText.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? state : $"{snapshot.Title} · {state}{kind}";
        if (options.Locked) statusText.Text += " · 已锁定，可在主窗口解锁";
    }

    private string ReadPlainLyrics(PlaybackSnapshot snapshot)
    {
        var lyrics = hub.Lyrics;
        if (lyrics.TrackGeneration != snapshot.TrackGeneration || lyrics.Kind != PlaybackLyricsKind.Plain) return "正在读取歌词…";
        if (plainGeneration == snapshot.TrackGeneration && ReferenceEquals(plainLyrics, lyrics)) return plainText;
        plainGeneration = snapshot.TrackGeneration; plainLyrics = lyrics;
        var text = new StringBuilder();
        bool limited = lyrics.IsLimited;
        foreach (var line in lyrics.Lines)
        {
            if (text.Length + line.Text.Length + 1 > 12000) { limited = true; break; }
            if (text.Length != 0) text.AppendLine();
            text.Append(line.Text);
        }
        if (limited) text.AppendLine().Append("（歌词较长，桌面窗口仅显示前部分）");
        return plainText = text.Length == 0 ? "暂无歌词" : text.ToString();
    }

    private void SystemThemeChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    { if (!disposed && options.Theme == "system") ApplyTheme(); });

    private void ApplyTheme()
    {
        var foreground = systemTheme.GetColorValue(UIColorType.Foreground);
        root.RequestedTheme = options.Theme switch
        { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark,
            _ => foreground.R + foreground.G + foreground.B > 384 ? ElementTheme.Dark : ElementTheme.Light };
        UpdateThemeColors();
    }

    private void RootThemeChanged(FrameworkElement sender, object args) => UpdateThemeColors();
    private void UpdateThemeColors()
    {
        bool dark = root.RequestedTheme == ElementTheme.Dark;
        var background = dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        var foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        root.Background = new SolidColorBrush(background);
        lyricText.Foreground = statusText.Foreground = new SolidColorBrush(foreground);
        var bar = AppWindow.TitleBar;
        bar.BackgroundColor = bar.ButtonBackgroundColor = bar.InactiveBackgroundColor = bar.ButtonInactiveBackgroundColor = background;
        bar.ForegroundColor = bar.ButtonForegroundColor = bar.InactiveForegroundColor = bar.ButtonInactiveForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(255, 128, 128, 128);
        bar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
    }

#if DEBUG
    internal string DisplayedLyrics => lyricText.Text;
    internal string DisplayedStatus => statusText.Text;
#endif

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; visible = false;
        hub.Changed -= SnapshotChanged;
        systemTheme.ColorValuesChanged -= SystemThemeChanged;
        root.ActualThemeChanged -= RootThemeChanged;
        AppWindow.Closing -= WindowClosing;
        AppWindow.Changed -= WindowChanged;
        OptionsChanged = null; Hidden = null;
        Close();
    }
}
