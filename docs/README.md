# Documentation

Current for Reminders for Windows v1.0.0.

## Using the app

| Guide | Contents |
| --- | --- |
| [Getting started](getting-started.md) | Requirements, setup, sync, Settings and recovery |
| [Sign-in options](authentication.md) | Apple-hosted verification, native SMS, remembered sessions and app-specific-password limits |
| [Local test account](demo-account.md) | Persistent sample data without an Apple Account |
| [v1.0.0 release notes](releases/v1.0.0.md) | Changes, downloads, updates and compatibility |
| [Privacy policy](../PRIVACY.md) | Data access, local storage and removal choices |

Install from the Microsoft Store listing or use the GitHub release's matching
x64/ARM64 `Setup.exe`. Portable ZIPs contain the complete app folder. The separate
unsigned MSIX downloads are for publisher upload to Partner Center and require
signing before direct installation.

Tags are view/filter only. Repeating tasks can be created and edited for supported
schedules. Selective unsynced-change recovery is under Settings → Sync & storage.

## Development and distribution

| Guide | Contents |
| --- | --- |
| [Native Windows frontend](native-windows-frontend.md) | Current controls, pane behavior, demo previews and manual UI checks |
| [Continuous integration](ci.md) | Tests, packaging, release tags and artifact boundaries |
| [Microsoft Store distribution](microsoft-store.md) | Package identity, signing, Store submission and packaged launch |
| [Feature validation](feature-validation.md) | Current automated evidence and remaining live checks |
| [Security verification](../SECURITY_AUDIT.md) | Commands and areas needing independent/live review |

## Protocol research

| Reference | Contents |
| --- | --- |
| [iCloud protocol findings](protocol-findings.md) | Record/sync invariants, authentication and recovery behavior |
| [SRP vectors](srp-reference-vectors.md) | Independent cryptographic fixtures and unresolved edge cases |
| [Tag-sync research](tag-sync-research.md) | Evidence for keeping tag writes disabled |

Research pages identify observed behavior and its limits. They are not a public
Apple API contract. Report issues with the app version and relevant diagnostics,
after removing credentials, tokens and personal reminder contents.
