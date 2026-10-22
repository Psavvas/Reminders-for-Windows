# iCloud protocol implementation notes

Current for v1.0.0, reviewed October 22, 2026. These are observed implementation
details of private Apple interfaces, not a supported Apple service contract.

## Records and synchronization

- Reminders use the private `com.apple.reminders` CloudKit container. Titles and
  notes use Apple's document encoding; dates preserve floating wall-clock values
  and the reminder's time zone.
- Sync requests Reminder, List, Hashtag and RecurrenceRule records in one paginated
  zone-change stream. Batch application and cursor updates are transactional.
- Mutations use current revision tags and preserve fields outside the requested
  change. Outbox entries retain stable operation/record identities so retry can
  recognize a write committed before its response was lost.
- Known rejected uploads remain queued while unrelated tasks can sync. Later
  changes to the same task retain their order. A conflict preserves local and
  remote versions for review.
- Optional lookups recognize both `NOT_FOUND` and `UNKNOWN_ITEM`. A missing reply
  is not evidence of deletion. Recovery requires explicit acknowledgement of
  every requested record, including related tags and repeat rules.

### Repeats

The UI/RPC uses daily=1, weekly=2, monthly=3, yearly=4. Apple's stored Frequency
uses 0/1/2/3 for those units. Other frequencies and unsupported constraints must
not be interpreted as one of these four schedules.

The mapping was inspected in Apple's publicly served
[Reminders 2638Build16 script](https://www.icloud.com/applications/reminders2/2638Build16/en-us/main.js)
on October 20, 2026. Inspected SHA-256:
`cb1097a325a50c37e8d3e9f5b1395c0551e34744bd857c7dc243dde78951d8b8`.
Independent fixtures pin the numeric mapping rather than deriving expected values
from the application's converter.

Completing a supported repeat retains the active task and rule, advances its due
date and creates a separate completed history record without a repeat link.
Linked writes are atomic. Schedule editing also updates parent/rule links
atomically and preserves supported end conditions. A retry must not create a
second history record or advance twice.

### Tags and lists

Tags remain readable and filterable. Windows tag writing and automatic repair
are removed because the attempted writes did not produce native iPhone tags.
An acknowledgement or server readback does not establish Apple-device propagation.
See [tag-sync research](tag-sync-research.md) for evidence and acceptance criteria.

Old queued tag writes/tag-only conflicts are archived locally and retired from the
active queue. They do not trigger another repair or delete actual iCloud tags.
List creation, rename and deletion remain unsupported for the same requirement
to establish device-side propagation before offering a write feature.

## Apple-hosted sign-in

WebView2 hosts Apple's own page in an isolated InPrivate profile. Popup windows
use the same environment/profile and retain opener/message relationships. The
host does not inject password interception or bypass Apple's verification.

Successful `accountLogin` or `/validate` responses can trigger handoff. The native
client independently validates the browser cookies with the same user agent,
checks the account/challenge and saves rotated cookies. Browser-cookie restoration
does not require an exposed token-bearing `accountLogin` request body.

`hsaTrustedBrowser` expresses remembered browser trust, not whether the current
verification finished. A completed browser session can have that flag false while
`hsaChallengeRequired` is false. Before accepting that session, the app also requires
a successful read-only Reminders access check. The saved trust flag is preserved;
the client does not manufacture trust. Pending challenges are always rejected.

Cookies and session/trust tokens are stored in Windows Credential Manager and
retained on normal shutdown. Browser sessions resume through `/validate`; native
sessions resume through saved tokens and `accountLogin`. A network failure keeps
the saved state for retry. Explicit sign-out removes it and persists that choice.

## Native password and SMS flow

Native sign-in uses Apple's SRP challenge/proof flow. A fresh password handshake
starts with a clean cookie container/native user agent while retaining an existing
trust token. It must not inherit browser-only restoration metadata. See
[SRP reference vectors](srp-reference-vectors.md) for cryptographic evidence/limits.

Important invariants in `AppleAuth` and the authentication tests:

- Rotate `scnt`, session identifiers and auth attributes from every applicable
  response before sending the next request.
- Auth headers for `idmsa.apple.com` differ from account/service headers for
  `setup.icloud.com`. Do not replay setup-only webauth/country headers to auth.
- Keep the challenge's available routes. A trusted-device code and an SMS code
  use different delivery/verifier endpoints and cannot be interchanged.
- Native SMS delivery and verification both use the requested `sms` mode, even
  when Apple's phone metadata contains another `pushMode`.
- Code delivery is user initiated; only explicit resend requests another SMS.
  Background restoration must not restart SRP while verification is pending.
- Do not decide code acceptance from HTTP status alone. Accepted verification
  can return 409 with a fresh session token or `securityCode.valid=true`.
  Explicit rejection still wins; successful trust and account validation are
  required before considering native sign-in complete.
- Preserve Apple's useful error text and distinguish delivery, verification,
  account-login and transport failures. An ambiguous error is not proof that the
  user typed the wrong code or selected an incorrect account setting.

Apple's current device-verification bridge is handled by the hosted Apple page.
The native form supports SMS. App-specific passwords are not a verified
authentication route for this Reminders service; see [sign-in options](authentication.md).

The accepted-409 behavior is also described in
[rclone's authentication fix](https://github.com/rclone/rclone/commit/731f2a6).
Apple can change these private endpoints and statuses; HTTP mocks establish
client behavior, not universal live compatibility.

## Recovery and diagnostics

Selective recovery reads fresh cloud snapshots and atomically removes only
reviewed local changes. It must not issue cloud mutations. Queue/conflict version
checks run before and after lookup and inside the transaction; missing/denied
lookup results and cancellation cannot partially discard the selected tasks.

Sign-in diagnostics record stages, statuses and acceptance/token-presence flags.
They must not log passwords, verification codes, token values, credential-vault
contents or reminder contents. Review local logs before sharing them.

See [feature validation](feature-validation.md) for current test results and
remaining live-account/device checks, and [CI](ci.md) for packaging validation.
