# Getting started on Windows

## Requirements

- Windows 10 1809 or newer, x64 or ARM64.
- Microsoft Edge WebView2 Runtime for Apple's hosted sign-in window.
- .NET 8 SDK or newer.
- Visual Studio 2022 or newer with .NET desktop development and Windows Application
  Packaging Project tools for packaged debugging and the package designer.
- Windows SDK with MakeAppx and SignTool for command-line MSIX packaging/signing.

The app and backend are C#. Python is used only by CI policy tests.

## Visual Studio

Open `Reminders.Windows.sln`. Select x64 or ARM64 and set `Reminders.Package` as
the startup project. Its project reference builds and publishes `Reminders.WinUI`
with `Reminders.Core` included. The checked-in `Package.appxmanifest` supports the
manifest designer and **Publish → Create App Packages** workflow.

Configure a signing certificate in the package project before deploying with F5.
Use a certificate whose subject matches the manifest publisher. Trust a development
certificate on your test device as appropriate. No private signing key is committed.
Unsigned packages can be built for inspection but cannot be installed.

For unpackaged debugging, set `Reminders.WinUI` as the startup project. The
`Reminders.Core.Tests` and `AppUpdater.Tests` console projects can run separately.

## Demo and development launches

```powershell
.\scripts\run-windows.ps1 -Demo
.\scripts\run-windows.ps1
```

Demo mode displays sample data without opening the real cache, reading credentials,
or contacting iCloud. Debug builds accept `REMINDERS_DATA_DIR` for a separate test
cache and UI-preferences directory. Release builds use the normal LocalAppData location.

To verify the first-run guide without using your normal preferences, launch a
Debug build with `REMINDERS_DATA_DIR` set to an empty test directory. Complete
the guide and reopen the app with the same directory to verify it stays completed.
The Debug-only `--preview-sign-in-error` argument displays an offline sample of
an account-login failure after an accepted code; no Apple request is made.
Use its **Review iCloud setup** button to inspect the troubleshooting, error-copy,
and log-folder actions. This preview is not included in Release builds.

## MSIX distribution

For public distribution without asking users to trust a development certificate,
use the [Microsoft Store submission workflow](microsoft-store.md). Microsoft signs
the approved app; `build-msix.ps1 -Store` prepares packages using your assigned
Partner Center identity and writes them to `dist-store`.

```powershell
.\scripts\check-prereqs.ps1 -Msix
.\scripts\build-msix.ps1 -Architecture x64
.\scripts\build-msix.ps1 -Architecture ARM64
```

The script publishes the self-contained application and packages it using the
same manifest and logos as Visual Studio. Outputs are
`dist-msix\Reminders-for-Windows-x64.msix` and
`dist-msix\Reminders-for-Windows-ARM64.msix`. `-SkipBuild` packages an existing
portable publish directory.

```powershell
.\scripts\build-msix.ps1 -Architecture x64 -CertificateThumbprint YOUR_THUMBPRINT
Add-AppxPackage .\dist-msix\Reminders-for-Windows-x64.msix
```

Signing uses `Cert:\CurrentUser\My` and automatically adopts the certificate subject
as Publisher. `-IdentityName` and `-Publisher` support an assigned Store identity
for unsigned artifacts. Keep identity/publisher stable and raise both the WinUI
`<Version>` and manifest Identity Version (four components) when shipping updates.
Windows package deployment handles MSIX upgrades; the app does not run an Inno
installer from an MSIX installation.

Windows applies package data virtualization: installing MSIX does not automatically
import an unpackaged app's LocalAppData. Verify migration on a test account before
replacing a production installation. Credential Manager target names remain stable.

## Portable and existing installer builds

```powershell
.\scripts\build-windows.ps1 -Architecture x64
.\dist-windows\Reminders.exe
.\scripts\build-installer.ps1 -Architecture x64
```

Keep the publish folder together; it contains the WinUI and .NET runtimes, SQLite
native library, resources and `Reminders.Core.dll`. ARM64 output is under
`dist-windows-arm64`. The Inno installer supports existing installations/updaters.

## Verification and troubleshooting

```powershell
dotnet run --project .\tests\Reminders.Core.Tests\Reminders.Core.Tests.csproj -c Release
dotnet run --project .\tests\AppUpdater.Tests\AppUpdater.Tests.csproj -c Release
dotnet build .\src\Reminders.WinUI\Reminders.WinUI.csproj -c Release -p:Platform=x64
```

Choose **Sign in with an Apple device** for Apple's hosted verification page.
Apple offers the methods available for your account. Choose **Trust** when offered;
the app saves the verified session in Windows Credential Manager. After sign-in,
the window returns to the native app; use **Continue in app** if needed. Native
email/password sign-in also supports **Text me a code**, using the latest SMS.
App-specific passwords cannot replace this flow. See [sign-in options](authentication.md).

Cached reminders remain available during outages. Upload errors leave edits queued;
conflicts retain both versions for review. **Sync now** retrieves changes from Apple
devices; the default automatic interval is one minute, configurable in Settings.

Under **Settings → Sync & storage**, **Discard unsynced changes** lets you select
individual tasks with queued uploads or conflicts. New local-only tasks are removed
and existing tasks revert to fresh iCloud copies. It does not delete cloud tasks.
**Clear app cache** resets the whole local reminder cache and requires confirmation
if unsynced edits would be lost. **Settings → Account → Sign out** removes saved
sign-in while retaining cached reminders and queued edits.

Use **Settings → About → Check for updates** to check inside the Settings window.
Existing iCloud tags are visible and filterable; create and edit them in Apple
Reminders. Daily, weekly, monthly and yearly repeats can be created and changed in
the native reminder editor; unsupported custom schedules remain protected.

Unpackaged logs are under `%LOCALAPPDATA%\RemindersSync\logs\app.log`; packaged
installations may have a virtualized location. Include the app version and relevant
error when reporting issues. Review logs before sharing them.
