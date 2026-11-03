# Bug and UI audit — November 2, 2026

The second audit found and fixed the following issues on `codex/edit-task-list`.

| Issue | Corrected behavior |
| --- | --- |
| Switching tasks, changing lists, Back or closing the window could lose an unsaved task edit. | Save, discard or cancel before leaving the editor. Cancel retains the draft; saving finishes before navigation or closing. |
| A slow query could replace the results of a later navigation/search request, including after sign-out. | Only the newest active request may update the list or report an error. Account cleanup and window closure invalidate pending results. |
| Sync and conflict actions were handled by both sidebar selection and invocation. | Invocation owns the action; selection restores the current view. Conflict dialogs cannot overlap. |
| Saving or deleting used mutable selection state across asynchronous work. | Capture the target task and draft before waiting, block competing edits during save, and retain drafts on failure. |
| Saving an edited title also resent untouched notes, dates, flags and priority, potentially replacing newer sync data. | Send only edited fields; keep date/time and all-day state together as a scheduling decision. |
| Malformed parent links could let family actions change a task in a different list or below an already-deleted ancestor. | Family mutations follow only links within the same list and deletion state. Recovery can still inspect all dependencies. |
| Repeating an already-completed flag silently dropped other edits in the same request. | Ignore the redundant flag while retaining title, notes and other changes. |
| Moving and reopening a child could reopen its former parent. | Reopen ancestors only through the child's saved, current relationship. |
| Metadata looked washed out when the app's light theme differed from the system theme. | Resolve normal and overdue colors in XAML using the current control theme. |
| Full-width compact details shifted the form right and clipped its controls. | A stretching container gives the editor one centered maximum-width layout, aligned with its header and footer. |

## Automated verification

- 990 backend/authentication/cache/scheduling/hierarchy checks, including new
  regression cases for family boundaries, redundant completion, detached-child
  reopening and deliberately out-of-order asynchronous loads.
  Edit-patch tests also preserve newer notes, flags and priority while saving
  a title from an older open editor.
- 24 updater checks.
- 8 CI policy checks.
- x64 Debug compilation for the audit fixes; x64/ARM64 Release compilation
  for the final changes, including the subtask appearance refresh.

The family-boundary test reproduced the cross-list mutation failure before its
fix. Async tests deliberately complete an older request after a newer one and
invalidate an in-flight request before returning old-account data.

## Native UI verification

Sample data was used without opening an iCloud account:

- Light theme: normal due-date text and overdue text use readable colors.
- Switch tasks with an unsaved note, cancel, and verify the original task and
  draft remain selected.
- Navigate to another list, save from the prompt, and verify both the requested
  destination and the saved note on the original task.
- Invoke Sync now repeatedly and retain the current list.
- Close using Alt+F4 with a draft: Cancel retains the window and draft; Save
  finishes and closes the app.
- Dark compact details: check the complete form against its header/footer and
  verify the Back navigation prompt and save path.
- Open and cancel the dark Settings dialog; its categories, controls and footer
  remain within the visible window.

The compact check uses the existing collapsed-divider preview. Layout sizing
also has automated boundary checks. Live iCloud/Apple-device interoperability,
native ARM64 execution and packaged/Store installation were not exercised.
NuGet's vulnerability feed was unreachable; builds report NU1900 for that lookup.

## Subtask appearance follow-up

The requested visual refresh moves the disclosure arrow to the right and uses
the native borderless button style. Child rows use tighter spacing, regular
titles and an indentation guide. Repeated context labels and child-note previews
are removed from the list; the full notes remain in task details. Deep indentation
is capped to preserve text space. Native on-screen testing was stopped with
Escape before this refresh, so the revised subtask appearance is compiled but
still needs a light/dark visual pass.
