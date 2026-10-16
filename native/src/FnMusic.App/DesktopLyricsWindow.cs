using System.Text;
using System.Runtime.InteropServices;
using FnMusic.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private readonly TransparentLyricsBackdrop transparentBackdrop;
    private readonly MenuFlyout quickSettings = new();
    private readonly ToggleMenuFlyoutItem topmostSetting = new() { Text = "置顶" };
    private readonly ToggleMenuFlyoutItem lockedSetting = new() { Text = "锁定并穿透鼠标" };
    private readonly ToggleMenuFlyoutItem systemColorSetting = new() { Text = "跟随主窗口" };
    private readonly ToggleMenuFlyoutItem lightColorSetting = new() { Text = "深色文字" };
    private readonly ToggleMenuFlyoutItem darkColorSetting = new() { Text = "浅色文字" };
    private readonly List<TextBlock> lyricOutlines = [], statusOutlines = [];
    private ElementTheme appTheme = ElementTheme.Default;
    private PointInt32? dragStart, dragPosition;
    private bool updatingMenu;
    private DesktopLyricsOptions options;
    private volatile bool disposed, visible;
    private bool applyingOptions;
    private int snapshotQueued;
    private PlaybackSnapshot? renderedSnapshot;
    private PlaybackLyricsSnapshot? renderedLyrics;
    private bool renderedLocked;
#if DEBUG
    private int snapshotDispatches, snapshotRenders, textUpdates;
#endif
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
        transparentBackdrop = new TransparentLyricsBackdrop(WinRT.Interop.WindowNative.GetWindowHandle(this));
        transparentBackdrop.PrepareNativeHost();
        Title = "Melvune · 桌面歌词";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FnMusic.ico"));
        root.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("ms-appx:///MonochromeResources.xaml") });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topmostButton = AddButton("\uE718", "置顶", () => ChangeOptions(this.options with { Topmost = !this.options.Topmost }));
        AddButton("\uE72E", "锁定并穿透鼠标", () => ChangeOptions(this.options with { Locked = true }));
        var settingsButton = AddButton("\uE713", "桌面歌词快速设置", () => { });
        settingsButton.Flyout = quickSettings;
        AddButton("\uE8BB", "隐藏桌面歌词", HideLyrics);
        InitializeQuickSettings();
        root.ContextFlyout = quickSettings;
        lyricScroll.Content = OutlinedText(lyricText, lyricOutlines, 1.3);
        var outlinedStatus = OutlinedText(statusText, statusOutlines, 1);
        Grid.SetRow(lyricScroll, 1); Grid.SetRow(outlinedStatus, 2);
        root.Children.Add(controls); root.Children.Add(lyricScroll); root.Children.Add(outlinedStatus);
        Content = root;
        SystemBackdrop = transparentBackdrop;
        root.PointerPressed += DragPressed;
        root.PointerMoved += DragMoved;
        root.PointerReleased += DragReleased;
        root.PointerCaptureLost += DragReleased;
        root.PointerEntered += (_, _) => SetControlsVisibility(true);
        root.PointerExited += (_, _) => { if (dragStart is null) SetControlsVisibility(false); };
        root.ActualThemeChanged += RootThemeChanged;
        systemTheme.ColorValuesChanged += SystemThemeChanged;
        AppWindow.Closing += WindowClosing;
        AppWindow.Changed += WindowChanged;
        Activated += WindowActivated;
        hub.Changed += SnapshotChanged;
        ApplyOptions(this.options);
        RenderSnapshot();
    }

    private Button AddButton(string glyph, string title, Action action)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(8, 2, 8, 2), MinHeight = 24 };
        ToolTipService.SetToolTip(button, title);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
        button.Click += (_, _) => { if (!disposed) action(); };
        controls.Children.Add(button);
        return button;
    }

    private static Grid OutlinedText(TextBlock text, List<TextBlock> outlines, double distance)
    {
        var grid = new Grid();
        foreach (var (x, y) in new (double, double)[] { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) })
        {
            var outline = new TextBlock { TextAlignment = text.TextAlignment, TextWrapping = text.TextWrapping,
                TextTrimming = text.TextTrimming, FontSize = text.FontSize, HorizontalAlignment = text.HorizontalAlignment,
                VerticalAlignment = text.VerticalAlignment, IsHitTestVisible = false,
                RenderTransform = new TranslateTransform { X = x * distance, Y = y * distance }, Opacity = .9 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(outline, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            outlines.Add(outline); grid.Children.Add(outline);
        }
        grid.Children.Add(text);
        return grid;
    }

    private void InitializeQuickSettings()
    {
        topmostSetting.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) => QuickTopmostChanged());
        lockedSetting.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) => QuickLockChanged());
        quickSettings.Items.Add(topmostSetting); quickSettings.Items.Add(lockedSetting);
        quickSettings.Items.Add(new MenuFlyoutSeparator());
        AddQuickCommand(quickSettings, "减小字号", () => ChangeOptions(options with { FontSize = options.FontSize - 2 }));
        AddQuickCommand(quickSettings, "增大字号", () => ChangeOptions(options with { FontSize = options.FontSize + 2 }));
        var colors = new MenuFlyoutSubItem { Text = "歌词颜色" };
        ConnectColorSetting(systemColorSetting, "system"); ConnectColorSetting(lightColorSetting, "light"); ConnectColorSetting(darkColorSetting, "dark");
        colors.Items.Add(systemColorSetting); colors.Items.Add(lightColorSetting); colors.Items.Add(darkColorSetting);
        quickSettings.Items.Add(colors);
        var size = new MenuFlyoutSubItem { Text = "窗口宽度" };
        AddQuickCommand(size, "紧凑", () => ChangeOptions(options with { Width = 480 }));
        AddQuickCommand(size, "标准", () => ChangeOptions(options with { Width = 720 }));
        AddQuickCommand(size, "宽幅", () => ChangeOptions(options with { Width = 1000 }));
        quickSettings.Items.Add(size);
        quickSettings.Items.Add(new MenuFlyoutSeparator());
        AddQuickCommand(quickSettings, "隐藏桌面歌词", HideLyrics);
        quickSettings.Opening += (_, _) => UpdateQuickSettings();
        PrepareQuickItems(quickSettings.Items);
    }

    private void PrepareQuickItems(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            item.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///MonochromeResources.xaml") });
            item.Loaded += (_, _) =>
            {
                // Flyout and submenu presenters are separate popup trees; install the shared resources there too.
                for (DependencyObject? parent = item; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                {
                    if (parent is not MenuFlyoutPresenter presenter) continue;
                    presenter.RequestedTheme = root.RequestedTheme;
                    if (!presenter.Resources.MergedDictionaries.Any(dictionary => dictionary.Source?.ToString() == "ms-appx:///MonochromeResources.xaml"))
                        presenter.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///MonochromeResources.xaml") });
                    break;
                }
            };
            if (item is MenuFlyoutSubItem submenu) PrepareQuickItems(submenu.Items);
        }
    }

    private void ApplyQuickTheme(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            item.RequestedTheme = root.RequestedTheme;
            if (item is MenuFlyoutSubItem submenu) ApplyQuickTheme(submenu.Items);
        }
    }

    private void ConnectColorSetting(ToggleMenuFlyoutItem item, string theme)
    {
        item.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) =>
        {
            if (updatingMenu || disposed) return;
            if (item.IsChecked) ChangeOptions(options with { Theme = theme });
            else UpdateQuickSettings();
        });
    }

    private static void AddQuickCommand(MenuFlyout menu, string text, Action action)
    { var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private static void AddQuickCommand(MenuFlyoutSubItem menu, string text, Action action)
    { var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private void QuickTopmostChanged()
    { if (!updatingMenu && !disposed) ChangeOptions(options with { Topmost = topmostSetting.IsChecked }); }
    private void QuickLockChanged()
    { if (!updatingMenu && !disposed) ChangeOptions(options with { Locked = lockedSetting.IsChecked }); }

    private void UpdateQuickSettings()
    {
        updatingMenu = true;
        try
        {
            topmostSetting.IsChecked = options.Topmost; lockedSetting.IsChecked = options.Locked;
            systemColorSetting.IsChecked = options.Theme == "system";
            lightColorSetting.IsChecked = options.Theme == "light"; darkColorSetting.IsChecked = options.Theme == "dark";
        }
        finally { updatingMenu = false; }
    }

    /// <summary>The saved "system" preference follows the main window without overwriting the chosen color mode.</summary>
    public void SetAppTheme(ElementTheme theme)
    { if (disposed) return; appTheme = theme; if (options.Theme == "system") ApplyTheme(); }

    private void SetControlsVisibility(bool show)
    {
        controls.Visibility = show && !options.Locked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DragPressed(object sender, PointerRoutedEventArgs args)
    {
        if (options.Locked || !args.GetCurrentPoint(root).Properties.IsLeftButtonPressed) return;
        for (var target = args.OriginalSource as DependencyObject; target is not null && target != root; target = VisualTreeHelper.GetParent(target))
            if (target is ButtonBase or ScrollBar) return;
        if (!GetCursorPos(out var cursor) || !root.CapturePointer(args.Pointer)) return;
        dragStart = new PointInt32(cursor.X, cursor.Y); dragPosition = AppWindow.Position;
        args.Handled = true;
    }

    private void DragMoved(object sender, PointerRoutedEventArgs args)
    {
        if (dragStart is not PointInt32 start || dragPosition is not PointInt32 position || options.Locked || !GetCursorPos(out var cursor)) return;
        AppWindow.Move(new PointInt32(position.X + cursor.X - start.X, position.Y + cursor.Y - start.Y));
        args.Handled = true;
    }

    private void DragReleased(object sender, PointerRoutedEventArgs args)
    {
        if (dragStart is null) return;
        dragStart = null; dragPosition = null;
        root.ReleasePointerCaptures();
        RecoverPosition();
    }

    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

    public void ShowLyrics()
    {
        if (disposed) return;
        RecoverPosition();
        visible = true;
        // Hidden windows do no snapshot work; reopening always catches up before presentation.
        RenderSnapshot();
        if (options.Locked) AppWindow.Show(false);
        else Activate();
        transparentBackdrop.SuppressNativeFrame();
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
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            transparentBackdrop.SuppressNativeFrame();
            if (options.Locked)
            {
                quickSettings.Hide();
                dragStart = null; dragPosition = null; root.ReleasePointerCaptures();
            }
            root.ContextFlyout = options.Locked ? null : quickSettings;
            root.IsHitTestVisible = !options.Locked;
            transparentBackdrop.SetInputPassThrough(options.Locked);
            root.RowDefinitions[0].Height = new GridLength(options.Locked ? 0 : 30);
            SetControlsVisibility(false);
            ToolTipService.SetToolTip(topmostButton, options.Topmost ? "取消置顶" : "置顶");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(topmostButton, options.Topmost ? "取消置顶" : "置顶");
            lyricText.FontSize = options.FontSize;
            foreach (var outline in lyricOutlines) outline.FontSize = options.FontSize;
            UpdateQuickSettings();
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
    private void WindowActivated(object sender, WindowActivatedEventArgs args)
    { if (!disposed) transparentBackdrop.SuppressNativeFrame(); }

    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (disposed || applyingOptions || !(args.DidPositionChange || args.DidSizeChange)) return;
        CapturePosition();
        // Pointer moves may arrive every display frame; persist the final bounded position on release.
        if (dragStart is null) OptionsChanged?.Invoke(options);
    }

    private void CapturePosition() => options = options with
    { X = AppWindow.Position.X, Y = AppWindow.Position.Y, Width = AppWindow.Size.Width, Height = AppWindow.Size.Height };

    private void SnapshotChanged(PlaybackSnapshot _)
    {
        // Publications can arrive from a worker or in bursts. Keep one pending UI callback,
        // which reads the newest snapshot rather than replaying every stale clock tick.
        if (disposed || !visible || Interlocked.CompareExchange(ref snapshotQueued, 1, 0) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref snapshotQueued, 0);
            if (!disposed && visible) RenderSnapshot();
        }))
        { Interlocked.Exchange(ref snapshotQueued, 0); return; }
#if DEBUG
        Interlocked.Increment(ref snapshotDispatches);
#endif
    }

    private void RenderSnapshot()
    {
        if (disposed || !visible) return;
        var snapshot = hub.Current;
        var lyrics = snapshot.LyricsKind == PlaybackLyricsKind.Plain ? hub.Lyrics : null;
        if (renderedSnapshot is { } previous && previous.State == snapshot.State
            && previous.Title == snapshot.Title && previous.LyricsKind == snapshot.LyricsKind
            && previous.CurrentLine == snapshot.CurrentLine && previous.TrackGeneration == snapshot.TrackGeneration
            && renderedLocked == options.Locked && ReferenceEquals(renderedLyrics, lyrics)) return;
        renderedSnapshot = snapshot; renderedLyrics = lyrics; renderedLocked = options.Locked;
#if DEBUG
        snapshotRenders++;
#endif
        lyricScroll.VerticalScrollMode = snapshot.LyricsKind == PlaybackLyricsKind.Plain && !options.Locked
            ? ScrollMode.Enabled : ScrollMode.Disabled;
        lyricScroll.VerticalScrollBarVisibility = snapshot.LyricsKind == PlaybackLyricsKind.Plain && !options.Locked
            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        string lyricsText = snapshot.State == PlaybackStatus.Stopped ? "尚未播放" : snapshot.LyricsKind switch
        {
            PlaybackLyricsKind.Loading => "正在读取歌词…",
            PlaybackLyricsKind.Empty => "暂无歌词",
            PlaybackLyricsKind.Unavailable => "歌词暂不可用",
            PlaybackLyricsKind.Plain => ReadPlainLyrics(snapshot),
            _ => string.IsNullOrWhiteSpace(snapshot.CurrentLine) ? "等待歌词…" : snapshot.CurrentLine
        };
        if (lyricText.Text != lyricsText)
        {
            lyricText.Text = lyricsText;
            foreach (var outline in lyricOutlines) outline.Text = lyricsText;
#if DEBUG
            textUpdates += 1 + lyricOutlines.Count;
#endif
        }
        string state = snapshot.State switch
        { PlaybackStatus.Loading => "正在加载", PlaybackStatus.Playing => "播放中", PlaybackStatus.Paused => "已暂停",
            PlaybackStatus.Buffering => "缓冲中", _ => "已停止" };
        string kind = snapshot.LyricsKind == PlaybackLyricsKind.Plain ? " · 纯文本歌词（无时间轴）" : "";
        string status = string.IsNullOrWhiteSpace(snapshot.Title) ? state : $"{snapshot.Title} · {state}{kind}";
        if (options.Locked) status += " · 已锁定并穿透鼠标，在设置中解锁";
        if (statusText.Text != status)
        {
            statusText.Text = status;
            foreach (var outline in statusOutlines) outline.Text = status;
#if DEBUG
            textUpdates += 1 + statusOutlines.Count;
#endif
        }
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
            _ => appTheme != ElementTheme.Default ? appTheme :
                foreground.R + foreground.G + foreground.B > 384 ? ElementTheme.Dark : ElementTheme.Light };
        UpdateThemeColors();
    }

    private void RootThemeChanged(FrameworkElement sender, object args) => UpdateThemeColors();
    private void UpdateThemeColors()
    {
        bool dark = root.RequestedTheme == ElementTheme.Dark;
        var foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        lyricText.Foreground = statusText.Foreground = new SolidColorBrush(foreground);
        var outlineBrush = new SolidColorBrush(dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
        foreach (var outline in lyricOutlines.Concat(statusOutlines)) outline.Foreground = outlineBrush;
        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, root.RequestedTheme));
        quickSettings.MenuFlyoutPresenterStyle = presenterStyle;
        ApplyQuickTheme(quickSettings.Items);
    }

#if DEBUG
    internal int SnapshotDispatches => Volatile.Read(ref snapshotDispatches);
    internal int SnapshotRenders => snapshotRenders;
    internal int TextUpdates => textUpdates;
    internal string DisplayedLyrics => lyricText.Text;
    internal string DisplayedStatus => statusText.Text;
    internal bool HasTransparentSurface => transparentBackdrop.IsConfigured && root.Background is SolidColorBrush brush && brush.Color.A == 0;
    internal bool IsNativeInputPassThrough => transparentBackdrop.IsInputPassThrough;
    internal MenuFlyout QuickSettings => quickSettings;
    internal ElementTheme EffectiveTheme => root.RequestedTheme;
    internal bool HasContrastingOutline => lyricOutlines.Count == 8 && lyricOutlines.All(outline => outline.Text == lyricText.Text && outline.Foreground is SolidColorBrush color && lyricText.Foreground is SolidColorBrush text && color.Color != text.Color);
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
        Activated -= WindowActivated;
        OptionsChanged = null; Hidden = null;
        SystemBackdrop = null;
        transparentBackdrop.Dispose();
        Close();
    }
}
