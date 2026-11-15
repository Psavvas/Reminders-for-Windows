using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private async Task SignOutAsync()
    {
        if (_accountBusy || _dialogOpen || _updateBusy || _savingDetail) return;
        if (_selected is not null && _detailDirty)
        {
            _dialogOpen = true;
            try
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, Title = L10n.Get("SignOutWithoutSaving"),
                    Content = L10n.Get("SignOutUnsavedChangesWarning"),
                    PrimaryButtonText = L10n.Get("SignOut"), CloseButtonText = L10n.Get("Cancel"), DefaultButton = ContentDialogButton.Close
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            }
            finally { _dialogOpen = false; }
        }
        _accountBusy = true;
        _searchTimer.Stop(); _statusTimer.Stop(); _notificationTimer.Stop(); _syncTimer.Stop();
        SetGateState(L10n.Get("SigningOut"));
        try
        {
            // Local/sample accounts do not own the saved iCloud credentials.
            if (!_demo && !_sync.IsLocalDemo) await _sync.CallAsync("sign_out", new { });
            await _sync.DisposeAsync();
            _demo = false;
            ClearAccountView();
            ShowLogin(); GateStatus.Text = L10n.Get("YoureSignedOut");
        }
        catch (Exception error)
        {
            _accountBusy = false;
            if (_sync.IsRunning)
            {
                await BootAsync();
                ShowInfo(L10n.Get("CouldntSignOut"), L10n.Error(error), InfoBarSeverity.Error);
            }
            else { ShowLogin(); GateError.Message = L10n.Error(error); GateError.IsOpen = true; }
        }
        finally { _accountBusy = false; }
    }

    private void ClearAccountView()
    {
        _accountEpoch++; _reminderLoads.Invalidate(); _navigationLoads.Invalidate();
        ShowDetail(null); _lists.Clear(); _tags.Clear(); _demoRows.Clear(); _collapsedSubtasks.Clear();
        DetailListName.Text = "";
        _view = new(L10n.Get("Today"), "", NavKind.Smart, "today"); SetReminderRows([]);
        ViewTitle.Text = L10n.Get("Today"); ViewSubtitle.Text = ""; SearchBox.Text = ""; RowCount.Text = "";
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
            panel.Children.Add(new TextBlock { Text = L10n.Get("ClearCacheDescription"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = L10n.Get("ClearCacheLocalChangesWarning"), TextWrapping = TextWrapping.Wrap });
            if (_selected is not null && _detailDirty)
                panel.Children.Add(new TextBlock { Text = L10n.Get("ClearCacheUnsavedEditorWarning"), TextWrapping = TextWrapping.Wrap });
            var discard = new CheckBox { Content = L10n.Get("DiscardUnsyncedChangesAndConflicts"), IsChecked = false, Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed };
            if (pending > 0)
                panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = L10n.Format(pending == 1 ? "PendingChangesOrConflictOne" : "PendingChangesOrConflictMany", pending), Message = L10n.Get("ClearCachePendingChangesDescription") });
            panel.Children.Add(discard);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = L10n.Get("ClearAppCache"), Content = panel,
                PrimaryButtonText = L10n.Get("ClearCache"), CloseButtonText = L10n.Get("Cancel"), DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = pending == 0 };
            discard.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            discard.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = pending == 0;
            confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
            discardPending = discard.IsChecked == true;
        }
        catch (Exception error) { ShowInfo(L10n.Get("CouldntClearCache"), L10n.Error(error), InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
        if (!confirmed) return;
        _accountBusy = true;
        _searchTimer.Stop(); _statusTimer.Stop(); _notificationTimer.Stop(); _syncTimer.Stop();
        SetGateState(L10n.Get("ClearingTheAppCache"));
        try
        {
            await _sync.CallAsync("clear_cache", new { discard_pending = discardPending });
            ClearAccountView();
            _accountBusy = false;
            await BootAsync();
            if (AuthGate.Visibility == Visibility.Collapsed)
                ShowInfo(L10n.Get("CacheCleared"), L10n.Get("CacheRefreshInProgress"), InfoBarSeverity.Informational);
            else if (LoginFields.Visibility == Visibility.Visible) GateStatus.Text = L10n.Get("CacheClearedSignInPrompt");
        }
        catch (Exception error)
        {
            _accountBusy = false;
            await BootAsync();
            ShowInfo(L10n.Get("CouldntClearCache"), L10n.Error(error), InfoBarSeverity.Error);
            if (AuthGate.Visibility != Visibility.Collapsed) { GateError.Message = L10n.Error(error); GateError.IsOpen = true; }
        }
        finally { _accountBusy = false; }
    }
}
