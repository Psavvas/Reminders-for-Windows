# Can Windows write real Apple Reminders tags?

Research date: 2026-10-22.

Current product status: tag editing and automatic repair have been removed at
the user's request. Existing tags remain visible and filterable. Old queued tag
writes are retained in a local archive and retired from the active upload queue;
no existing iCloud tag is deleted by this migration.

## Conclusion

Direct Windows-to-iPhone tag writes remain **unverified and currently fail in
the user's testing**. They are not established by the passing account-free
suite, an HTTP acknowledgement, or a successful readback from iCloud. The
previous description of the schema change as a tag-sync fix was premature.

This is not proof that direct writes are impossible. It means the attempted
implementation and public examples do not establish a compatible write path.
Native tags can be written through Apple's private ReminderKit framework on a
Mac. A Mac helper is therefore a plausible alternative architecture; it has
not been implemented or tested with this Windows app.

## Evidence

| Route | What the evidence establishes | What it does not establish |
| --- | --- | --- |
| Apple iPhone/Mac apps | Native tags exist and sync between supported Apple devices. | A public Windows tag-write API. |
| Inspected Apple web bundle | Reads Hashtag records; Name and Reminder are read-only in its model. Its Hashtag operations register load and update handlers, with no create handler. | A supported tag-creation workflow or a complete native tag-write contract. |
| pyicloud | Implements direct CloudKit tag creates and server round-trip validation. | Device-side recognition: its tag validator polls its own API and does not assert iPhone rendering. |
| go-icloud | Implements similar parent/child operations. Its inspected tag replay tests use synthetic HTTP fixtures. | A real iPhone tag round trip. |
| macOS ReminderKit integrations | Document tag writes through the framework used by Apple's app. | Availability of that framework on Windows or a tested Windows-to-Mac bridge. |

The inspected Apple bundle is public Reminders `2638Build16/en-us/main.js`,
identified by the source URL and SHA-256 below. These findings do not describe
every currently served Apple build. It also contains attributed-text hashtag
identifiers, showing that inline tags have metadata beyond plain `#text`.
This does not prove inline annotations are required for standalone tags.

Changing Name from ENCRYPTED_BYTES to STRING and matching observed Type/Imported
fields addressed a concrete difference in stored records. It did not prove
that those records satisfy Apple's native import and relationship rules.
Likewise, removing a token absent from the inspected web model is a hypothesis
about compatibility, not a documented native service contract.

## Primary sources inspected

- [Apple: tag items on iPhone](https://support.apple.com/en-ie/guide/iphone/iph474a6685c/ios)
  and [Apple: organize reminders](https://support.apple.com/en-gb/119953).
- [Apple's public web bundle](https://www.icloud.com/applications/reminders2/2638Build16/en-us/main.js).
  Retained copy SHA-256: `cb1097a325a50c37e8d3e9f5b1395c0551e34744bd857c7dc243dde78951d8b8`.
- [pyicloud tag writer](https://github.com/timlaing/pyicloud/blob/e2906e8aa9ed038a52b301c63438c1b15959afb5/pyicloud/services/reminders/_writes.py)
  and [live validator](https://github.com/timlaing/pyicloud/blob/e2906e8aa9ed038a52b301c63438c1b15959afb5/examples/example_reminders.py).
  The separate MCP project [94youshin/icloud-reminders-mcp](https://github.com/94youshin/icloud-reminders-mcp/blob/8e651f78304568fdd3265921805b35196fd4a53b/src/icloud_reminders_mcp/client.py)
  delegates its tag writes to pyicloud, so it is not independent protocol evidence.
- [go-icloud tag writer](https://github.com/portpowered/go-icloud/blob/694bb2eed1e80b2f786cd4bd548c3c7df895a993/pkg/icloud/reminder_hashtag_create.go)
  and [synthetic tag replay tests](https://github.com/portpowered/go-icloud/blob/694bb2eed1e80b2f786cd4bd548c3c7df895a993/tests/replay/reminder_hashtag_sdk_test.go).
- [rem's private ReminderKit bridge](https://rem.sidv.dev/docs/architecture/#private-reminderkit-bridge)
  and [another ReminderKit tag writer](https://github.com/falconbradley/claude-connector-apple-reminders#writing-tags).
  These use macOS frameworks rather than direct Windows web-service writes.
- [BusyCal's feature matrix](https://www.busymac.com/docs/busycal/70595-info-panel/)
  also lists native Reminders tags as unavailable to its integration. That is a
  product/API limitation, not proof that private CloudKit replication is impossible.

## Next experiment that could settle the direct route

Use a dedicated test reminder and record a before/after cloud snapshot around
adding a standalone tag in Apple's native app. Compare the complete changed
parent and child records, field types, references, revision/token metadata and
any accompanying assets. Keep account identifiers, content and credentials out
of committed fixtures.

Reproduce the observed change on a second dedicated reminder from Windows, then
verify all of the following on an actual iPhone: the tag chip appears, the tag
browser includes the reminder, a later date edit propagates, and another Apple
edit does not discard the tag. Repeat with removal, restart and a reused tag.
Cloud readback alone cannot satisfy these acceptance criteria.

Until that succeeds, do not reintroduce tag writes or automatic tag repairs as
working native tag sync. Reading and filtering existing Apple tags remain
available. The research itself performed no cloud writes.
