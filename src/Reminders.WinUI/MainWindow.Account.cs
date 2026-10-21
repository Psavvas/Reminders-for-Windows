using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private async Task SignOutAsync()
    {
        if (_accountBusy || _dialogOpen || _updateBusy) return;
        if (_selected is not null && _detailDirty)
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
        _accountBusy = true;
        _searchTimer.Stop(); _statusTimer.Stop(); _notificationTimer.Stop(); _syncTimer.Stop();
        SetGateState("Signing out…");
        try
        {
            // Local/sample accounts do not own the saved iCloud credentials.
            if (!_demo && !_sync.IsLocalDemo) await _sync.CallAsync("sign_out", new { });
            await _sync.DisposeAsync();
            _demo = false;
            ClearAccountView();
            ShowLogin(); GateStatus.Text = "You're signed out";
        }
        catch (Exception error)
        {
            _accountBusy = false;
            if (_sync.IsRunning)
            {
                await BootAsync();
                ShowInfo("Couldn't sign out", error.Message, InfoBarSeverity.Error);
            }
            else { ShowLogin(); GateError.Message = error.Message; GateError.IsOpen = true; }
        }
        finally { _accountBusy = false; }
    }

    private void ClearAccountView()
    {
        ShowDetail(null); _lists.Clear(); _tags.Clear(); _demoRows.Clear();
        DetailListName.Text = "";
        _view = new("Today", "", NavKind.Smart, "today"); SetReminderRows([]);
        ViewTitle.Text = "Today"; ViewSubtitle.Text = ""; SearchBox.Text = ""; RowCount.Text = "";
        _searchTimer.Stop();
        AppleIdBox.Text = ""; PasswordBox.Password = ""; CodeBox.Text = "";
        _lastSignInFailure = null; _lastSyncRequest = DateTimeOffset.MinValue;
        ConflictItem.Visibility = Visibility.Collapsed; AppInfoBar.IsOpen = false; GateError.IsOpen = false;
        _uiPreferences = Services.UiPreferences.Load();
        SetPanePinned(_uiPreferences.NavigationPaneOpen, persist: false);
        RebuildListNavigation(); RestoreNavigationSelection(); UpdatePaneLayout();
    }

    private async Task ClearCacheAsync()
    {
        if (_accountBusy || _dialogOpen || _updateBusy || _demo || _sync.IsLocalDemo) return;
        _dialogOpen = true;
        var confirmed = false;
        var discardPending = false;
        try
        {
            if (!_sync.IsRunning) await _sync.StartAsync();
            var status = await _sync.CallAsync("sync_status", new { });
            var pending = status.Number("pending_pushes") + status.Number("conflicts");
            var panel = new StackPanel { Spacing = 14, MaxWidth = 460 };
            panel.Children.Add(new TextBlock { Text = "Remove downloaded reminders and sync history from this PC, then download a fresh copy from iCloud. Your sign-in and app preferences are kept.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "Reminders already saved in iCloud will remain there. Changes saved only on this PC will be lost.", TextWrapping = TextWrapping.Wrap });
            if (_selected is not null && _detailDirty)
                panel.Children.Add(new TextBlock { Text = "The unsaved edits in the open reminder will also be discarded.", TextWrapping = TextWrapping.Wrap });
            var discard = new CheckBox { Content = "Discard unsynced changes and conflicts", IsChecked = false, Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed };
            if (pending > 0)
                panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = $"{pending} unsynced change{(pending == 1 ? "" : "s")} or conflict{(pending == 1 ? "" : "s")}", Message = "These will be discarded. Clear the cache only if you no longer need them or iCloud keeps refusing them." });
            panel.Children.Add(discard);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = "Clear app cache?", Content = panel,
                PrimaryButtonText = "Clear cache", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = pending == 0 };
            discard.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            discard.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = pending == 0;
            confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
            discardPending = discard.IsChecked == true;
        }
        catch (Exception error) { ShowInfo("Couldn't clear cache", error.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
        if (!confirmed) return;
        _accountBusy = true;
        _searchTimer.Stop(); _statusTimer.Stop(); _notificationTimer.Stop(); _syncTimer.Stop();
        SetGateState("Clearing the app cache…");
        try
        {
            await _sync.CallAsync("clear_cache", new { discard_pending = discardPending });
            ClearAccountView();
            _accountBusy = false;
            await BootAsync();
            if (AuthGate.Visibility == Visibility.Collapsed)
                ShowInfo("Cache cleared", "Downloading a fresh copy of your iCloud reminders…", InfoBarSeverity.Informational);
            else if (LoginFields.Visibility == Visibility.Visible) GateStatus.Text = "Cache cleared. Sign in to download your reminders.";
        }
        catch (Exception error)
        {
            _accountBusy = false;
            await BootAsync();
            ShowInfo("Couldn't clear cache", error.Message, InfoBarSeverity.Error);
            if (AuthGate.Visibility != Visibility.Collapsed) { GateError.Message = error.Message; GateError.IsOpen = true; }
        }
        finally { _accountBusy = false; }
    }
}
