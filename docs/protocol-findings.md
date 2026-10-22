# iCloud Reminders protocol findings

These observations came from the repository's original live-account probes and
define the behavior the native C# connector must preserve.

**Tag-write status (2026-10-22): editing and automatic repair are removed.**
Existing tags remain readable and filterable. The prior Windows writer did not
produce native tags on the user's iPhone. See [tag-sync-research.md](tag-sync-research.md)
for the evidence and required device-level experiment.

- Reminders use the private `com.apple.reminders` CloudKit container and the
  `Reminders` custom zone. The API is undocumented and may change.
- Due dates are wall-clock fields encoded in a CloudKit timestamp as though the
  fields were UTC. `TimeZone` optionally anchors them; a missing zone floats in
  the device's current zone. Creation, modification, and completion dates are
  ordinary instants.
- Deletion is a soft `Deleted = 1` field update.
- Sync requests the combined `Reminder`, `List`, `Hashtag` and `RecurrenceRule`
  zone stream. Full reads and delta reads apply those records together in a
  SQLite transaction; tag changes do not trigger another full history read.
- List colors are JSON blobs; `daHexString` is the preferred display value.
- Tags are read-only. Apple-created records inspected on 2026-10-21 use a
  STRING name, Type (INT64, usually 0; an existing 1 was also observed), Imported,
  CreationDate, Deleted and a Reminder reference. Earlier Windows writers sent
  different child formats and an unsupported hashtagIDs resolution token.
  Matching the observed fields and reading them back did not establish native
  compatibility. No tag child writes or automatic repairs are now performed.
  Ordinary reminder edits still remove that earlier Windows token while
  preserving the other field-resolution tokens.
- List creates, renames, and deletes accepted by CloudKit did not propagate to
  Apple devices. This app does not expose those operations.
- A phone may replace linked field collections wholesale. Local writes carry a
  base `recordChangeTag`; mismatches become user-visible conflicts rather than
  last-write-wins overwrites.

## Two-factor sign-in

The optional Apple-device sign-in window lets Apple's own website implement the
current HSA2 bridge. It uses a separate InPrivate WebView2 profile, observes only
successful account-login/validation responses from `setup.icloud.com` and Apple
auth headers, and imports tokens when exposed, the stable client ID, browser user
agent and Apple cookies. The backend independently validates browser cookies with
`/validate`, checks pending verification and the returned account, then saves and switches the
session. A browser sign-in does not require a token-bearing `accountLogin` POST
body to be exposed. Responses are processed in order so a trusted validation is
not dropped while a previous response is being read. No JavaScript bridge or
password interception is installed. The native SMS path remains available. Cookies and rotated tokens are saved in the
existing chunked Windows Credential Manager entries, including after sync and
normal shutdown; startup resumes before attempting a password handshake.

Apple popups use separate WebViews in the same environment/profile. The host
assigns `NewWindow` while holding a deferral and preserves the original page and
its `window.opener` relationship. Simply setting `Handled = true` and navigating
the original view creates a closed dummy popup; see
[Microsoft's NewWindowRequested contract](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2newwindowrequestedeventargs).
The popup host has an account-free integration fixture. This verifies the
cross-window handoff, not Apple's delivery of an actual device prompt.

A completed web sign-in can expose Reminders while `hsaTrustedBrowser` is false
and `hsaChallengeRequired` is false. Requiring remembered browser trust here left
the authenticated website open indefinitely. The native handoff accepts this
combination only after server-side cookie validation, account matching, and a
read-only Reminders access check. The saved trust value is preserved, not changed
to true. An unfinished challenge or rejected data access still prevents import.

The following findings describe the native verification endpoints:

- **Apple has three verifiers; this connector implements two.** Trusted-device
  verification has moved to Apple's HSA2 **bridge**: the client opens a
  websocket to `websocket.push.apple.com`, takes an APNs push token, posts
  `auth/bridge/step/0`, waits for a pushed challenge, and answers it with an
  SRP-style prover exchange. pyicloud implements that in about 2,300 lines
  (`hsa2_bridge.py`, `hsa2_bridge_prover.py`) and selects it whenever Apple's
  boot data says `authInitialRoute == "auth/bridge/step"`, falling back to
  `POST verify/trusteddevice/securitycode` only for accounts Apple has left on
  the old verifier.

  For an account Apple has moved, that legacy endpoint answers **409** -- while
  Apple still displays the prompt on the user's devices, which is what makes it
  look like a code problem. It is not. No six digits will ever be accepted
  there. This was misdiagnosed three times as a header problem, a delivery
  problem, and a routing problem before the missing verifier was the answer.

  `POST verify/phone/securitycode` is a different endpoint, plain HTTP, and
  needs no bridge -- so a texted code is the only route this connector can
  currently complete directly, and `request_2fa` prefers it whenever Apple lists a
  trusted number. The Apple-hosted sign-in option handles the newer bridge.
- The `phoneNumber` payload is `{"id": <id>}` plus `"nonFTEU"` only when Apple
  included it. Both `PUT verify/phone` and `POST verify/phone/securitycode`
  take the same shape.
- **Apple's auth server must not be sent the headers its own responses hand
  back for setup.icloud.com.** `X-Apple-Webauth-Token` and
  `X-Apple-ID-Account-Country` are values Apple returns for a client to store
  and replay to `setup.icloud.com`; replaying them to `idmsa.apple.com` makes it
  answer **409** to every verification request. Neither exists until
  `signin/complete` has returned a session token, so a client that does this
  signs in normally right up to the moment a code is involved and then fails at
  everything -- which reads as "the code is invalid", "Apple would not send a
  code", and a bare HTTP 409, all from one cause. The header set that works is
  exactly pyicloud's `_get_auth_headers`: the OAuth/widget headers, the FD
  client info, `X-Apple-OAuth-State` and `X-Apple-Frame-Id` (both the client
  id), and whichever of `scnt`, `X-Apple-ID-Session-Id` and
  `X-Apple-Auth-Attributes` the session holds. Nothing else.
- `Referer` on auth requests is the idmsa **host** (`https://idmsa.apple.com`),
  not the auth path. `Origin` stays `https://www.icloud.com`.
- `Accept` varies per endpoint: `application/json, text/javascript` for the SRP
  requests, `application/json` for the verification requests, and
  `application/json, plain/text` for the SMS verifier.
- `signin/complete` answers **409** when Apple wants a second factor. That
  response also carries the challenge options: `trustedDeviceCount` and
  `trustedPhoneNumbers`. Keep them -- they decide where a code can be sent, and
  the challenge is thrown away by the next sign-in attempt.
- **Apple rotates `scnt` on every response from the auth endpoint** and refuses
  any later request that echoes a stale one. Every `idmsa` response must be read
  for a new `scnt`, `X-Apple-ID-Session-Id` and `X-Apple-Session-Token`,
  including the ones for requesting and submitting a code. A stale `scnt` is
  refused in a way that is indistinguishable from a wrong code.
- There are two delivery routes and they do not interoperate. A code pushed to a
  trusted device is requested with `GET verify/trusteddevice` and validated at
  `POST verify/trusteddevice/securitycode`; a texted code is requested with
  `PUT verify/phone` and validated at `POST verify/phone/securitycode`. Sending
  on one route and validating on the other is rejected, again exactly as a wrong
  code is. An account with no trusted device only has the phone route.
- **Both delivery endpoints answer with a non-2xx status and send the code
  anyway.** Observed on a live account: the app reported "Apple would not send a
  verification code" while the prompt was on the user's phone and two texts had
  arrived. The session is still unauthenticated at that point and Apple says so;
  that is not a refusal. Never infer "no code was sent" from the status of these
  two requests -- only a dead session (401/403/421) is a real failure.
- Only `service_errors[0].code == "-21669"` means the digits were wrong. Every
  other refusal is ambiguous -- it can equally mean the code was fine and the
  challenge or the route was not -- so it must not be reported as a typing
  mistake, and it is the signal that another route is worth trying.
- Apple's own `service_errors[0].message` is user-grade text ("Incorrect
  verification code.", "Enter the verification code displayed on your other
  devices."). Prefer it over anything inferred from a status code.
- Requesting a *text* mints a new code and retires the previous one, so a text
  must only ever follow the user asking for one. Re-requesting the trusted-device
  push re-serves the same challenge and is safe.
- Whether the 409 pushes the device code on its own is not observable from the
  client, so the app requests one and tracks which routes have actually
  delivered. That also means a user can legitimately hold two live codes at
  once, and verification tries every route that sent one before blaming the
  typing.
- Rebuilding the session re-runs SRP and voids any outstanding challenge. The
  background sync must not do that while a code is in flight.
- The exact statuses Apple returns here are undocumented. Verify behavior with
  account-free HTTP mocks and a disposable account before changing classifications.

The authentication and record formats need live Windows/account validation
whenever Apple changes the private service. Tests must use a dedicated account
and must never log passwords, verification codes, session tokens, record
contents, or credential-vault values.

## Account-free backend tests

The production C# backend uses Windows Credential Manager. Account-free tests
inject a memory vault and mocked HTTP handlers.

## Authentication lifecycle regression checks

The C# connector prepares the challenge without requesting delivery. The
code-entry screen requests one SMS; only an explicit resend requests another.
Verification and delivery controls are disabled together while either request
is in flight. The previous priming step sent an untracked SMS before the UI
requested a second one, creating a code-invalidation risk.

Startup first exchanges the saved session and trust tokens through accountLogin,
which recreates service cookies. Only a rejected or limited token falls back to
a password handshake. A network failure retains the saved tokens for retry.
Session data stays in Windows Credential Manager; closing the window does not
invoke sign-out. An explicit hsaChallengeRequired response is honored even when
hsaTrustedBrowser is true.

Mocked HTTP tests in tests/Reminders.Core.Tests/Program.cs cover the preparation,
delivery, verification and trust sequence, rotated headers, token restoration,
response limits and malformed/rejected verification responses. These checks
validate client behavior; a real Apple account must still confirm successful
sign-in followed by closing and reopening the built application.

### Accepted verification responses with HTTP 409

Do not classify a security-code response from its status alone. rclone's fix
https://github.com/rclone/rclone/commit/731f2a6 documents accepted verification
responses carrying HTTP 409 and an X-Apple-Session-Token. Issue 9730 also reports
securityCode.valid=true without that response header. The connector now proceeds
to trust when a 409 carries either positive signal, unless the body explicitly
rejects the code. A previously saved token does not qualify. Successful trust
and accountLogin confirming a trusted browser without an outstanding challenge
are still required before the UI is authenticated.

Verification diagnostics log the acceptance decision and token-presence boolean.
They do not log the code or token. Existing field-name-only logs cannot establish
whether an earlier 409 contained either acceptance signal.

### SMS mode and failures after verification

The verification payload must use the same `mode` as the delivery request. The
connector explicitly requests `sms`, so it must verify with `sms` even if Apple's
phone metadata has a different `pushMode`. Mocked checks cover this mismatch.

After an accepted code and successful trust request, accountLogin can still fail.
Network errors at this stage now identify that the code was accepted and include
the HTTP status when available. Verification diagnostics record the acceptance
decision and whether a token was issued, without recording either secret.
The certification screenshot alone cannot establish the reviewer's failure:
capture the tested package version and the app.log endpoint/status sequence.

### Fresh native sign-in versus browser restoration

Compared with released v0.5.5, saved-cookie support had also changed fresh
password sign-in: recreating its HTTP client reused the browser cookie container
and browser user agent. A new native password handshake now starts with a fresh
cookie container and the same native user agent as v0.5.5. It removes browser-only
restoration metadata while retaining the remembered trust token. Normal session
restoration and importing an Apple-hosted sign-in still preserve their cookies
and user agent. Account-free checks cover this separation and the existing SMS
delivery/verification flow. This is a concrete compatibility correction; live SMS
verification for the updated build still needs a result.
