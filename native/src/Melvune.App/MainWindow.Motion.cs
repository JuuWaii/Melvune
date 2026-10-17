using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private sealed record UiMotion(Action<bool> Finish);
    private readonly Dictionary<FrameworkElement, UiMotion> uiMotions = [];
#if DEBUG
    private bool? motionPreferenceOverride;
    private readonly List<string> motionUnloadSamples = [];
#endif
    private bool UiMotionEnabled => !closed &&
#if DEBUG
        (motionPreferenceOverride ??
#endif
        systemTheme.AnimationsEnabled
#if DEBUG
        )
#endif
        ;

    private Task AnimateUiAsync(FrameworkElement element, double fromY, double toY, int milliseconds,
        bool fade = false, bool bounce = false, bool horizontal = false)
    {
        if (closed) return Task.CompletedTask;
        if (uiMotions.TryGetValue(element, out var old)) old.Finish(false);
        var transform = FindUiMotionTransform(element);
        if (transform is null)
        {
            transform = new CompositeTransform();
            if (element.RenderTransform is { } existing)
            {
                var group = new TransformGroup();
                group.Children.Add(existing); group.Children.Add(transform);
                element.RenderTransform = group;
            }
            else element.RenderTransform = transform;
        }
        element.RenderTransformOrigin = new global::Windows.Foundation.Point(.5, .5);
        string axis = horizontal ? nameof(CompositeTransform.TranslateX) : nameof(CompositeTransform.TranslateY);
        void SetPosition(double value) { if (horizontal) transform.TranslateX = value; else transform.TranslateY = value; }
        if (!UiMotionEnabled || element.Visibility != Visibility.Visible)
        {
            SetPosition(toY); transform.ScaleX = transform.ScaleY = 1; element.Opacity = 1;
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPosition(fromY);
        transform.ScaleX = transform.ScaleY = bounce ? .94 : 1;
        element.Opacity = fade ? .25 : 1;
        var storyboard = new Storyboard();
        var watchdog = DispatcherQueue.CreateTimer();
        watchdog.IsRepeating = false; watchdog.Interval = TimeSpan.FromMilliseconds(milliseconds + 200);
        bool finished = false;
        EasingFunctionBase ease = bounce ? new BackEase { Amplitude = .25, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        void Add(DependencyObject target, string property, double from, double to, int duration, EasingFunctionBase easing)
        {
            var animation = new DoubleAnimation { From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(duration)), EasingFunction = easing };
            Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
        Add(transform, axis, fromY, toY, milliseconds, ease);
        if (fade) Add(element, nameof(UIElement.Opacity), .25, 1, Math.Min(milliseconds, 160), new CubicEase { EasingMode = EasingMode.EaseOut });
        if (bounce)
        {
            Add(transform, nameof(CompositeTransform.ScaleX), .94, 1, milliseconds, ease);
            Add(transform, nameof(CompositeTransform.ScaleY), .94, 1, milliseconds, ease);
        }
        void Finish(bool settle)
        {
            if (finished) return;
            finished = true;
            double y = horizontal ? transform.TranslateX : transform.TranslateY, xScale = transform.ScaleX, yScale = transform.ScaleY, opacity = element.Opacity;
            watchdog.Stop(); storyboard.Completed -= Completed; element.Unloaded -= Unloaded;
            storyboard.Stop();
            SetPosition(settle ? toY : y);
            transform.ScaleX = settle ? 1 : xScale; transform.ScaleY = settle ? 1 : yScale;
            element.Opacity = settle ? 1 : opacity;
            uiMotions.Remove(element);
            completion.TrySetResult();
        }
        void Completed(object? sender, object args) => Finish(true);
        void Unloaded(object sender, RoutedEventArgs args)
        {
#if DEBUG
            motionUnloadSamples.Add(element.Name);
#endif
            Finish(true);
        }
        storyboard.Completed += Completed; element.Unloaded += Unloaded;
        watchdog.Tick += (_, _) => Finish(true);
        uiMotions[element] = new UiMotion(Finish);
        try { storyboard.Begin(); watchdog.Start(); }
        catch (Exception) { Finish(true); }
        return completion.Task;
    }
    private static CompositeTransform? FindUiMotionTransform(FrameworkElement element) => element.RenderTransform as CompositeTransform
        ?? (element.RenderTransform as TransformGroup)?.Children.LastOrDefault() as CompositeTransform;
    private void AnimateViewEntry(FrameworkElement element) => _ = AnimateUiAsync(element, -18, 0, 180, fade: true);
    private void StopAllUiMotions()
    {
        foreach (var motion in uiMotions.Values.ToArray()) motion.Finish(true);
    }
    private void SystemMotionChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
        => DispatcherQueue.TryEnqueue(() => { if (!closed && !UiMotionEnabled) StopAllUiMotions(); });
}
