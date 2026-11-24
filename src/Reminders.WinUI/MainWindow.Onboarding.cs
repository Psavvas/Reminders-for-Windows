using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private int _tourStep = -1;
    private int? _nextTourStep;
    private Control? _tourReturnFocus;
    private bool _firstTourPending;
    private int _accountLoads;
    private bool AppTourActive => _tourStep >= 0;
    private const int TourStepCount = 6;

    private void ShowWelcome()
    {
        Navigation.IsEnabled = false;
        AuthGate.IsEnabled = false;
        WelcomeScreen.Visibility = Visibility.Visible;
        UpdateWelcomeLayout();
        WelcomeContinue.Focus(FocusState.Programmatic);
    }

    private void CloseWelcome()
    {
        WelcomeScreen.Visibility = Visibility.Collapsed;
        Navigation.IsEnabled = true;
        AuthGate.IsEnabled = true;
    }

    private void WelcomeContinue_Click(object sender, RoutedEventArgs e)
    {
        _uiPreferences.WelcomeCompleted = true;
        SaveUiPreferences();
        CloseWelcome();
        ShowSetupGuide(firstRun: true);
    }

    private void WelcomeDemo_Click(object sender, RoutedEventArgs e)
    {
        // Exploring sample data must not complete setup or the real account's tour.
        CloseWelcome();
        LoadDemo();
        ShowAppTour();
    }

    private void UpdateWelcomeLayout()
    {
        if (WelcomeCard is null || Root.ActualWidth <= 0) return;
        var narrow = Root.ActualWidth < 600;
        var margin = Root.ActualWidth < 480 ? 12 : 24;
        WelcomeCard.Margin = new Thickness(margin);
        WelcomeCard.Padding = new Thickness(narrow ? 20 : 28);
        WelcomeCard.Width = Math.Max(0, Math.Min(560, Root.ActualWidth - 2 * margin));
        WelcomeCard.MaxHeight = Math.Max(160, Root.ActualHeight - 2 * margin);
        WelcomeHeading.FontSize = narrow ? 24 : 28;
    }

    private void OfferFirstAppTour(bool authenticated)
    {
        _firstTourPending = _uiPreferences.ShouldOfferAppTour(authenticated);
        TryShowFirstAppTour();
    }

    private void TryShowFirstAppTour()
    {
        if (!_firstTourPending || _windowClosed) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_firstTourPending || _uiPreferences.AppTourCompleted || _windowClosed || AppTourActive
                || _dialogOpen || _verificationBusy || _accountBusy || _accountLoads > 0 || AuthGate.Visibility != Visibility.Collapsed
                || SetupGuide.Visibility == Visibility.Visible || WelcomeScreen.Visibility == Visibility.Visible) return;
            _firstTourPending = false;
            ShowAppTour();
        });
    }

    private void ShowAppTour()
    {
        if (AppTourActive || _windowClosed || _dialogOpen || _accountBusy || _verificationBusy
            || AuthGate.Visibility != Visibility.Collapsed || SetupGuide.Visibility == Visibility.Visible
            || WelcomeScreen.Visibility == Visibility.Visible) return;
        _tourReturnFocus = Root.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
        _tourStep = 0;
        RenderTourStep();
    }

    private void RenderTourStep()
    {
        if (!AppTourActive || _windowClosed) return;
        var key = new[] { "TourNavigation", "TourSearch", "TourAdd", "TourDetails", "TourComplete", "TourSync" }[_tourStep];
        var target = _tourStep switch
        {
            0 => IsAppleStyle ? ApplePaneButton : Navigation.IsPaneOpen
                ? (FrameworkElement?)Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault() ?? ViewTitle : ViewTitle,
            1 => IsAppleStyle ? AppleSearchButton : SearchBox,
            2 => AddButton,
            3 or 4 => ReminderHost.Visibility == Visibility.Visible ? (FrameworkElement)ReminderList : BackToReminders,
            _ => IsAppleStyle && AppleSidebar.Visibility == Visibility.Visible ? AppleSyncStatus : ViewTitle
        };
        AppTourTip.Target = target;
        AppTourTip.Title = L10n.Get(key + "Title");
        TourDescription.Text = L10n.Get(_tourStep == TourStepCount - 1 && (_demo || _sync.IsLocalDemo)
            ? "TourSyncDemoDescription" : key + "Description");
        TourHint.Text = L10n.Get(key + "Hint");
        TourStepLabel.Text = L10n.Format("TourStepCount", _tourStep + 1, TourStepCount);
        TourProgress.Value = _tourStep + 1;
        TourBack.IsEnabled = _tourStep > 0;
        AppTourTip.ActionButtonContent = L10n.Get(_tourStep == TourStepCount - 1 ? "TourFinish" : "Next");
        AppTourTip.CloseButtonContent = L10n.Get("TourSkip");
        AppTourTip.IsOpen = true;
        TourDescription.Focus(FocusState.Programmatic);
    }

    private void MoveTour(int step)
    {
        _nextTourStep = step;
        // Reopen after Closed so WinUI measures the tip against its new target.
        AppTourTip.IsOpen = false;
    }

    private void TourNext_Click(TeachingTip sender, object args)
    {
        if (_tourStep < TourStepCount - 1) MoveTour(_tourStep + 1);
        else EndAppTour(remember: true);
    }

    private void TourBack_Click(object sender, RoutedEventArgs e)
    {
        if (_tourStep > 0) MoveTour(_tourStep - 1);
    }

    private void TourSkip_Click(TeachingTip sender, object args) => EndAppTour(remember: true);

    private void TourTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
    {
        if (!AppTourActive) return;
        if (_nextTourStep is { } next)
        {
            _nextTourStep = null;
            _tourStep = next;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RenderTourStep);
        }
        else EndAppTour(remember: true); // Includes keyboard dismissal with Escape.
    }

    private void EndAppTour(bool remember)
    {
        _firstTourPending = false;
        if (!AppTourActive) return;
        _tourStep = -1;
        _nextTourStep = null;
        AppTourTip.IsOpen = false;
        if (remember)
        {
            _uiPreferences.AppTourCompleted = true;
            SaveUiPreferences();
        }
        if (!_windowClosed && AuthGate.Visibility == Visibility.Collapsed)
        {
            if (_tourReturnFocus?.Focus(FocusState.Programmatic) != true) AddButton.Focus(FocusState.Programmatic);
        }
        _tourReturnFocus = null;
    }
}
