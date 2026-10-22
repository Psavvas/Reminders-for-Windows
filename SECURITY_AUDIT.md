# Security and release verification

The current application runs `src/Reminders.WinUI` and `src/Reminders.Core` in
one process. This index describes checks for the current C# implementation; it is
not an independent security audit. The obsolete audit of the removed Rust backend
has been retired; Git history retains that document.

## Current checks

Run from the repository root on Windows:

```powershell
dotnet run --project .\tests\Reminders.Core.Tests\Reminders.Core.Tests.csproj -c Release
dotnet run --project .\tests\AppUpdater.Tests\AppUpdater.Tests.csproj -c Release
dotnet build .\src\Reminders.WinUI\Reminders.WinUI.csproj -c Release -p:Platform=x64
.\scripts\build-msix.ps1 -Architecture x64
.\scripts\build-msix.ps1 -Architecture ARM64
```

The account-free tests cover SRP reference vectors, credential/session recovery,
bounded and mocked HTTP flows, cache/outbox and conflict handling, dates across
DST, reminder grouping, pane sizing, and update downloads. Build scripts and CI
use the C# projects exclusively. Review changes to the NuGet lockfiles and scan
dependencies as part of release preparation.

## Remaining release validation

- Exercise live sign-in, SMS 2FA, trusted restart, sign-out, expired sessions,
  and terms-required responses with a disposable Apple account. Mocked HTTP
  tests do not establish live interoperability.
- Compare full/delta sync with an Apple device and test reminder mutations and
  conflict resolution on disposable data.
- Verify timed and all-day reminders across DST boundaries and Windows time zones.
- Test signed MSIX installation and upgrades on a test device, including local
  data migration when changing package identities.
- Check that passwords and session tokens stay in Windows Credential Manager
  and do not appear in SQLite, logs, or crash output. The reminder cache itself
  is plaintext and accessible to other processes running as the same user.
- Obtain independent review of authentication, CloudKit mutations, credential
  lifecycle, and updater validation. Apple's private protocol may change;
  authentication and service URL validation must continue to fail closed.

See [protocol findings](docs/protocol-findings.md),
[SRP reference vectors](docs/srp-reference-vectors.md), and
[Store distribution](docs/microsoft-store.md) for the current limitations.
