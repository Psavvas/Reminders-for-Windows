# Native Windows frontend

The desktop client is a C# WinUI 3 application using the Windows App SDK. Its
shell is composed from Windows controls including `NavigationView`, `InfoBar`,
`CommandBar`, `ListView`, `ContentDialog`, `CalendarDatePicker`, `TimePicker`,
`ToggleSwitch`, `InfoBadge`, and the platform theme resources.

The reminder interface uses native Windows controls. Apple-device sign-in opens
Apple's own page in an isolated Microsoft Edge WebView2 window and hands the
verified session back to the native app. The WebView2 Runtime is needed for that
sign-in route; native SMS sign-in remains available.

## Integrated C# backend

Reminders.Core runs in the WinUI process. SyncClient makes asynchronous calls
and forwards authentication, sync and conflict events onto the UI dispatcher.
SQLite and authentication work run off the UI thread. The service cancels outstanding
work before disposing its cache and HTTP client.

The SQLite schema and Windows Credential Manager target names remain compatible.
There is no executable discovery, child process or JSON pipe. Unpackaged data is
under %LOCALAPPDATA%\RemindersSync; Windows may virtualize it for an MSIX app.

## Demo mode

`--demo` is a first-class, side-effect-free UI testing mode. It does not start
the backend or access the real cache. Sample lists and reminders live only in
memory. The sign-in screen exposes the same path through an **Explore the UI
with sample data** button.

## Reminder and sidebar controls

Select a task, choose its destination in the details pane's **List** picker,
then click **Save changes** to move it. The picker follows sidebar list order
and excludes groups. Moves are saved locally and queued for iCloud when offline;
the task leaves its previous list as soon as it is saved. Demo mode supports
the same interaction with its sample lists.

Only Today shows a count badge. Today includes unfinished reminders due today
or earlier; Upcoming shows all unfinished dated reminders, including overdue
work. Overdue dates include an **Overdue** label and use the Windows critical
(red) color. All-day reminders become overdue on the following day; timed
reminders become overdue after their due time.

Right-click a list (or press Shift+F10 while it is focused) to change its icon
and color or move it up/down. These preferences are stored on this PC in
`%LOCALAPPDATA%\RemindersForWindows\ui-settings.json`; they do not modify
list metadata in iCloud. The existing sidebar open/collapsed preference is
preserved when customization is saved.

Sync now acts as a command and leaves the active list selected. Settings is a
scrollable dialog with General, Sync & storage, Account, and About pages. General
includes theme, default list, notifications, animation and pane sizing. Sync &
storage includes status, sync interval, setup help, selective unsynced-change
recovery and full cache reset. Account contains Sign out; About contains update
checks and the offline privacy policy. Update checks and recovery review remain
inside Settings, preserving unsaved preferences when returning.

The New reminder popup accepts notes, list, optional due date, all-day/time,
priority, flag, and a repeating schedule before saving. Details uses the same
grouped layout and allows daily/weekly/monthly/yearly recurrence changes after
creation. Tags remain visible and filterable, but are edited in Apple Reminders.
Opening New reminder from Today defaults the due date to today; opening it from
a list uses that list. Completion holds the checked
row for 180 ms, then fades it over 420 ms before refreshing. Windows' reduced
motion preference and the app's animation toggle disable that delay.

Demo mode includes an overdue reminder and supports filtering, creation,
completion, customization, settings, and deletion in memory.

Drag the divider to either extreme to use the full-width reminder list. Selecting
a reminder then opens full-width details with **Back to reminders**, as in a
narrow window. Back restores the list. Intermediate splits keep both panes
readable; double-click the divider, press Home, or choose automatic detail sizing
in Settings to restore automatic layout. Saved custom endpoint sizes use the
same navigation after restarting.

Sync notices float above content with an opaque background and a drop-down
animation that respects reduced motion. The title bar and divider match the
selected theme. The Completed page hides the completed-reminders switch and
retains the reminder count; other views retain the switch.

### Manual UI verification

Run `scripts\run-windows.ps1 -Demo`, then check:

- Today is the only sidebar item with a badge; the sample overdue item is red
  and labeled in Today and Upcoming.
- Sync now can be clicked repeatedly without replacing the selected list.
- Right-click a list, change its icon/color, and move it up/down; tags and the
  active list remain available.
- The add popup validates an empty title, allows an undated reminder, and saves
  due date, time, priority, flag, and recurrence together. Tags are read-only.
- Open a task from Inbox, change **List** to Work, and save. It should leave
  Inbox and appear in Work. From All, moving it again should retain selection
  and update the details header. Other task fields should keep their values.
- Settings opens on the current theme; Save applies changes and Cancel keeps
  the prior settings. Reopening shows the saved values.
- Completing a reminder visibly checks it before it leaves the list; disabling
  animation removes the pause.
- Drag the divider to either endpoint, then select a reminder:
  full-width details and Back must be available. Back returns to the list with
  the divider still collapsed. Reopen the divider and check split-view editing.
- Repeat with Settings' Custom detail share at both endpoints, then check
  automatic detail sizing in a narrow window and an intermediate custom split.
- Completed hides the completed-reminders switch; returning to All or a list
  shows it again. The reminder count stays visible.
- About checks for updates without closing Settings. Sync & storage keeps the
  unsynced-task review and Back navigation within the same dialog.

For persistence checks, run the regular app and restart after customizing a
list. Demo mode intentionally does not write preferences.

## Build

Node.js and npm are not used. Run these commands from the repository root:

```powershell
.\scripts\run-windows.ps1
.\scripts\run-windows.ps1 -Demo
.\scripts\build-windows.ps1
.\scripts\build-windows.ps1 -Architecture ARM64
.\scripts\build-msix.ps1 -Architecture x64
```

The release output is a self-contained x64 folder under `dist-windows`.
ARM64 output is written to `dist-windows-arm64`, and MSIX packages are written
to `dist-msix`.
For prerequisites, direct executable commands, tests, and troubleshooting, see
[Getting started on Windows](getting-started.md).
