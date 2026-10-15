# Local test account

In version 0.5.5 or newer, use the normal sign-in form with:

- Username: `demo`
- Password: `dBZz35hzY99ATPt8VWQ6ol6k`

On first launch, continue through the setup guide to reach sign-in. You do not
need an Apple account or to change any iCloud settings to use this local account.

This is a public test credential, not an iCloud account. The app does not display
or advertise it. The built-in verifier stores only its SHA-256 hash.

The account starts with Inbox, Work, and Personal lists, including reminders with
dates, priorities, a tag, completed items, and a deleted item. Create, edit,
complete, delete, search, sort, and test due-date notifications using
the normal app controls. Settings and reminders persist on this PC in
`%LOCALAPPDATA%\RemindersSync\demo\cache.db` (Windows may virtualize this path
for packaged installations). They never enter the iCloud cache or upload queue.

Use **Settings → Leave local account** to return to sign-in. Local test data is
retained. Sign in again with the same credentials after restarting the app to
continue testing. Cloud synchronization and conflicts require a real iCloud
account; Sync now only confirms that this account's changes are saved locally.

For a temporary in-memory UI preview instead, developers can still launch with
`.\scripts\run-windows.ps1 -Demo`.