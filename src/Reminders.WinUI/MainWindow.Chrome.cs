using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private void InitializeWindowChrome()
    {
        Root.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();
        Root.Loaded += (_, _) => UpdateTitleBarTheme();
        foreach (var banner in new[] { AppInfoBar, UpdateInfoBar })
        {
            banner.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (sender, _) =>
            {
                var bar = (InfoBar)sender;
                // IsOpen hides the template's content, but does not collapse
                // the control itself. Closed banners must occupy no space.
                bar.Visibility = bar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
                if (bar.IsOpen) AnimateBanner(bar);
            });
        }
        UpdateTitleBarTheme();
    }

    private static void AnimateBanner(InfoBar banner)
    {
        if (!new UISettings().AnimationsEnabled) return;
        ElementCompositionPreview.SetIsTranslationEnabled(banner, true);
        var visual = ElementCompositionPreview.GetElementVisual(banner);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, 1), new Vector2(.3f, 1));
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(0, -20, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, easing);
        slide.Duration = TimeSpan.FromMilliseconds(220);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1, easing);
        fade.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Translation", slide);
        visual.StartAnimation("Opacity", fade);
    }

    private void UpdateTitleBarTheme()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (handle == IntPtr.Zero) return;
        var titleBar = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle)).TitleBar;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        titleBar.PreferredTheme = dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        var background = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Color.FromArgb(255, 245, 245, 245) : Color.FromArgb(255, 25, 25, 25);
        var muted = dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 100, 100, 100);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = muted;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = muted;
    }
}
