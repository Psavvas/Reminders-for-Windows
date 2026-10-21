using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Reminders.Windows.Services;
using Windows.System;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private bool _updatingLayout;
    private double? _dragOriginalFraction;
    private ReminderItem? _dragOriginalSelection;

    private void ReminderLayout_SizeChanged(object sender, SizeChangedEventArgs args) => UpdatePaneLayout();

    private void UpdatePaneLayout()
    {
        if (_updatingLayout || DetailsHost is null || ReminderLayout.ActualWidth <= 0) return;
        _updatingLayout = true;
        try
        {
            var sizes = PaneSizing.Calculate(ReminderLayout.ActualWidth, _uiPreferences.DetailPaneFraction, _selected is not null);
            // Star columns can shrink during measure; fixed pixel columns keep the
            // parent's desired width too large to deliver a smaller SizeChanged.
            ReminderColumn.Width = sizes.List == 0 ? new GridLength(0) : new GridLength(sizes.Compact ? 1 : sizes.List, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(sizes.Compact ? 0 : PaneSizing.DividerWidth);
            DetailsColumn.Width = new GridLength(sizes.Compact ? 0 : sizes.Details, sizes.Compact || sizes.Details == 0 ? GridUnitType.Pixel : GridUnitType.Star);
            Grid.SetColumn(DetailsHost, sizes.Compact ? 0 : 2);
            ReminderHost.MaxWidth = sizes.List;
            DetailsHost.MaxWidth = sizes.Details;
            ReminderHost.Visibility = (sizes.Compact ? _selected is null : sizes.List > 0) ? Visibility.Visible : Visibility.Collapsed;
            DetailsHost.Visibility = (sizes.Compact ? _selected is not null : sizes.Details > 0) ? Visibility.Visible : Visibility.Collapsed;
            PaneDivider.Visibility = sizes.Compact ? Visibility.Collapsed : Visibility.Visible;
            BackToReminders.Visibility = sizes.Compact ? Visibility.Visible : Visibility.Collapsed;
            AddLabel.Visibility = sizes.List < 400 ? Visibility.Collapsed : Visibility.Visible;
            var stacked = sizes.Details < 420;
            ArrangeDetailPair(DetailListPriority, DetailPriority, stacked);
            ArrangeDetailPair(DetailDateTime, DetailTime, stacked);
            ArrangeDetailPair(DetailRecurrence, DetailRepeatInterval, stacked);
            ArrangeDetailPair(DetailToggles, DetailFlagged, stacked);
        }
        finally { _updatingLayout = false; }
    }

    private static void ArrangeDetailPair(Grid grid, FrameworkElement second, bool stacked)
    {
        grid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 1, stacked ? GridUnitType.Pixel : GridUnitType.Star);
        grid.RowSpacing = stacked ? 10 : 0;
        Grid.SetColumn(second, stacked ? 0 : 1);
        Grid.SetRow(second, stacked ? 1 : 0);
    }

    private void ResizeDetails(double delta)
    {
        var previousFraction = _uiPreferences.DetailPaneFraction;
        _uiPreferences.DetailPaneFraction = PaneSizing.Resize(ReminderLayout.ActualWidth, _uiPreferences.DetailPaneFraction, delta);
        if (_uiPreferences.DetailPaneFraction == 0 && previousFraction != 0)
        {
            ReminderList.SelectedItem = null;
            ShowDetail(null);
        }
        UpdatePaneLayout();
    }

    private void PaneDivider_DragStarted(object sender, DragStartedEventArgs args)
    {
        _dragOriginalFraction = _uiPreferences.DetailPaneFraction;
        _dragOriginalSelection = _selected;
    }
    private void PaneDivider_DragDelta(object sender, DragDeltaEventArgs args) => ResizeDetails(args.HorizontalChange);
    private void PaneDivider_DragCompleted(object sender, DragCompletedEventArgs args)
    {
        if (args.Canceled)
        {
            _uiPreferences.DetailPaneFraction = _dragOriginalFraction;
            ReminderList.SelectedItem = _dragOriginalSelection;
            ShowDetail(_dragOriginalSelection);
        }
        else SaveUiPreferences();
        _dragOriginalSelection = null;
    }

    private void PaneDivider_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Left or VirtualKey.Right)
        {
            ResizeDetails(args.Key == VirtualKey.Left ? -24 : 24);
            SaveUiPreferences(); args.Handled = true;
        }
        else if (args.Key == VirtualKey.Home) { UseAutomaticPaneSizing(); args.Handled = true; }
    }

    private void UseAutomaticPaneSizing()
    {
        _uiPreferences.DetailPaneFraction = null;
        SaveUiPreferences(); UpdatePaneLayout();
    }
    private void AutomaticPaneSizing_Click(object sender, RoutedEventArgs args) => UseAutomaticPaneSizing();
    private void PaneDivider_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args) { UseAutomaticPaneSizing(); args.Handled = true; }
    private void BackToReminders_Click(object sender, RoutedEventArgs args) { ReminderList.SelectedItem = null; ShowDetail(null); }
}
