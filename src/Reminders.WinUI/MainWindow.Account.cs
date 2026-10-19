using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private async Task SignOutAsync()
    {
        if (_signingOut || _dialogOpen || _updateBusy || AuthGate.Visibility != Visibility.Collapsed) return;
        if (_selected is not null && SaveButton.IsEnabled)
        {
            _dialogOpen = true;
            try
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, Title = "Sign out without saving?",
                    Content = "The changes in the open reminder haven't been saved.",
                    PrimaryButtonText = "Sign out", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            }
            finally { _dialogOpen = false; }
        }
        _signingOut = true;
        _searchTimer.Stop(); _statusTimer.Stop(); _notificationTimer.Stop(); _syncTimer.Stop();
        SetGateState("Signing out…");
        try
        {
            // Local/sample accounts do not own the saved iCloud credentials.
            if (!_demo && !_sync.IsLocalDemo) await _sync.CallAsync("sign_out", new { });
            await _sync.DisposeAsync();
            _demo = false;
            ShowDetail(null); _lists.Clear(); _tags.Clear(); _demoRows.Clear();
            DetailList.ItemsSource = null;
            _view = new("Today", "", NavKind.Smart, "today");
            SetReminderRows([]);
            ViewTitle.Text = "Today"; ViewSubtitle.Text = ""; SearchBox.Text = ""; RowCount.Text = "";
            _searchTimer.Stop();
            AppleIdBox.Text = ""; PasswordBox.Password = ""; CodeBox.Text = "";
            _lastSignInFailure = null; _lastSyncRequest = DateTimeOffset.MinValue;
            ConflictItem.Visibility = Visibility.Collapsed; AppInfoBar.IsOpen = false; GateError.IsOpen = false;
            _uiPreferences = Services.UiPreferences.Load();
            SetPanePinned(_uiPreferences.NavigationPaneOpen, persist: false);
            RebuildListNavigation(); RestoreNavigationSelection(); UpdatePaneLayout();
            ShowLogin(); GateStatus.Text = "You're signed out";
        }
        catch (Exception error)
        {
            _signingOut = false;
            if (_sync.IsRunning)
            {
                await BootAsync();
                ShowInfo("Couldn't sign out", error.Message, InfoBarSeverity.Error);
            }
            else { ShowLogin(); GateError.Message = error.Message; GateError.IsOpen = true; }
        }
        finally { _signingOut = false; }
    }
}
