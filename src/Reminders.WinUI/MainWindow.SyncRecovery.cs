using Reminders.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private bool IsSyncRecoveryPreview
    {
        get
        {
#if DEBUG
            return _demo && Environment.GetCommandLineArgs().Contains("--preview-unsynced", StringComparer.OrdinalIgnoreCase);
#else
            return false;
#endif
        }
    }
    private Task<List<UnsyncedReminder>> ReadUnsyncedReview(CancellationToken token)
    {
#if DEBUG
        if (IsSyncRecoveryPreview) return Task.FromResult(new List<UnsyncedReminder>
        {
            new() { Id = "preview/new", Title = "Finish the CAD assembly", IsNew = true, PendingChanges = 1, Version = "preview" },
            new() { Id = "preview/conflict", Title = "Review the flight planning checklist", Conflicts = 1, Version = "preview" },
            new() { Id = "preview/edits", Title = "Engineering project: a longer reminder title that should wrap cleanly in a narrow settings window", PendingChanges = 2, Version = "preview" }
        });
#endif
        return _sync.CallAsync<List<UnsyncedReminder>>("unsynced_reminders", new { }, token);
    }

    private sealed class SyncRecoveryReview
    {
        public bool CanDiscard { get; set; } = true;
        public Grid View { get; } = new() { RowSpacing = 12 };
        public InfoBar Notice { get; } = new() { IsClosable = false, IsOpen = false };
        public Button Refresh { get; } = SettingsAction(L10n.Get("RefreshList"));
        private readonly ListView tasks;
        private readonly Button selectAll = SettingsAction(L10n.Get("SelectAll"));
        private readonly Button clear = SettingsAction(L10n.Get("ClearSelection"));
        public IReadOnlyList<UnsyncedReminder> Selected => tasks.SelectedItems.OfType<UnsyncedReminder>().ToArray();

        public SyncRecoveryReview(List<UnsyncedReminder> rows, Action selectionChanged, bool unsavedEditor)
        {
            View.RowDefinitions.Add(new() { Height = GridLength.Auto });
            View.RowDefinitions.Add(new()); View.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var header = new StackPanel { Spacing = 10 };
            header.Children.Add(SettingsDescription(L10n.Get("DiscardChangesReviewInstructions")));
            if (unsavedEditor) header.Children.Add(SettingsDescription(L10n.Get("DiscardChangesOpenEditorWarning")));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(selectAll); buttons.Children.Add(clear); header.Children.Add(buttons);
            tasks = new ListView { ItemsSource = rows, SelectionMode = ListViewSelectionMode.Multiple,
                IsMultiSelectCheckBoxEnabled = true, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var rowStyle = new Style { TargetType = typeof(ListViewItem) };
            rowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            tasks.ItemContainerStyle = rowStyle;
            AutomationProperties.SetName(tasks, L10n.Get("UnsyncedRemindersToDiscard"));
            ScrollViewer.SetHorizontalScrollBarVisibility(tasks, ScrollBarVisibility.Disabled);
            tasks.ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <StackPanel Spacing="4" Margin="4,8">
                    <TextBlock Text="{Binding DisplayTitle}" TextWrapping="Wrap" FontWeight="SemiBold"/>
                    <TextBlock Text="{Binding Summary}" TextWrapping="Wrap" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                  </StackPanel>
                </DataTemplate>
                """);
            var footer = new StackPanel { Spacing = 8 }; footer.Children.Add(Notice); footer.Children.Add(Refresh);
            Grid.SetRow(tasks, 1); Grid.SetRow(footer, 2);
            View.Children.Add(header); View.Children.Add(tasks); View.Children.Add(footer);
            selectAll.Click += (_, _) => tasks.SelectAll(); clear.Click += (_, _) => tasks.SelectedItems.Clear();
            tasks.SelectionChanged += (_, _) => selectionChanged();
        }

        public void ClearSelection() => tasks.SelectedItems.Clear();

        public void SetBusy(bool busy)
        {
            tasks.IsEnabled = !busy; selectAll.IsEnabled = !busy; clear.IsEnabled = !busy; Refresh.IsEnabled = !busy;
            if (busy) { Notice.Severity = InfoBarSeverity.Informational; Notice.Title = L10n.Get("RestoringSelectedReminders"); Notice.Message = L10n.Get("DiscardChangesCheckingICloud"); Notice.IsOpen = true; }
        }
    }
}
