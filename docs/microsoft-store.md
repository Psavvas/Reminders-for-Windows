# Microsoft Store distribution

Microsoft signs MSIX apps distributed through the Store. Users install the
approved app without importing a developer certificate, and Windows handles updates.
The unsigned packages produced by this repository are submission inputs, not
trusted downloadable installers. Microsoft must approve and publish the app first.

## Account and app identity

1. Create and verify your developer account at https://storedeveloper.microsoft.com.
2. Reserve an available app name in Partner Center.
3. Copy the three values from **Product identity**: **Package/Identity/Name**,
   **Package/Identity/Publisher**, and **PublisherDisplayName**.

Use the values assigned to your actual app, including the exact publisher string.
Do not invent an identity or use the repository's development publisher. Store
identity differs from existing sideloaded builds; test local-data migration before
replacing an existing installation.

## Build submission packages

The checked-in manifest now uses the reserved app's Partner Center identity:

- Name: `PaulSavvas.RemindersforWindows`
- Publisher: `CN=3E985065-3B5B-4175-965A-08CB432B2EFB`
- PublisherDisplayName: `Paul Savvas`

These values are public metadata, not credentials. Run from the repository root:

```powershell
.\scripts\build-msix.ps1 -Architecture x64 -Store
.\scripts\build-msix.ps1 -Architecture ARM64 -Store
```

Upload both `.msix` files from `dist-store` on the submission's **Packages** page.
Partner Center accepts individual MSIX packages and selects the matching
architecture for each customer's device. Store builds read the assigned identity
from the manifest and keep submission files separate from sideloading outputs.
Explicit identity parameters remain available for another reservation. Ordinary
non-Store command-line builds retain the legacy development identity by default.

GitHub Actions builds the Store submission packages for both architectures using
`-Store`. After merging, wait for the successful workflow on the merged commit and
download its x64 and ARM64 artifacts. Extract the artifact ZIPs and upload the two
`.msix` files from `dist-store` unchanged. No certificate, manual signing, or
repacking is needed. Release-tag builds place both packages and an explanatory
README in `Reminders-for-Windows-Store-submission.zip` on the draft release. Use
the latest build rather than an older development-identity MSIX.

## Installing from GitHub versus the Store

Installing from the Microsoft Store works because Microsoft signs the package
delivered by the Store. Publishing that app does not sign separate MSIX files
uploaded to GitHub. An unsigned GitHub MSIX remains a submission input and Windows
will refuse to install it directly, even if its identity matches a published app.

For installation, use the Store listing or the matching x64/ARM64 `Setup.exe`
from GitHub. Portable ZIPs are another option. The Store-submission ZIP is for the
publisher to extract and upload to Partner Center, not an end-user installer.
GitHub release publication does not update the Store listing automatically.

Direct MSIX distribution outside the Store requires signing with a certificate
trusted by the target device. A self-signed certificate requires a separate trust
step and does not provide public trust. See Microsoft's
[MSIX signing overview](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview).

## Product display name

The application and package display name is **Reminders for Windows**. The
manifest uses the exact product identity supplied from Partner Center for this
product, including the casing of `PaulSavvas.RemindersforWindows`. Upload its
packages to that product's submission and select the reserved display name for
the Store listing. The older `PaulSavvas.CloudReminderSync` identity belongs to
a different package family; packages built with it cannot be submitted as this
product. For future display-name changes on the same product, retain the assigned
package identity and publisher.

Microsoft's naming guidance permits plain-text compatibility phrases such as
"for Windows" when they do not imply Microsoft endorsement. This is naming
guidance, not trademark clearance. Store availability and any third-party
trademark rights still need checking. Keep the Apple and Microsoft independence
notice in the listing.

- [Microsoft's app trademark guidance](https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/trademark-and-copyright-protection)
- [Manage MSIX app names](https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/pwa/manage-app-name-reservations)

Alternatively, associate **Reminders.Package** with your app in Visual Studio and
use **Publish → Create App Packages → Microsoft Store**. The wizard can create a
`.msixupload` submission file. Use the associated Store identity for later builds.

## Submit and publish

### Resource identity and packaged launch

The portable build contains `Reminders.pri`, whose primary resource map is named
`Reminders`. Giving that executable package identity changes WinUI/MRT resource
lookup. The MSIX needs a package-level `resources.pri` with a primary map matching
`Package/Identity/Name`. Copying portable output into an MSIX without creating
that index can crash while initializing application XAML, before showing a window.
Renaming the file alone does not correct the primary map.

`build-msix.ps1` uses the Windows SDK's MakePri tool to import
the portable index into `resources.pri` under the final package identity, preserving
embedded XAML and framework resources. It verifies the identity and startup XAML
before packing. See [Microsoft's MRT packaging guidance](https://learn.microsoft.com/en-us/windows/uwp/app-resources/using-mrt-for-converted-desktop-apps-and-games)
and the [WinUI identity-dependent PRI lookup report](https://github.com/microsoft/microsoft-ui-xaml/issues/10856).

Build fresh packages from the release commit; do not use `-SkipBuild` with an
older publish directory. Test the actual submission payload
in a clean Windows user or VM with Developer Mode enabled:

```powershell
.\scripts\test-msix-launch.ps1 -PackagePath .\dist-store\Reminders-for-Windows-x64.msix
```

The test registers the extracted payload without signing, launches the app through
Windows activation, verifies its window stays alive, and removes the registration.
It refuses to replace an existing installation and requires the package to match
the machine's native architecture. Run the ARM64 package test on an ARM64 machine
as well. CI runs the launch test on x64 and resource validation on both architectures.
This test covers package identity and startup; it does not replace certification
or testing the signed Store installation on a clean machine.

Startup failures are logged before application resources and the main window are
initialized. For packaged installs, Windows redirects the app's local-data paths;
look for `RemindersSync\logs\app.log` inside the package's local data. If a future
report still shows `0xc000027b`, download the linked `.evtx` and inspect adjacent
events: that exception code alone does not identify the underlying XAML error.

### Privacy policy

Answer **Yes** to whether the product accesses, collects, or transmits personal
information: it accesses Apple Account information and reminder contents, caches
them locally, and syncs with Apple. Local storage still counts as accessing
personal information.

The policy is maintained in [PRIVACY.md](../PRIVACY.md) and embedded in the app,
accessible before sign-in and through **Settings → About → Privacy policy**, even offline.
Settings provides a Back button to return without losing unsaved preferences.
Keep the policy current with any changes to data handling.

Enter this public URL in Partner Center:

https://github.com/Psavvas/Reminders-for-Windows/blob/main/PRIVACY.md

Verify the chosen URL opens while signed out before submitting. The app does not
create a separate developer-hosted account or upload reminder databases to a
developer server; that does not remove the need to disclose its access to personal
information. See Microsoft's [privacy policy requirements](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies#105-personal-information).

### Submission details

Complete the Store listing, privacy policy, age rating, and certification notes.
Explain the `runFullTrust` capability: the desktop WinUI app runs its C# iCloud sync
engine, SQLite cache, Windows Credential Manager integration, and notifications.
Tell reviewers that demo mode is available without an Apple account, while live
sync requires an Apple account and verification. Describe the independent app
accurately; the existing Apple affiliation disclaimer applies to the listing too.

Microsoft may request changes during certification. After approval and publication,
share the Store listing link. The unsigned submission file alone does not resolve
the certificate error in Windows App Installer. Increase the app and MSIX versions
for each subsequent update and submit new packages using the same Store identity.

Official references:

- [Get started with Microsoft Store](https://learn.microsoft.com/en-us/windows/apps/publish/get-started)
- [Upload MSIX packages](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages)
- [MSIX signing guide](https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide)
