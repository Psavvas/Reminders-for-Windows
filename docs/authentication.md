# Sign-in options

Current for v1.0.0, reviewed October 22, 2026.

## Apple-device sign-in

Choose **Sign in with an Apple device** to open Apple's iCloud sign-in page in
an isolated Microsoft Edge WebView2 window. Apple handles the password and offers
the verification methods available for the account. Choose **Trust** when offered.
After verification, the window closes and the native reminder interface opens;
**Continue in app** can finish the handoff if the web window remains open.

The app verifies the imported session before saving it. Cookies and session/trust
tokens are kept in Windows Credential Manager. Startup tries the saved session
before another sign-in. Apple can expire or revoke it, so remembering this PC
cannot guarantee that verification will never be requested again.

## Native sign-in

The native email/password form supports **Text me a code**. Enter the latest SMS
code sent to the selected trusted phone number. A code shown in a device prompt
is not interchangeable with a requested SMS code; use the Apple-hosted route for
Apple's current device-verification flow.

Use the regular Apple Account password for native sign-in. Reopen the setup guide
from the sign-in screen or **Settings → Sync & storage → iCloud setup and
troubleshooting** for account prerequisites and diagnostic actions.

## Why app-specific passwords do not replace this login

Apple documents app-specific passwords for access such as Mail, Calendar, and
Contacts. This app uses the modern Reminders CloudKit service, which requires a
verified iCloud session. No supported, verified app-specific-password route for
that service was found during the October 22 research.

Some older reports of success reused a session originally authenticated with the
main password. The pyicloud maintainer explained that the saved session, rather
than the app-specific password, supplied access; authentication failed after the
session expired. The original tester confirmed that result. Legacy CalDAV reminder
clients are a separate case and do not provide access to upgraded CloudKit lists.

- [Apple: app-specific passwords](https://support.apple.com/en-us/102654)
- [Apple: third-party Mail, Calendar, and Contacts authorization](https://support.apple.com/en-us/121539)
- [pyicloud maintainer's explanation](https://github.com/timlaing/pyicloud/issues/26#issuecomment-2781594215)
- [Original tester's correction](https://github.com/timlaing/pyicloud/issues/26#issuecomment-2781782055)
- [Legacy CalDAV client's compatibility limits](https://github.com/Lingnik/icloud-reminders-mcp#readme)

These findings come from documentation and source inspection, not a new live
credential test. See [protocol findings](protocol-findings.md) for implementation
details and [feature validation](feature-validation.md) for validation limits.
