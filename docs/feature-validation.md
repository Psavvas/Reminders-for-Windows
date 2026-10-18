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

Tag writes follow the linked-record shape described in the
[public protocol implementation](https://github.com/portpowered/go-icloud/blob/main/pkg/icloud/reminder_hashtag_create.go).
Repeat records follow its
[recurrence write format](https://github.com/portpowered/go-icloud/blob/main/pkg/icloud/reminder_recurrence_request.go).
Those references describe an unofficial API, not an Apple compatibility guarantee.
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
3. Add a tag to a reminder with existing iPhone tags. Confirm all tags render on
   both devices. Repeat after creating a reminder while offline.
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
