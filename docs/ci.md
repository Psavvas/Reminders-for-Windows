# Continuous integration

CI runs account-free C# backend tests, checks app/MSIX versions and release ancestry,
validates workflow trust boundaries, and builds x64/ARM64 applications. Each
architecture produces a portable ZIP, optional Inno installer, and unsigned MSIX.
Packaging verifies that `resources.pri` has the final package identity and contains
the startup XAML. The x64 job also registers the extracted MSIX in Developer Mode,
activates it through Windows, checks that its window opens and stays alive, and
removes the test registration. ARM64 packages receive the resource checks; a native
ARM64 launch still needs an ARM64 machine.
Tags create a draft release only after all checks pass. Publishing a stable draft is
a separate decision. MSIX artifacts require signing or Store ingestion to install.
Public releases retain the established six-file layout: x64/ARM64 EXE installers,
portable ZIPs, and separate unsigned MSIX files. Keep the original casing:
`Reminders-for-Windows-x64-Setup.exe`, `Reminders-for-Windows-arm64-Setup.exe`,
`Reminders-for-Windows-x64.zip`, `Reminders-for-Windows-arm64.zip`,
`Reminders-for-Windows-x64.msix`, and `Reminders-for-Windows-ARM64.msix`.
Release notes distinguish MSIX submission packages from installable downloads.

For v1.0.0, the WinUI project version is `1.0.0` and the package manifest version
is `1.0.0.0`. Merge the release changes into `main`, then push `v1.0.0` at that
merged commit. CI rejects a tag/version mismatch or a tag outside `main`.
After the tag workflow finishes, review all six release assets, replace generated
notes with the checked-in [release notes](releases/v1.0.0.md), and publish the draft
as a stable release. Publishing on GitHub makes the update visible to the app's
updater; it does not submit or publish a Microsoft Store update.

NuGet restores use committed lockfiles. Cache access is read-only for PR/manual
builds, writable only on main pushes, and disabled for tags. Keys include architecture,
app/backend projects and lockfiles. Actions are pinned to full commit hashes.
Checkout does not persist credentials. Release jobs consume only their own run's
artifacts and cannot check out/build code with write permissions.

`scripts/test_ci_policy.py` includes mutation tests for these boundaries. Python is
a CI utility; application builds use .NET and Windows packaging tools. Review
NuGet lockfiles and dependency advisories alongside changes.
Live Apple interoperability is outside account-free CI and needs a disposable account.
