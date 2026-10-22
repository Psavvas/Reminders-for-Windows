# Privacy Policy for Reminders for Windows

Effective date: October 5, 2026. Last updated: October 22, 2026.

Paul Savvas maintains Reminders for Windows, an independent Windows app that works with Apple iCloud Reminders. This policy describes the app's handling of personal information. The app is not affiliated with Apple or Microsoft. For privacy questions, contact hello@paulsavvas.com.

## Information the app accesses

Account and sign-in information: the app accesses the Apple Account identifier you enter, your password during sign-in, verification codes, authentication cookies and session tokens, and account information returned by Apple. This can include masked trusted phone numbers and verification identifiers. The app also generates a client identifier used in requests to Apple. Authentication exchanges are sent directly to Apple over HTTPS, using Apple's password proof protocol. Saved passwords and sessions are stored in Windows Credential Manager to support later sign-ins.

Reminder information: the app accesses and syncs your lists, reminders, titles, notes, tags, due dates, priorities, completion and deletion status, and associated identifiers and sync metadata. Reminder text may contain personal or sensitive information that you or someone sharing a list entered. A local cache also stores pending edits and versions needed to resolve sync conflicts.

Apple-device sign-in: an optional Microsoft Edge WebView2 window displays Apple's own iCloud sign-in page in an InPrivate profile. Apple handles password entry and device verification there. After verification, the app reads successful Apple account-login or validation exchanges and Apple cookies to save a verified session in Windows Credential Manager. It does not record browser passwords, verification codes, or sign-in page contents. The browser profile is separate from your normal browser.

Preferences and diagnostics: the app stores settings such as theme, list order, list appearance, pane sizes, sync interval, and notification preferences. Local logs record technical events, request status, and errors. Error messages may include account-related details or local file paths. Logs are not automatically uploaded to the developer.

Update requests: the app checks GitHub for releases on launch and periodically while running, and when you request a check. GitHub receives ordinary network information, including your IP address, request time, and an app version in the request header. Installer downloads also identify the requested release and architecture. The updater does not send reminder contents or Apple sign-in credentials to GitHub.

## How information is used

The app uses this information to sign in to iCloud, display and edit reminders, sync changes with Apple, support cached use and queued offline edits, resolve sync conflicts, remember preferences, show due-date notifications, and check for app updates. Existing iCloud tags are displayed and used for filtering; tag editing is unavailable. Sample reminders in the UI demo are kept in memory. The separate local test account stores sample data and preferences on this PC. Neither mode sends sample reminders to iCloud.

The app has no advertising, behavioral analytics, or automatic developer telemetry. Paul Savvas does not operate a server that receives your reminder database or Apple credentials from the app, and does not sell personal information obtained through the app.

## Services that receive information

Apple receives the account authentication exchanges and reminder changes required for iCloud syncing, and supplies the account and reminder data the app displays. Apple also receives ordinary network information, such as your IP address and request headers. Apple handles that information under its own policies and account settings: https://www.apple.com/legal/privacy/.

GitHub handles release checks, downloads, and visits to the project's website under its own privacy policy: https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement. Clicking a website link opens your browser; the browser and website may process information such as cookies according to their own settings and policies.

Windows handles notifications and credential storage. Notifications can contain reminder titles and due dates and may appear on your lock screen, depending on your Windows settings. Microsoft also processes Store purchases, installations, updates, and related information under its own privacy statement: https://privacy.microsoft.com/privacystatement.

If you contact the developer, the information you choose to send, such as your email address, message, screenshots, or logs, is used to respond and investigate the issue. Support correspondence may be retained as needed to handle the request, maintain support records, and meet applicable legal obligations. GitHub issues are public: review anything you share and never include passwords, verification codes, or session tokens. Information may also be disclosed where required by applicable law.

## Local storage and security

For unpackaged installations, reminder data and the account identifier are normally stored in %LOCALAPPDATA%\RemindersSync\cache.db. Logs are in %LOCALAPPDATA%\RemindersSync\logs. Interface preferences and downloaded updates are under %LOCALAPPDATA%\RemindersForWindows. Windows may redirect these locations into package-specific storage for an MSIX installation.

The reminder database is not encrypted by the app. Its protection depends on your Windows account, device security, and any disk encryption you enable. Passwords and session tokens are stored separately in Windows Credential Manager, not in the SQLite database. Network communication with Apple and GitHub uses HTTPS. No storage or transmission method can guarantee complete security.

## Retention and your choices

Local reminder data, pending edits, preferences, and saved credentials remain until removed or replaced; they have no fixed expiration period. Logs rotate by size and keep a current and previous log. Downloaded installers may remain in the app's update folder. Uninstalling the app does not reliably remove all cached data, preferences, or Credential Manager entries. Windows backups may retain copies separately.

You can view and edit your reminders in the app or through Apple's supported clients. Synced changes are sent to Apple. Deleting a reminder can leave recoverable records in the Deleted view, local cache, or Apple's services; deletion in this app is not a promise of immediate permanent erasure. Apple controls retention and removal of information held in iCloud.

You can disable due-date notifications in Settings and adjust lock-screen notifications in Windows. Closing the app stops its running sync and update checks. An installation managed by Microsoft Store can still be updated by Windows independently.

Settings → Account → Sign out removes the app's saved password, session cookies, and trust tokens while retaining cached reminders and queued edits. Settings → Sync & storage → Discard unsynced changes lets you select tasks with queued uploads or conflicts: new local-only tasks are removed locally, and existing tasks are restored from fresh iCloud copies. Settings → Sync & storage → Clear app cache removes the complete local reminder cache, including pending uploads and conflicts, while retaining saved sign-in and app preferences. These recovery actions require confirmation, do not delete records in iCloud, and do not erase logs, downloaded installers, or device backups.

To remove local app data, fully close the app first, and save or sync any edits you want to keep. Remove the app's RemindersSync and RemindersForWindows data folders, or the corresponding package-specific data. In Windows Credential Manager, remove only this app's generic credentials for your Apple Account: their names include .com.paulsavvas.reminders-sync.password or .com.paulsavvas.reminders-sync.session, including session chunk entries. Removing the cache discards unsynced edits and does not delete reminders already stored in iCloud. Contact the developer if you need help locating these files or entries.

## Privacy requests and children

Depending on the law that applies to you, you may have rights to access, correct, or delete personal information, or object to certain processing. Contact hello@paulsavvas.com about information you have sent to the developer or for help managing local app data. The developer cannot retrieve your device's private cache or change Apple's records remotely; requests about data held by Apple, GitHub, or Microsoft should also be directed to that provider.

The app is not directed at children under 13. If you believe a child has provided personal information to the developer through a support request, contact the address above so it can be addressed.

## Changes to this policy

This policy will be updated when the app's data handling changes. The date above identifies the revision. A copy is available before sign-in and in Settings, including offline. Updated app releases include the policy applicable to that version; the repository contains the latest published revision.
