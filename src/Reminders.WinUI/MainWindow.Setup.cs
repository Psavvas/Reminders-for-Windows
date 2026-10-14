using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        SetupGuideHeading.Text = firstRun ? "Before you sign in" : "iCloud setup and troubleshooting";
        SetupReturn.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;
        SetupReturn.Content = AuthGate.Visibility == Visibility.Visible ? "Return to sign-in" : "Return to reminders";
        SetupDemo.Visibility = firstRun ? Visibility.Visible : Visibility.Collapsed;
        SetupFailure.IsOpen = _lastSignInFailure is not null;
        SetupFailure.Title = _lastSignInFailure?.Title ?? "";
        SetupFailure.Message = _lastSignInFailure is { } failure ? failure.Reason + "\n\n" + failure.NextStep : "";
        SetupSupportActions.Visibility = _lastSignInFailure is null ? Visibility.Collapsed : Visibility.Visible;
        SetupGuideNotice.IsOpen = false;
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
        SetupStepInstructions.Text = step.Instructions;
        SetupStepNote.Text = step.Note;
        SetupStepLink.Content = step.LinkLabel;
        SetupBack.IsEnabled = _setupStep > 0;
        SetupNext.Content = _setupStep < ICloudSetupGuide.Steps.Count - 1 ? "Next" : _setupFirstRun ? "Continue to sign-in" : "Done";
        SetupGuideScroll.ChangeView(null, 0, null, true);
        SetupStepTitle.Focus(FocusState.Programmatic);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSetupCardHeight();
    private void UpdateSetupCardHeight()
    {
        if (SetupCard is not null && Root.ActualHeight > 0)
            SetupCard.MaxHeight = Math.Max(160, Root.ActualHeight - 48);
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
    }
}
