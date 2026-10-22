# Sync, repeats, trust, tags and sidebar order

The account-free suite covers atomic linked-record writes, stable reminder
identities, recovery after a committed completion loses its network reply,
parent/tag associations, incremental tag updates, cache migrations, list order
across refresh/reopen, and session/token restoration without starting another
password/verification handshake. Calendar cases include DST, missing month days,
leap days, count limits and end dates.

Full sync uses one paginated zone stream instead of a cursor query, list-history
read, one query per list and tag-history read. An unchanged delta is one request.
SQLite applies each downloaded batch in a transaction. The default interval is
one minute, configurable from 1 to 60 minutes. Existing installations using the
old 10-minute default migrate once; other intervals remain intact.

Supported repeats retain the active reminder and its linked schedule, advance
its due date, and atomically create a completed history entry without a repeat
link. The history identity is queued before upload so a lost response cannot
advance twice. Unsupported custom constraints and multiple rules are refused
before changing the remote reminder. Those reminders must be completed on an
Apple device. The app can create daily, weekly, monthly and yearly repeats with
an interval of 1–999 and a due date.

## Repeat frequency compatibility

The Windows UI and RPC use daily=1, weekly=2, monthly=3, yearly=4. CloudKit's
stored `RecurrenceRule.Frequency` uses daily=0, weekly=1, monthly=2, yearly=3.
Convert explicitly on writing and reading the rule. Apple's hourly/minutely/
secondly values (4/5/6), missing values and unknown frequencies are unsupported
and must not be mistaken for one of these four schedules.

This was verified on 2026-10-20 against Apple's publicly served
[Reminders 2638Build16 script](https://www.icloud.com/applications/reminders2/2638Build16/en-us/main.js).
The `RecurrenceRule` model reads `Frequency` as a number, and its conversion
function (named `_m` in that build) maps 0/1/2/3 to daily/weekly/monthly/yearly.
Downloaded script SHA-256:
`cb1097a325a50c37e8d3e9f5b1395c0551e34744bd857c7dc243dde78951d8b8`.
Tests use independent numeric service fixtures for all four units, calendar
advancement, two-week intervals, create requests and reloaded cache descriptions.
They also complete an Apple-created weekly rule without changing its frequency.

Earlier test builds sent the UI value unchanged. The user confirmed that a
Windows-created weekly reminder showed monthly on iPhone and advanced one month
when completed there. Independent wire-format tests reproduce the mismatch
before the fix. All four units were affected; yearly could become hourly.
Previously uploaded rules cannot be safely repaired by guessing their intended
schedule, because they are indistinguishable from valid Apple-created rules.
Set affected reminders to their intended repeat once using the new Details
editor or Apple's Reminders; both will then use the same rule. Pending reminders
upload with the corrected values. The user subsequently confirmed the recurring
due dates and Apple-device web sign-in work in the corrected test copy.

## Editing a task's repeat

Select a task and use **Repeat** and **Repeat every** in Details, then **Save
changes**. Add a daily/weekly/monthly/yearly repeat to an existing task, change
its unit or interval (1–999 whole units), or choose **Never** to stop repeating.
A repeat needs a due date; choosing Never also allows clearing that date in the
same save. The controls stack in narrow detail panes. Custom and multiple-rule
Apple schedules remain read-only to preserve their constraints; other fields
can still be edited without sending a replacement schedule.

Existing supported rules are updated in place, preserving their end date,
remaining occurrence count, task identity and unrelated fields. Adding a repeat
creates a linked rule; Never clears the parent's rule IDs and marks the linked
rule deleted without deleting the task. Parent and rule changes are atomic and
use their current revision tags. The changed rule is applied to the cache as
soon as upload succeeds. A conflict involving a rule is reported against its
parent task, and keep-local retries a repeat change only when that conflict
actually included a repeat edit.

Each queued schedule edit stores a stable UUID. Its identity is included in the
parent's existing `recurrenceRuleIDs` resolution token so that retry can detect
an already committed change, including Never, before comparing stale revisions.
No additional CloudKit fields are introduced. Tests cover lost replies,
duplicate retries, offline create/add/change/remove sequences across restart,
cache refresh, valid/invalid intervals, required dates, unchanged custom rules,
preserved end conditions and stale parent/rule conflicts. Local-only accounts
use the same edit validation and calendar behavior.

The sample window closed before UI inspection. Live UI and iPhone propagation
of post-creation schedule edits still need confirmation; the earlier repeat-date
and web sign-in confirmations do not cover this new editing path.

## Window theme, notifications and task editor

The native title bar follows the root's actual light/dark theme, including
appearance changes while the app is open and changes to the Windows system
theme. Active and inactive caption/button colors match the app surface. This
uses the Windows App SDK's
[title-bar theme API](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindowtitlebar.preferredtheme).

Notifications are top-aligned overlays over the main content, alongside the
reminder layout in the same grid with no reserved banner row. They do not move
the list or editor when opened or closed. Closed bars are explicitly collapsed;
their old outer margins and NavigationView content border/corner are removed.
Each banner has an opaque theme-aware backing beneath WinUI's translucent
severity color, so the heading and toolbar do not show through it. The backing,
notification and shadow slide together without an opacity fade. The overlay's
minimum height is 64 DIPs to cover the heading and Add button without leaving
clipped fragments below it. Opening uses a short slide-down and subtle shadow.
Render-time translation leaves layout positions unchanged. Windows' animation preference
is respected. Update actions, sync recovery buttons and Close remain available.

When both banners are closed, their parent and individual elevated surfaces are
collapsed so empty bounds cannot leave a shadow line across the top. The pane
resize thumb shows only its short accent handle on hover, drag or keyboard focus; the old
full-height decorative and focus lines are removed, while system keyboard
focus visuals and existing resize controls remain. A dark, account-free sample
confirmed the clean top edge after dismissing the sync banner and the handle
without the vertical line. A later dark preview confirmed resizing to both
new pane limits; light-mode appearance still needs visual checks.

The task editor has a compact title/notes area, a grouped Schedule card and an
expandable Tags & priority section. List context is text in the header instead
of a disabled list picker. Delete is in Task actions; Save changes stays in a
fixed footer outside the scrolling fields. Time appears for timed tasks, the
all-day switch appears when a date is set, and the interval appears only for a
repeat. Paired fields stack in narrow panes. Existing save/validation behavior
and repeat editing are retained.

Debug-only `--preview-editor` loads an isolated in-memory sample task; add
`--preview-light` or `--preview-banner` for appearance/notification checks.
Dark previews confirmed the themed native title bar, full-width banner, grouped
editor and the new opaque backing. The user stopped Computer Use with Escape
before rechecking the final 64-DIP banner height. Its final height, light-mode
appearance and slide animation still need visual checks. No real-account
reminder was changed by the previews.

## Pane limits and the New reminder form

Intermediate split positions keep both panes at least 320 DIPs wide, so tiny
custom shares cannot squash their text. The intentional 0% and 100% endpoints
both use list-to-task navigation: without a selection, the list fills the content
area; selecting a task opens details across that full area with Back to reminders.
Back restores the list without changing the chosen endpoint. A resize handle
remains at the list's right edge, where dragging inward restores a readable split;
Home or double-click restores automatic sizing.

Pointer resizing accumulates movement from the original split, allowing a drag
to reach either endpoint beyond the normal minimum-width stop. A selected task
switches to full-width details after the drag ends, keeping the captured handle
available throughout the gesture. Cancelling a drag restores the prior share
without reloading or discarding an unsaved detail edit. Normal preferred shares
still return when the window grows.

Below 648 DIPs of content width, automatic and custom sizing both use one pane
at a time. Selecting a task opens full-width details with Back to reminders;
without a selection the list fills the content area. Paired editor controls
respond to their actual inner width and stack below 500 DIPs, leaving enough
room for the native time picker. Settings labels the custom percentage as a
preferred width and explains full-width task navigation at either endpoint.

The New reminder dialog shares the editor's prominent title, compact scrollable
notes and Schedule card. List selection remains visible; priority and flagging
are in More options. The schedule toggle adds/removes the due date, a repeat
turns scheduling on, time appears only for timed reminders, and the interval
appears only for repeats with a unit-specific label. Controls stack in narrower
forms. The form's width and scroll height adapt to the window while Add reminder
and Cancel stay in the fixed dialog footer. Existing list/date/title/interval
validation and the creation request fields are retained.

790 account-free checks passed, including task selection and Back at both
endpoints, restoring split view from the list's edge, cumulative dragging beyond
the minimum stop, saved shares, compact layouts and shrink/grow behavior.
An earlier dark sample confirmed readable split panes and empty-details text.
The refreshed dialog was checked
with scheduling off/on and successfully created a timed weekly reminder in
memory, including the expected Weekly metadata. No iCloud record was changed.
The endpoint live check could not activate its sample window: the UI helper
returned `failed to activate captured window` on the initial attempt and retry.
Full-width endpoint navigation, narrow-dialog reflow and light mode still need
live checks. Debug previews accept `--preview-list-min`,
`--preview-details-min` and `--preview-new-reminder` alongside `--preview-editor`;
these flags are excluded from Release builds.

## UI polish

The reminder toolbar uses consistent gutters and a compact title row. Empty
subtitles, metadata, notes and tag labels collapse instead of reserving blank
space. Rows retain native selection/focus feedback with consistent padding;
the completed toggle and count share a single footer row. Empty lists and empty
search results have separate, wrapping messages. Details header, form and Save
button share a 620-DIP maximum width, and the hidden Back button leaves no
extra column gap. New reminder and Details share title/notes field styles.

790 existing backend/scheduling/layout checks passed. An isolated dark sample
confirmed the toolbar, list rows with/without notes, compact footer, editor and
empty-details placeholder. A later dark preview also confirmed the final
Back-column alignment. Light theme and empty-search text still need live visual
checks. No real-account data was changed.

The resize gutter now shares the details background, removing the third color
between the two panes. Settings uses padded, wrapping category rows, wider
dropdowns, compact switches, consistently padded action buttons and an inset
disclosure chevron. Narrow settings rows stack and stretch dropdowns; category
navigation switches to a dropdown before the content becomes cramped. Custom
pane-width help appears with its slider. A dark sample confirmed the clean
gutter and General page padding, icons, switches and dropdowns. The remaining
Settings pages, narrow-window reflow and light theme still need live checks;
further inspection was deferred while the desktop was in use. All 790 existing
checks passed again.

## Related records and icons

Tag compatibility is based on the Apple-created record shape described in
`docs/protocol-findings.md`, not the older third-party example that encoded
`Name` as `ENCRYPTED_BYTES`. Tags use STRING names, Type/Imported metadata,
validated parent references and atomic child/parent relationship changes. The
Windows-only `hashtagIDs` resolution token is removed from subsequent edits.
The sync engine repairs only the identifiable incomplete shape written by the
earlier Windows build; existing Apple tag types and unrelated metadata remain.
It verifies tag writes by reading back the changed records before acknowledging
the queue entry. HTTP success alone is not a successful tag save.

Both New reminder and Task details have an editable **Tags** field under
**Tags & priority**. Separate names with spaces or commas, with optional `#`.
Removing a name removes that task's tag on Save; a blank field removes all tags.
Tag edits commit to the local cache and upload queue together with other form
changes. Task fields upload first, then the tag relationships, in per-task order.
Stable child identities survive a restart or a lost response. A download cannot
resurrect locally removed tags while their upload is pending. Conflict recovery
preserves tag intent only when the conflicting operation edited tags.

The account-free suite passes 890 checks. Tag checks cover an independent Apple
record fixture, correct field types, retained Type=1 records, subsequent date
edits, offline creation/removal/restart with repeats, lost removal replies,
last-tag removal, old-record repair, conflict recovery and successful HTTP replies
whose changes were not persisted. Downloads have their own event; completion is
emitted only when uploads have drained and no conflicts remain. These checks do
not establish real iPhone rendering or propagation; that remains a live check.
Isolated dark previews confirmed tag fields in both forms, wrapping help text,
the fixed footer, and saving sample tags into the task row and sidebar.

Repeat records follow the structure of the
[public recurrence implementation](https://github.com/portpowered/go-icloud/blob/main/pkg/icloud/reminder_recurrence_request.go),
with the explicit Apple frequency conversion described above.
Apple documents app-specific passwords for Mail, Calendar and Contacts;
they do not authenticate this CloudKit Reminders client. See
[Apple's explanation](https://support.apple.com/en-us/102654).

MSIX includes default, unplated and light-unplated target sizes plus 200%/400%
logos. The packaging script checks every unplated size in the generated PRI,
including its `Files/Assets` resource path. The sizes follow
[Microsoft's app-icon guidance](https://learn.microsoft.com/en-us/windows/apps/design/style/iconography/app-icon-construction).

## Checks before distributing to users

These require a disposable Apple account and a real iPhone; mocks cannot confirm
that Apple's other clients render a write or accept a trusted session.

1. Complete an iPhone-created daily/weekly repeat in Windows. Confirm the next
   occurrence remains active on both devices and the completed entry appears.
   Repeat with a timed reminder, tags, notes and an end/count limit.
2. Create each supported repeat in Windows and confirm the schedule on iPhone.
   Confirm a custom weekday/ordinal rule is protected by the app's guard.
3. Create a tagged task in Windows and confirm its tags on iPhone. Add a tag to
   an existing task with iPhone tags, change its due date, then remove one tag
   and finally all tags. Confirm each change on both devices and after restart.
   Repeat offline, and check an old Windows-created malformed tag after sync.
4. Use **Sign in with an Apple device**, finish Apple's verification and choose
   **Trust**. Close/reopen the app, then reboot Windows; confirm automatic restore.
   Repeat with native SMS sign-in. Check explicit sign-out removes the saved trust.
5. Reorder lists, sync, restart, change another UI preference and restart again.
   Confirm the order remains and newly created iPhone lists are appended.
6. Install the packaged build on light/dark Windows themes and several display
   scales; confirm Start/taskbar icons have no colored backplate. Re-pin an old
   shortcut if Windows is still showing its cached icon.

The Apple-hosted sign-in window needs Microsoft Edge WebView2 Runtime. Apple may
expire/revoke trusted sessions or require additional account approval. Advanced
Data Protection and web-data-access limitations remain as described in setup.

## Apple sign-in popup and failure checks

The host supplies a separate WebView2 for an Apple popup in the same environment
and InPrivate profile. It preserves `window.opener` and cross-window messages;
navigating the original view in response to `window.open()` broke that relationship.
The Back button closes the child and restores its parent. Reload starts a fresh
page without automatically submitting a password or requesting another code.

For account-free integration testing, launch an x64 Debug build with
`--preview-apple-popup` and an isolated `REMINDERS_DATA_DIR`. Choose **Open test
popup**, then **Return test result**. The original page must show **PASS**.
Also check **Back**, **Reload**, and **Cancel**. This fixture contains local HTML
only and is excluded from Release builds.

Page/frame navigation failures, authentication/account HTTP statuses, and session
import failures are recorded with their stage. The log omits URLs, query strings,
headers, response bodies, credentials, and account identifiers from these events.
Normal Apple challenge statuses such as 401 and 409 remain under Apple's page's
control. Cancelling an import keeps the previous account and does not save a new
session. The account-free popup check does not confirm delivery of a real Apple
device prompt; that remains part of the live-account check above.

Browser sign-in may finish via `/validate` rather than another token-bearing
`accountLogin` request. Both responses can trigger the native handoff. The native
client independently checks the browser's cookies against `/validate` using the
same user agent, checks the account and pending challenge, and saves rotated cookies
for restart. A token is optional for this browser-cookie route. Pending server
challenges are still rejected. The tests cover cookie-only import and restart,
network retry, and refusing an unfinished challenge.

`hsaTrustedBrowser` is remembered browser trust, not the completion status of a
sign-in. A completed browser session can report `hsaTrustedBrowser: false` and
`hsaChallengeRequired: false` while displaying Reminders. That combination must
not silently block the native handoff. When remembered trust is false, the native
client additionally performs a read-only Reminders query limited to one result
before accepting or restoring the session. It rejects a pending challenge, wrong
account, denied Reminders access, and record-level authentication errors. The
stored trust flag remains false; the app does not claim Apple granted trust or
change Apple's rules for when verification will be required again.

The handoff also recognizes successful Reminders query/change responses. This
allows a restored web session to connect even if its account-login response was
not observed. **Continue in app** explicitly retries cookie validation; it does
not need a captured account response or any account information scraped from the
page. The native client learns the identity from iCloud's validation and refuses
to switch accounts with queued edits before saving credentials or clearing cache.
Check that automatic handoff closes the web dialog. If it stays open after the
web reminders load, check **Continue in app**, including error and cancellation
behavior.

Live check on 2026-10-19: the user confirmed that Apple-device sign-in returned
automatically to the native app without using **Continue in app**. The matching
trace shows the unfinished account response was ignored, a later eligible
response started import, native `/validate` returned HTTP 200, import completed,
and native sync began. This confirms automatic handoff for that session. The
manual fallback and Windows reboot restoration still need live checks.
Closing/reopening was checked later that day: browser-cookie validation returned
HTTP 421, saved-token account login succeeded with HTTP 200, and native sync
resumed without another password or verification prompt.

Debug builds accept `--test-apple-sign-in` to label the window **Sign-in test**.
Startup logs include assembly build identity, and `stage=page opening handoff=2`
identifies this handoff implementation without logging account information.

## Sign out

**Sign out** is in **Settings → Account**. It removes the saved password,
session cookies, and trust tokens, stops the active sync service and notification
timers, clears the account's rows from the window, and returns to sign-in. An
in-flight background restore or sync is cancelled before waiting for the account
lock, so sign-out does not wait for a slow HTTP timeout. An
unsaved detail edit asks before it is discarded. Cached reminders and queued
edits stay on this PC; an explicit sign-out flag prevents automatic restoration
and cache-based access on the next launch. Successful sign-in clears that flag.
Local accounts and sample data leave their own session without clearing saved
iCloud credentials.

Account-free checks cover credential removal, sign-out across restart, disabled
sync/notifications, and retaining queued edits through a later sign-in. Check the
Settings action in a real account and then relaunch to confirm it remains signed
out; signing back into the same account must restore the retained reminders.
The final Settings placement has build validation; its live UI check was stopped
by the user before interacting with the running copy.

## Settings and cache recovery

Settings has four categories: **General**, **Sync & storage**, **Account**, and
**About**. A category selector replaces the side navigation in smaller windows.
Controls are grouped into cards, custom pane width appears only when selected,
and the long app disclaimer is expandable. Categories and the privacy-policy
view preserve unsaved settings; Save changes applies them and Cancel discards
them. **Settings and storage** on the sign-in screen provides recovery access
without requiring successful sign-in first.

**About → Check for updates** keeps Settings open. A spinner and disabled check
button indicate progress; the result (up to date, available release, or error)
appears in the same card. Category changes and privacy-policy navigation keep
the result and unsaved preferences. Closing Settings cancels and drains its
check before account actions continue. Background update checks do not start
while a dialog is open. The explicit Install and restart action still opens
the existing installation confirmation; Open releases keeps Settings open.
The updater service checks and x64 builds passed. Live checks
of the inline result, cancellation, and retained preferences remain pending.

**Sync & storage → Clear app cache** removes downloaded reminders, tags, repeat
records, sync cursors, old identifier aliases, queued uploads, and conflicts.
It keeps account identity, saved sign-in, app preferences, and the explicit
sign-out choice. It does not delete records in iCloud. Background work is cancelled
and drained before the atomic reset, preventing an older download from filling
the cleared cache. A fresh download starts when the account can restore.

If there are queued changes or unresolved conflicts, the confirmation requires
an unchecked-by-default discard choice. The backend also checks this condition
inside the reset transaction. Local-only accounts reject cache clearing because
their reminders have no cloud copy. The implementation was tested on isolated
databases; the user's real cache was not cleared.

A known record rejection during upload no longer blocks downloading the latest
iCloud data. The edit remains queued and the UI shows a warning linking to Sync
& storage after the download. Transient or unclassified server errors remain
retryable. Regression checks cover retained edits, successful downloads despite
rejected uploads, reset cancellation/draining, preserved credentials/preferences,
discard confirmation, and fresh downloads without an old sync cursor.

The sample-only `--preview-settings` flag opens the General page without an Apple
account and is excluded from Release builds. Visual inspection caught an attempt
to scroll before attachment to XamlRoot; that initialization was corrected. The
user stopped the subsequent UI check, so final visual checks of the responsive
layout, themes, and reset confirmation remain pending.

## Write compatibility with v0.5.5

The new stable-identity checks look up a reminder or completed occurrence before
creating it, so retries can recover a committed write whose reply was lost.
CloudKit Web Services reports a missing record as `NOT_FOUND`; the first
implementation only accepted `UNKNOWN_ITEM`. This incorrectly rejected fresh
creates and recurring completions before their modify request. Tests now use
the [documented Web Services response](https://developer.apple.com/library/archive/documentation/DataManagement/Conceptual/CloudKitWebServicesReference/ErrorCodes.html)
and reproduce the refusal before the fix. Optional per-record lookups accept
both missing-item codes. Required lookups, top-level failures, denied access,
missing zones, and authentication errors still fail normally.

A rejected queued write previously stopped every later upload. Sync now keeps
that edit and its dependent edits queued, attempts other reminders in sequence,
and downloads current data before showing a warning. A later edit to the same
reminder cannot overtake a rejected edit. Authentication and network failures
still stop or restore the session. An index supports selecting independent
uploads without repeatedly scanning a large backlog.

Regression coverage includes successful creation and ordinary completion behind
a rejected queue head, more than 100 dependent edits, rebasing an edit queued
before creation finishes, retrying without duplicate creation or repeat
advancement, and retaining every rejected edit. Diagnostics record endpoint and
bounded server error code without record identities, content, or server reason
text. The user reports that the published v0.5.5 still writes successfully;
real-account creation/completion in the corrected test build remains to be
confirmed. Clearing the user's cache is not required by this fix and has not
been performed.
