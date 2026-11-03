# Subtasks

Select an unfinished task and choose **Add subtask** in its details pane. The
new task inherits its parent's list and has its own title, notes, due date,
priority, flag and completion state. Save unfinished changes to the parent
before opening the subtask dialog. Subtasks can themselves have subtasks.

The arrow beside a task expands or collapses its children for the current
session. Existing iCloud subtasks use the same display. Sorting keeps families
together; search, tags, Today and Upcoming include related tasks as context.
Only matching tasks contribute to the result count. A missing parent or a
malformed relationship never makes a task disappear.

Completing a parent completes its unfinished descendants. Reopening a child
also reopens its ancestors. Moving a parent moves all non-deleted descendants
to the destination list. Moving a child to a different list makes that
child a standalone task and keeps its own children underneath it. Deleting a
parent also deletes its descendants; restoring a task restores only that task,
so previously deleted children are not silently resurrected.
Restoring a child whose parent was removed or moved to another list makes the
restored task standalone instead of retaining an invalid relationship.

Completing repeating task families requires an Apple device. The app refuses
that operation before saving any part of the family, rather than advancing a
repeat while leaving its children's schedules inconsistent. Repeating leaf
subtasks can be completed individually. Changing an existing task's parent is
not exposed; use **Add subtask** to create a child.

## Sync and recovery

Parent links are read from the native `ParentReminder` reference and cached as
`parent_id`. The separate CloudKit record parent still refers to the list.
Creation writes the `parentReminder` resolution token. This mapping was checked
against Apple's publicly served
[Reminders script](https://www.icloud.com/applications/reminders2/2638Build16/en-us/main.js).

Existing caches recover parent links from their retained raw records without a
reset. Offline-created children wait for earlier parent uploads, and successful
parent uploads rebase both cached links and queued payloads to the real cloud
identity. Stable create identities recover a committed upload whose response
was lost. Discarding a new parent requires selecting its unsynced descendants
too, to prevent orphaned drafts.

## Validation — November 2, 2026

- 973 backend, authentication, scheduling, cache, hierarchy and sync checks.
- 24 updater checks and 8 CI policy checks.
- Regression coverage for offline creation/restart, old-cache migration,
  filtered family context, sorting/collapse, missing parents, cycles, 10,000
  nested tasks, family completion/reopening/moves/deletion, transaction rollback,
  rejected parent uploads, lost child-upload replies, ID rebasing and retry.
- Native sample-data UI checks cover nested display, collapse/expand, blank-title
  validation, inherited list, undated subtask creation, independent notes,
  preservation of unsaved edits during refresh, family completion and reopening.
- Debug x64 and Release x64/ARM64 builds compile successfully. ARM64 runtime
  behavior was not exercised on this x64 machine. NuGet's vulnerability feed
  was unreachable, so the builds report NU1900 warnings for that lookup.

The tests use temporary databases, in-memory credentials and mocked iCloud
requests. Live interoperability with an Apple device remains unverified.
