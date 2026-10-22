# v1.0.0 behavior and validation

Reviewed October 22, 2026. This document describes the current implementation and
its evidence, rather than individual test-build experiments.

## Automated checks

- 836 account-free backend, authentication, scheduling and layout checks.
- 24 updater checks for stable-release selection, architecture, size and digest
  verification, and installation eligibility.
- 8 CI policy checks for cache, permissions and release-artifact boundaries.
- x64 and ARM64 Release builds, self-contained portable folders, EXE installers
  and Store submission MSIX packages. CI checks app/package version agreement,
  final PRI identity, startup XAML and shell-icon resources.
- Packaged launch on x64. Native ARM64 launch and signed Store installation or
  upgrade need the corresponding hardware and distribution path.

Run the commands in [Getting started](getting-started.md). See [CI](ci.md) for the
release pipeline. Tests use temporary databases, an in-memory credential vault
and mocked HTTP; they do not modify the user's actual iCloud account or cache.

## Sync and queued edits

Full sync downloads Reminder, List, Hashtag and RecurrenceRule records through
one paginated zone stream. SQLite applies each downloaded batch atomically. An
unchanged incremental sync needs one request. The default interval is one minute,
configurable from 1 to 60; the former ten-minute default migrates once without
overwriting other saved intervals.

Known rejected writes stay queued, while independent tasks can upload and current
cloud data can download. Later edits to a rejected task cannot overtake its earlier
edits. Authentication/network failures stop or restore the session. The UI reports
pending uploads or conflicts instead of announcing unconditional success.

Checks cover offline edits, more than 100 dependent/unselected uploads, rebasing,
missing-record responses, retries after lost replies and stable create identities.
CloudKit's `NOT_FOUND` and `UNKNOWN_ITEM` are accepted only where a missing record
is an expected result; denied access and incomplete replies remain failures.

## Repeating tasks

New reminder and Details support daily, weekly, monthly and yearly schedules with
intervals of 1–999 and a due date. Details can add, change or remove a repeat after
creation. Supported rules retain end dates and occurrence limits. Unsupported
custom constraints and multiple-rule schedules are protected rather than replaced.

Completing a supported repeat advances the active reminder and creates completed
history atomically. Stable queued identities prevent a retry from advancing twice.
Frequency conversion is explicit: Windows/RPC values 1/2/3/4 correspond to Apple's
stored 0/1/2/3. Independent fixtures cover all four units, calendar boundaries,
DST, leap days, count limits, offline schedule edits and stale revisions.

The frequency mapping was checked against Apple's Reminders `2638Build16` bundle
on October 20. The inspected copy's SHA-256 is
`cb1097a325a50c37e8d3e9f5b1395c0551e34744bd857c7dc243dde78951d8b8`.
See [protocol findings](protocol-findings.md) for the reference.

The user confirmed corrected recurring dates in a test build. Earlier incorrectly
uploaded schedules need an explicit change to the intended frequency; the app
cannot distinguish them from deliberately chosen Apple schedules. Exhaustive
iPhone verification of post-creation schedule edits and custom rules remains a
separate live check.

## Tags

Tags are read-only in both Windows reminder forms. Existing iCloud tags are
displayed and can filter the sidebar. The backend rejects tag creation and tag
fields on create/update before saving changes. No tag writer or automatic repair
remains.

Old `tag`, `tags` and `repair_tags` outbox entries and tag-only conflicts are
archived in local `disabled_tag_writes` metadata and retired from the active queue.
Displayed speculative tags revert to the last downloaded snapshot. Ordinary edits,
unrelated conflicts and actual iCloud tag records are preserved.

Checks cover migration, restart, archived requests, later ordinary edits, tag
filtering and downloaded changes. Earlier server readback did not establish native
iPhone tag propagation. Reintroduction requires the acceptance criteria in
[tag-sync research](tag-sync-research.md).

## Authentication and account controls

Apple-hosted sign-in uses isolated WebView2 windows, including popups with their
opener/message relationship preserved. Successful account-login or validation
responses can trigger handoff; Continue in app is a fallback. The native client
validates the imported cookies, user agent, account and challenge state before
saving the session. A completed browser session without remembered trust also
needs a successful read-only Reminders access check.

Saved cookies, tokens and trust survive normal shutdown in Windows Credential
Manager. Native SMS delivery and verification use the same selected route/mode.
Mocked checks cover header rotation, accepted/rejected codes, expired sessions,
cookie-only restoration, cancellation and a fresh native password handshake.

The user confirmed Apple-device handoff and a subsequent reopen without another
verification prompt. Reboot restoration, live native SMS, security-key/account
variations and expiry still require live validation. Apple controls session
expiry; app-specific passwords are not a supported replacement. See
[sign-in options](authentication.md).

Settings → Account → Sign out clears saved credentials/trust, stops background
work and returns to sign-in while retaining cached reminders and queued edits.
An explicit sign-out persists across restart and prevents automatic restoration.

## Recovery and Settings

Settings has General, Sync & storage, Account and About pages. Update checks,
privacy content and recovery review stay in that dialog. Back retains unsaved
preferences; long operations support cancellation and prevent overlapping writes.

Discard unsynced changes lists tasks with queued uploads or unresolved conflicts,
with no selection by default. Confirmation removes new local-only drafts or
restores cloud-backed tasks from fresh iCloud copies. It never modifies iCloud.
Reviewed queue/conflict versions must still match, including after lookup and
inside the transaction. Recovery drains background work first and requires a
complete per-record response. Cancellation or failed lookup keeps the selection
intact. Unselected edits, raw subtasks, lists, tags/repeats, preferences and the sync
cursor are preserved. Counts include all queued uploads.

Clear app cache resets the complete local reminder cache, queued uploads, conflicts
and cursors, retaining saved sign-in and preferences. It requires explicit discard
confirmation when edits would be lost and is unavailable for local-only accounts.
Neither recovery action was exercised against the user's actual cache during tests.

## Layout and icons

Intermediate split panes keep both sides readable. At either intentional endpoint
or in a narrow window, task selection opens full-width Details with Back to
reminders. Dragging inward, Home or double-click restores a split/automatic layout.
Layout checks cover both endpoints, cumulative dragging, cancellation, saved shares
and shrink/grow behavior.

Details and New reminder share grouped Schedule/More options fields and fixed
action footers. Tags remain read-only. The Completed page hides the completed-task
switch while retaining its count. Other views retain the switch.

Title bars/divider gutters follow the theme. Sync notices have opaque backing and
slide over content without moving it, respecting reduced motion. MSIX resources
include default, unplated and light-unplated icons and 200%/400% logos.

Isolated dark previews covered key list/editor/Settings layouts and a timed weekly
creation in memory. Final light-theme/compact-layout checks, full endpoint
navigation, overlay animation, inline update results and selective-discard review
still need manual inspection. Debug preview flags are excluded from Release builds.

## Live validation checklist

Use disposable reminders/accounts where mutation or data loss is possible:

1. Compare Windows and iPhone completion and schedule edits for each repeat unit,
   including timed/all-day dates, tags, end limits and protected custom rules.
2. Change tags in Apple Reminders and confirm Windows display/filtering after sync,
   ordinary edits and restart. Windows must not offer tag writes.
3. Test Apple-hosted and native SMS sign-in, explicit sign-out, expiry and reboot
   restoration without assuming Apple will retain trust indefinitely.
4. Reorder lists, sync/restart and confirm saved order and newly appended lists.
5. Exercise selected recovery with mixed drafts/conflicts and confirm unselected
   tasks and cloud records remain intact; inspect the full-cache confirmation.
6. Check light/dark themes, keyboard navigation, display scales, compact Settings,
   split endpoints and update results. Verify signed Store upgrades separately.
