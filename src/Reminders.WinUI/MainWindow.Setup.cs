using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Reminders.Windows.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private bool _setupFirstRun;
    private int _setupStep;
    private SignInFailure? _lastSignInFailure;
    private Control? _setupReturnFocus;

    private void ShowSetupGuide(bool firstRun = false)
    {
        if (_verificationBusy || _dialogOpen) return;
        _setupFirstRun = firstRun;
        _setupStep = firstRun ? 0 : _lastSignInFailure?.GuideStep ?? 0;
        _setupReturnFocus = Root.XamlRoot is { } root
            ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as Control : null;
        // The hidden views must not be reachable by keyboard shortcuts or Tab.
        Navigation.IsEnabled = false;
        AuthGate.IsEnabled = false;
        SetupGuide.Visibility = Visibility.Visible;
        SetupGuideHeading.Text = firstRun ? "iCloud setup" : "Setup and help";
        SetupReturn.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;
        var returnLabel = AuthGate.Visibility == Visibility.Visible ? "Return to sign-in" : "Return to reminders";
        AutomationProperties.SetName(SetupReturn, returnLabel);
        ToolTipService.SetToolTip(SetupReturn, returnLabel);
        SetupFailure.IsOpen = _lastSignInFailure is not null;
        SetupFailure.Visibility = _lastSignInFailure is null ? Visibility.Collapsed : Visibility.Visible;
        SetupFailure.Title = _lastSignInFailure?.Title ?? "";
        SetupFailure.Message = _lastSignInFailure is { } failure ? failure.Reason + "\n\n" + failure.NextStep : "";
        SetupSupportActions.Visibility = _lastSignInFailure is null ? Visibility.Collapsed : Visibility.Visible;
        SetupSupportActions.IsExpanded = false;
        SetupGuideNotice.IsOpen = false;
        SetupGuideNotice.Visibility = Visibility.Collapsed;
        RenderSetupStep();
    }

    private void RenderSetupStep()
    {
        UpdateSetupCardHeight();
        var step = ICloudSetupGuide.Steps[_setupStep];
        SetupStepCount.Text = $"Step {_setupStep + 1} of {ICloudSetupGuide.Steps.Count}";
        SetupProgress.Maximum = ICloudSetupGuide.Steps.Count;
        SetupProgress.Value = _setupStep + 1;
        SetupStepIcon.Glyph = step.Glyph;
        SetupStepTitle.Text = step.Title;
        SetupStepSummary.Text = step.Summary;
        SetupStepInstructions.Text = step.QuickInstructions;
        SetupStepDetails.Text = step.Instructions;
        SetupStepNote.Text = step.Note;
        SetupStepNote.Visibility = step.ImportantNote ? Visibility.Collapsed : Visibility.Visible;
        SetupImportantNote.Message = step.Note;
        SetupImportantNote.IsOpen = step.ImportantNote;
        SetupImportantNote.Visibility = step.ImportantNote ? Visibility.Visible : Visibility.Collapsed;
        SetupDetailsHeading.Text = step.DetailsTitle;
        AutomationProperties.SetName(SetupDetails, step.DetailsTitle);
        SetupDetails.IsExpanded = false;
        SetupStepLink.Content = step.LinkLabel;
        SetupBack.IsEnabled = _setupStep > 0;
        SetupBack.Visibility = _setupStep > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetupDemo.Visibility = _setupFirstRun && _setupStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetupNext.Content = _setupStep < ICloudSetupGuide.Steps.Count - 1 ? "Next" : _setupFirstRun ? "Sign in" : "Done";
        SetupGuideScroll.ChangeView(null, 0, null, true);
        SetupStepTitle.Focus(FocusState.Programmatic);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSetupCardHeight();
    private void UpdateSetupCardHeight()
    {
        if (SetupCard is null || Root.ActualHeight <= 0) return;
        var narrow = Root.ActualWidth < 600;
        var margin = Root.ActualWidth < 480 ? 12 : 24;
        SetupCard.Margin = new Thickness(margin);
        SetupCard.Padding = new Thickness(narrow ? 20 : 28);
        SetupCard.Width = Math.Max(0, Math.Min(560, Root.ActualWidth - 2 * margin));
        SetupCard.MaxHeight = Math.Max(160, Root.ActualHeight - 2 * margin);
        if (SetupStepTitle is not null) SetupStepTitle.FontSize = narrow ? 22 : 26;
    }

    private void CloseSetupGuide()
    {
        SetupGuide.Visibility = Visibility.Collapsed;
        Navigation.IsEnabled = true;
        AuthGate.IsEnabled = true;
        if (_setupReturnFocus?.Focus(FocusState.Programmatic) != true)
        {
            if (AuthGate.Visibility == Visibility.Collapsed) SearchBox.Focus(FocusState.Programmatic);
            else if (CodeFields.Visibility == Visibility.Visible) CodeBox.Focus(FocusState.Programmatic);
            else if (LoginFields.Visibility == Visibility.Visible) AppleIdBox.Focus(FocusState.Programmatic);
        }
        _setupReturnFocus = null;
    }

    private void ReviewICloudSetup_Click(object sender, RoutedEventArgs e) => ShowSetupGuide();
    private void RestartSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_verificationBusy) return;
        CodeBox.Text = "";
        PasswordBox.Password = "";
        GateError.IsOpen = false;
        ShowLogin();
    }
    private void SetupBack_Click(object sender, RoutedEventArgs e) { if (_setupStep > 0) { _setupStep--; RenderSetupStep(); } }
    private void SetupReturn_Click(object sender, RoutedEventArgs e) => CloseSetupGuide();

    private async void SetupNext_Click(object sender, RoutedEventArgs e)
    {
        if (_setupStep < ICloudSetupGuide.Steps.Count - 1) { _setupStep++; RenderSetupStep(); return; }
        var firstRun = _setupFirstRun;
        CloseSetupGuide();
        if (!firstRun) return;
        _uiPreferences.ICloudSetupCompleted = true;
        SaveUiPreferences();
        await StartAccountAsync();
    }

    private void SetupDemo_Click(object sender, RoutedEventArgs e) { CloseSetupGuide(); LoadDemo(); }

    private async void SetupStepLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri(ICloudSetupGuide.Steps[_setupStep].Link)))
                ShowSetupNotice("Open your browser", ICloudSetupGuide.Steps[_setupStep].Link);
        }
        catch { ShowSetupNotice("Couldn't open the browser", "Open this address in your browser: " + ICloudSetupGuide.Steps[_setupStep].Link); }
    }

    private void ShowSignInError(Exception error, SignInStage stage)
    {
        if (stage == SignInStage.Password && Reminders.Core.DemoAccount.IsDemoUsername(AppleIdBox.Text))
        {
            _lastSignInFailure = null;
            GateError.Title = "Couldn't sign in";
            GateError.Message = error.Message;
            GateError.IsOpen = true;
            return;
        }
        var syncError = error as SyncException;
        var code = error is OperationCanceledException ? "NETWORK" : syncError?.Code;
        var message = error is OperationCanceledException ? "The iCloud request timed out. Please try again." : error.Message;
        _lastSignInFailure = SignInTroubleshooting.Explain(code, message, syncError?.Detail, stage);
        GateError.Title = _lastSignInFailure.Title;
        GateError.Message = _lastSignInFailure.Reason + "\n\n" + _lastSignInFailure.NextStep;
        GateError.IsOpen = true;
    }

    private void CopySignInError_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSignInFailure is not { } failure) return;
        try
        {
            var data = new DataPackage();
            data.SetText($"Reminders for Windows {AppUpdater.CurrentVersion.ToString(3)}\n{failure.Title}\n{failure.Reason}\n{failure.NextStep}");
            Clipboard.SetContent(data);
            ShowSetupNotice("Error copied", "Paste it into your support message. You can also attach app.log from the diagnostic-log folder.");
        }
        catch { ShowSetupNotice("Couldn't copy the error", "Select and copy the error message, or use app.log from the diagnostic-log folder."); }
    }

    private async void OpenAuthLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLog.FolderPath);
            if (!await global::Windows.System.Launcher.LaunchFolderPathAsync(AppLog.FolderPath))
                ShowSetupNotice("Log folder", AppLog.FolderPath);
        }
        catch { ShowSetupNotice("Log folder", "Open this folder in File Explorer: " + AppLog.FolderPath); }
    }

    private void ShowSetupNotice(string title, string message)
    {
        SetupGuideNotice.Title = title;
        SetupGuideNotice.Message = message;
        SetupGuideNotice.IsOpen = true;
        SetupGuideNotice.Visibility = Visibility.Visible;
    }

    private void SetupGuideNotice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => SetupGuideNotice.Visibility = Visibility.Collapsed;
}
