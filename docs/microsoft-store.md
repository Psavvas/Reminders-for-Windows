# Microsoft Store distribution

## Automatic publishing

Publishing a stable GitHub release for a `vMAJOR.MINOR.PATCH` tag on `main`
triggers **Publish to Microsoft Store**. The workflow checks both release MSIX
files against the tag and Partner Center app identity, then submits them together
to [Reminders for Windows (9PH6GW0M6G86)](https://apps.microsoft.com/detail/9PH6GW0M6G86).
Microsoft signs the packages for Store distribution and publishes the update
after it passes certification.
[Microsoft's submission API](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions)

In the GitHub release body, use a `## What's new` section for customer-facing
changes. The workflow also accepts GitHub's generated **What's Changed** section
or a standalone changelog in the body. It copies that text into **What's new in
this version** for the English Store listings and links to the full GitHub notes
if the text exceeds the Store's 1,500-character limit.
[Microsoft's listing guidance](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info#whats-new-in-this-version)

### One-time setup

1. In [Partner Center → Account settings → Tenants](https://partner.microsoft.com/dashboard/v2/account-settings/organization/tenant-management#developer),
   create or associate a Microsoft Entra tenant with the Windows app
   developer account. Sign in as the tenant's global admin and open **Account
   settings → User management → Microsoft Entra applications**. Add a dedicated
   application with the **Manager** role and create a client secret. Copy the
   secret **value** when it is displayed.
   [Microsoft's API authentication setup](https://learn.microsoft.com/en-us/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services#step-1-associate-an-azure-ad-application-with-your-partner-center-account)
2. In [GitHub → Settings → Environments](https://github.com/Psavvas/Reminders-for-Windows/settings/environments),
   open the `microsoft-store` environment. Its deployment rules allow `main` for
   manual runs and `v*` tags for release runs. Limit who can create or update
   those tags to release maintainers. Add the following environment secrets.

   | Secret | Value |
   | --- | --- |
   | `STORE_TENANT_ID` | Microsoft Entra directory (tenant) ID |
   | `STORE_CLIENT_ID` | Microsoft Entra application (client) ID |
   | `STORE_CLIENT_SECRET` | Client secret value |

3. Merge the workflow into `main` before tagging the next release. Under
   **Actions → Publish to Microsoft Store → Run workflow**, select `main`, enter
   a published tag with both MSIX assets (for example `v1.1.0`), and enable
   **check-only** to verify credentials, notes, and packages without submitting.

### Monitor or retry

The Actions run records the submission ID and checks ingestion. Certification
and Store publication continue in Partner Center. If an earlier submission is
still in progress, run the workflow manually on `main` with the newer release
tag after the earlier one finishes. If a run stops after updating its draft, a
retry can resume that draft using the same GitHub assets. If it stops immediately
after creating a draft, check the logged submission ID in Partner Center and
clear that incomplete draft before retrying. Partner Center also shows the
certification report if Microsoft rejects a submission.

## Package signing and identity

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

GitHub Actions builds the submission packages for both architectures using
`-Store`. Release-tag builds attach `Reminders-for-Windows-x64.msix` and
`Reminders-for-Windows-ARM64.msix` alongside the installers and portable ZIPs.
For a manual submission, download both MSIX files from the release or extract
them from the workflow artifacts, then upload the files unchanged.

## Installing from GitHub versus the Store

Installing from the Microsoft Store works because Microsoft signs the package
delivered by the Store. Publishing that app does not sign separate MSIX files
uploaded to GitHub. An unsigned GitHub MSIX remains a submission input and Windows
will refuse to install it directly, even if its identity matches a published app.

For installation, use the Store listing or the matching x64/ARM64 `Setup.exe`
from GitHub. Portable ZIPs are another option. The separate unsigned MSIX files
are submission files for Partner Center.

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
