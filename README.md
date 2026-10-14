<div align="center">
<img src="src/Reminders.WinUI/Assets/icon-v2.png" width="112" alt="Reminders app icon">

# Reminders for Windows

**A native WinUI 3 client for iCloud Reminders, written in C#/.NET.**
</div>

Apple does not ship Reminders for Windows. This project provides an offline-capable
Windows client with background sync and due-date notifications. The UI and iCloud
engine run in one process. Rust, a separate sidecar, Node.js, and a browser runtime
are no longer required.

> This independent project is not authorized, sponsored, endorsed, or otherwise
> approved by Apple Inc. It uses private, undocumented iCloud interfaces that may
> change or stop working without notice. Read [LEGAL.md](LEGAL.md) before use.

Read the [Privacy Policy](PRIVACY.md) for account data, iCloud syncing, local
storage, notifications, update checks, and removal instructions. The same policy
is available offline from the sign-in screen and **Settings → Privacy policy**.

## Develop in Visual Studio

Open **Reminders.Windows.sln** in Visual Studio 2022 or newer with .NET desktop
development and Windows Application Packaging Project tools installed. Select
**x64** or **ARM64** and set **Reminders.Package** as the startup project to debug
the MSIX application. Configure a local signing certificate in the packaging
project before deploying; no signing key is committed to this repository.

The solution contains the WinUI app, its C# backend library, the MSIX packaging
project, and account-free backend/updater tests. Existing SQLite caches and Windows
Credential Manager entries retain their formats and names.

## Repository layout

- `src/`: WinUI application, C# backend, and MSIX packaging project.
- `tests/`: account-free backend, layout, and updater checks.
- `scripts/`: development, build, packaging, and CI verification tools.
- `docs/`: setup, architecture, protocol findings, and historical reviews.

The retired Rust backend has been removed; its source remains in Git history.

## Quick start: demo mode

With the .NET 8 SDK or newer installed on Windows:

```powershell
.\scripts\run-windows.ps1 -Demo
```

Demo mode uses in-memory sample data, without opening the real cache or connecting
to iCloud. For a normal unpackaged development launch, omit `-Demo`.

## Before signing in

On first launch, a five-screen guide explains this version's account setup:
iCloud Reminders enabled, two-factor authentication with a trusted phone number
for SMS codes, Advanced Data Protection off, web data access allowed, and account
setup/updated terms completed on iCloud.com. Turning Advanced Data Protection off
changes encryption protection for the whole iCloud account; the guide explains
that choice and links to Apple's help. The app does not inspect or change these
settings for you.

Completion is remembered on this PC. Reopen the guide from the sign-in screen,
an error's **Review iCloud setup** button, or **Settings → iCloud setup and
troubleshooting**. Error guidance preserves Apple's message and offers next
steps without guessing an unreported cause. Troubleshooting can copy the error
and open the actual diagnostic-log folder, including in a Store installation.
Opening or closing the guide does not send another verification code or restart
the current sign-in session.

## Build and package

```powershell
.\scripts\check-prereqs.ps1 -Msix
.\scripts\build-msix.ps1 -Architecture x64
.\scripts\build-msix.ps1 -Architecture ARM64
```

Packages are written to `dist-msix`. Windows requires a signed MSIX; use a
certificate whose publisher matches the package identity:

```powershell
.\scripts\build-msix.ps1 -Architecture x64 -CertificateThumbprint YOUR_THUMBPRINT
```

Visual Studio's **Create App Packages** command also builds the package from the
same checked-in manifest and assets. For Microsoft Store distribution, associate
the packaging project with the assigned Store identity. Package versions must
increase for upgrades. See [Getting started](docs/getting-started.md).

Portable builds remain available through `scripts/build-windows.ps1`; the optional
Inno Setup installer remains available through `scripts/build-installer.ps1` for
existing users. MSIX installations use Windows/Store package deployment for
upgrades. The app's GitHub updater executes installers only for Inno installations;
MSIX and portable builds show a release link.

## App updates

Installer builds check GitHub for updates on launch and every six hours while
running. A banner offers **Install and restart** when a newer stable release is
available. You can also use **Settings → Check for updates**. The app downloads
the matching x64 or ARM64 installer, checks its size and GitHub SHA-256 digest,
then upgrades the existing installation and reopens. Reminder data, queued
changes, preferences and sign-in credentials are kept. Save unfinished edits
before restarting. Failed checks never prevent normal app use.

Install a build containing the updater once to enable it. Portable, development
and MSIX builds offer a link to the release instead of running an installer.

To ship an update, increase `<Version>` in the WinUI project and the MSIX version
in `src/Reminders.Package/Package.appxmanifest` together, commit the changes,
and push a matching tag (for
example `v0.4.0`). CI creates a draft with both installers. Review and publish
that draft as a stable GitHub release; drafts and prereleases are ignored by
the updater. Committing code alone does not distribute an update.

## What works

| Feature | Support |
|---|---|
| Lists and reminders | Read lists; read and write reminders |
| Titles, notes, due dates, priorities, completion and flags | Read and write |
| Smart lists | Today, Upcoming, All, Completed and Deleted |
| Search and sorting | Local SQLite queries |
| Tags | Read and filter |
| Offline use | Cached reads; edits queue for upload |
| Automatic sync | Launch/return, after edits, and every 10 minutes by default |
| Authentication | Apple ID, SMS 2FA, terms acceptance and session restoration |
| Conflicts | Preserve local/remote versions for explicit resolution |
| Windows integration | Native controls, theme, badges and due notifications |

Creating or changing lists is unsupported: CloudKit Web Services list records do
not propagate to Apple devices. Two-factor sign-in requires a trusted phone number
and a texted code. Apple's HSA2 device-prompt bridge is not implemented. See
[protocol findings](docs/protocol-findings.md).

## Architecture

```text
Reminders.WinUI (C# / WinUI 3)
        | direct asynchronous calls and events
Reminders.Core (C# / .NET 8)
        +-- Apple SRP authentication and Windows Credential Manager
        +-- CloudKit records, text documents and sync/conflict handling
        +-- SQLite cache, durable outbox and notification planning
Reminders.Package (Visual Studio MSIX project)
```

Cache and account operations execute off the UI dispatcher. Local reads remain
available during network requests. Edits and outbox entries commit together;
upload acknowledgments preserve and rebase newer queued edits. Sync has bounded
responses, pagination and a ten-minute deadline. Due dates preserve Apple's
floating wall-clock representation and daylight-saving behavior.

Unpackaged data is under `%LOCALAPPDATA%\RemindersSync`; Windows may virtualize
LocalAppData for packaged installations. Passwords and session tokens remain in
Windows Credential Manager, never SQLite. Review logs before sharing. See
[the frontend notes](docs/native-windows-frontend.md).

## Tests

```powershell
dotnet run --project .\tests\Reminders.Core.Tests\Reminders.Core.Tests.csproj -c Release
dotnet run --project .\tests\AppUpdater.Tests\AppUpdater.Tests.csproj -c Release
dotnet build .\src\Reminders.WinUI\Reminders.WinUI.csproj -c Release -p:Platform=x64
```

Tests are account-free and cover independent SRP vectors, credential chunk recovery,
Apple text documents, cache/outbox behavior, DST dates and mocked HTTP. Live iCloud
interoperability requires a disposable account; see [SECURITY_AUDIT.md](SECURITY_AUDIT.md).

## Credits

Maintained by Paul Savvas. Package publisher: [paulsavvas.com](https://paulsavvas.com).
Apple and iCloud are trademarks of Apple Inc., registered in the U.S. and other
countries and regions. Windows is a trademark of Microsoft Corporation.
Product names describe compatibility only; this project is not affiliated with
Apple or Microsoft.
