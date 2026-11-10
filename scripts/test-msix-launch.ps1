[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem $sdkRoot -Recurse -Filter makeappx.exe |
    Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (-not $makeAppx) { throw 'MakeAppx.exe was not found in the installed Windows SDK.' }
$staging = Join-Path ([IO.Path]::GetTempPath()) "reminders-launch-$([guid]::NewGuid().ToString('N'))"
$registeredPackage = $null
$appProcess = $null

try {
    $unpackOutput = @(& $makeAppx.FullName unpack /p $PackagePath /d $staging 2>&1)
    if ($LASTEXITCODE -ne 0) { $unpackOutput | Write-Host; throw 'Could not unpack the MSIX for launch testing.' }
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $staging 'AppxManifest.xml') -Raw
    $identity = $manifest.Package.Identity.Name
    # Never replace an installed app or remove its data as part of a smoke test.
    if (Get-AppxPackage -Name $identity) { throw "Package $identity is already installed. Run this test in a clean Windows user or VM." }
    $nativeArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($manifest.Package.Identity.ProcessorArchitecture -ne $nativeArchitecture) {
        throw "Launch testing requires a native $nativeArchitecture package."
    }

    # Register the extracted submission payload without a certificate. This
    # requires Windows Developer Mode and exercises real package identity.
    Add-AppxPackage -Register (Join-Path $staging 'AppxManifest.xml') -DisableDevelopmentMode:$false
    $registeredPackage = Get-AppxPackage -Name $identity
    if (-not $registeredPackage) { throw 'The test package was not registered.' }

    if (-not ('RemindersLaunch.Activator' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace RemindersLaunch {
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
    public static class Activator {
        public static uint Launch(string appUserModelId) {
            var manager = (IApplicationActivationManager)System.Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")));
            try {
                manager.ActivateApplication(appUserModelId, "", 4, out uint processId);
                return processId;
            } finally { Marshal.ReleaseComObject(manager); }
        }
    }
}
'@
    }
    $applicationId = $manifest.Package.Applications.Application.Id
    $appProcessId = [RemindersLaunch.Activator]::Launch("$($registeredPackage.PackageFamilyName)!$applicationId")
    $appProcess = Get-Process -Id $appProcessId -ErrorAction Stop
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($appProcess.HasExited) { throw 'Packaged app exited before showing a window. Check the Application event log for the underlying exception.' }
        $appProcess.Refresh()
        if ($appProcess.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($appProcess.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Packaged app did not create a window within the launch timeout.' }
    Start-Sleep -Seconds 5
    if ($appProcess.HasExited) { throw 'Packaged app crashed after showing its window. Check the Application event log for the underlying exception.' }
    Write-Host "Packaged launch passed: $identity $($manifest.Package.Identity.Version) ($nativeArchitecture)."
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id -Force }
    if ($registeredPackage) { Remove-AppxPackage -Package $registeredPackage.PackageFullName }
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedStaging.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
