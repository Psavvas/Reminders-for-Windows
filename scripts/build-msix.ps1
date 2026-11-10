[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Architecture = 'x64',
    [ValidatePattern('^[A-Za-z0-9.-]{3,50}$')]
    [string]$IdentityName = 'RemindersForWindows',
    [string]$Publisher = 'CN=paulsavvas.com',
    [string]$PublisherDisplayName,
    [string]$CertificateThumbprint,
    [switch]$Store,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if ($Store) {
    [xml]$storeManifest = Get-Content -LiteralPath (Join-Path $repository 'src/Reminders.Package/Package.appxmanifest') -Raw
    if (-not $PSBoundParameters.ContainsKey('IdentityName')) { $IdentityName = $storeManifest.Package.Identity.Name }
    if (-not $PSBoundParameters.ContainsKey('Publisher')) { $Publisher = $storeManifest.Package.Identity.Publisher }
    if (-not $PSBoundParameters.ContainsKey('PublisherDisplayName')) { $PublisherDisplayName = $storeManifest.Package.Properties.PublisherDisplayName }
    if ([string]::IsNullOrWhiteSpace($IdentityName) -or $IdentityName -eq 'RemindersForWindows' -or
        [string]::IsNullOrWhiteSpace($PublisherDisplayName) -or $Publisher -eq 'CN=paulsavvas.com') {
        throw 'Store submissions require the assigned Partner Center identity in Package.appxmanifest or explicit identity parameters.'
    }
    if ($CertificateThumbprint) { throw 'Store submissions are signed by Microsoft. Omit -CertificateThumbprint.' }
    if ($Publisher -notmatch '^CN=' -or $Publisher.Contains('OID.2.25.311729368913984317654407730594956997722')) {
        throw 'Use the exact Package/Identity/Publisher assigned by Partner Center for Store submission.'
    }
}
$portableOutput = if ($Architecture -eq 'ARM64') { Join-Path $repository 'dist-windows-arm64' } else { Join-Path $repository 'dist-windows' }
$packageOutput = Join-Path $repository $(if ($Store) { 'dist-store' } else { 'dist-msix' })
$project = Join-Path $repository 'src\Reminders.WinUI\Reminders.WinUI.csproj'
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem $sdkRoot -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (-not $makeAppx) { throw 'MakeAppx.exe was not found in the installed Windows SDK.' }
$makePri = Join-Path $makeAppx.Directory.FullName 'makepri.exe'
if (-not (Test-Path -LiteralPath $makePri)) { throw 'MakePri.exe was not found in the installed Windows SDK.' }

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-windows.ps1') -Architecture $Architecture
    if ($LASTEXITCODE -ne 0) { throw "The $Architecture production build failed." }
}
if (-not (Test-Path (Join-Path $portableOutput 'Reminders.exe'))) {
    throw "The $Architecture portable build is missing. Run .\scripts\build-windows.ps1 -Architecture $Architecture first."
}

if ($CertificateThumbprint) {
    $certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
    $Publisher = $certificate.Subject
}

[xml]$projectXml = Get-Content -Raw $project
$sourceVersion = [version]$projectXml.Project.PropertyGroup.Version
$packageVersion = "$($sourceVersion.Major).$($sourceVersion.Minor).$($sourceVersion.Build).0"
$executableVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $portableOutput 'Reminders.exe')).FileVersion
if ([version]$executableVersion -ne [version]$packageVersion) {
    throw "The published executable is $executableVersion, but the package is $packageVersion. Rebuild without -SkipBuild."
}
$processorArchitecture = if ($Architecture -eq 'ARM64') { 'arm64' } else { 'x64' }
$staging = Join-Path ([IO.Path]::GetTempPath()) "reminders-msix-$([guid]::NewGuid().ToString('N'))"

try {
    New-Item -ItemType Directory -Path $staging, (Join-Path $staging 'Assets'), $packageOutput -Force | Out-Null
    Copy-Item -Path (Join-Path $portableOutput '*') -Destination $staging -Recurse -Force
    Get-ChildItem $staging -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    $packageProject = Join-Path $repository 'src\Reminders.Package'
    Copy-Item -Path (Join-Path $packageProject 'Assets\*.png') -Destination (Join-Path $staging 'Assets') -Force
    # Explicit paths preserve the Assets subtree without re-indexing compiled
    # XAML or the portable artwork already imported from Reminders.pri.
    $shellAssetList = Join-Path $staging 'shell-assets.resfiles'
    Get-ChildItem (Join-Path $packageProject 'Assets') -Filter '*.png' -File |
        ForEach-Object { 'Assets\' + $_.Name } | Set-Content -LiteralPath $shellAssetList -Encoding utf8
    [xml]$manifestXml = Get-Content -LiteralPath (Join-Path $packageProject 'Package.appxmanifest') -Raw
    $manifestXml.Package.Identity.SetAttribute('Name', $IdentityName)
    $manifestXml.Package.Identity.SetAttribute('Publisher', $Publisher)
    $manifestXml.Package.Identity.SetAttribute('Version', $packageVersion)
    $manifestXml.Package.Identity.SetAttribute('ProcessorArchitecture', $processorArchitecture)
    if ($Store) { $manifestXml.Package.Properties.PublisherDisplayName = $PublisherDisplayName }
    $manifestXml.Package.Applications.Application.SetAttribute('Executable', 'Reminders.exe')
    $manifestXml.Package.Applications.Application.SetAttribute('EntryPoint', 'Windows.FullTrustApplication')
    $manifestXml.Save((Join-Path $staging 'AppxManifest.xml'))
    # Package identity changes MRT lookup: the portable Reminders.pri is not the
    # package's default resource index. Rebuild it as resources.pri with the
    # final identity (renaming the file alone leaves the wrong primary map).
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'Reminders.pri'))) {
        throw 'The portable build is missing Reminders.pri. Rebuild before packaging.'
    }
    $priOutput = @(& $makePri new /pr $staging /cf (Join-Path $PSScriptRoot 'msix-priconfig.xml') /in $IdentityName /of (Join-Path $staging 'resources.pri') /o 2>&1)
    if ($LASTEXITCODE -ne 0) { $priOutput | Write-Host; throw 'MakePri failed to create the package resource index.' }

    # Verify the actual generated map and startup resources before packing.
    $priDump = Join-Path ([IO.Path]::GetTempPath()) "reminders-pri-$([guid]::NewGuid().ToString('N')).xml"
    try {
        $dumpOutput = @(& $makePri dump /if (Join-Path $staging 'resources.pri') /of $priDump /dt detailed /o 2>&1)
        if ($LASTEXITCODE -ne 0) { $dumpOutput | Write-Host; throw 'MakePri failed to inspect the package resource index.' }
        [xml]$resourceIndex = Get-Content -LiteralPath $priDump -Raw
        $primaryMap = $resourceIndex.SelectSingleNode('//ResourceMap[@primary="true"]')
        if (-not $primaryMap -or $primaryMap.GetAttribute('name') -cne $IdentityName) {
            throw 'The package resource index does not match the final package identity.'
        }
        foreach ($resource in @('App.xbf', 'MainWindow.xbf')) {
            if (-not $primaryMap.SelectSingleNode("ResourceMapSubtree[@name='Files']/NamedResource[@name='$resource']/Candidate")) {
                throw "The package resource index is missing $resource."
            }
        }
        $shellLogo = $primaryMap.SelectSingleNode("ResourceMapSubtree[@name='Files']/ResourceMapSubtree[@name='Assets']/NamedResource[@name='Square44x44Logo.png']")
        foreach ($size in @(16,20,24,30,32,36,40,48,60,64,72,80,96,256)) {
            foreach ($form in @('UNPLATED', 'LIGHTUNPLATED')) {
                if (-not $shellLogo -or -not $shellLogo.SelectSingleNode("Candidate[QualifierSet/Qualifier[@name='TargetSize' and @value='$size'] and QualifierSet/Qualifier[@name='AlternateForm' and @value='$form']]/Value")) {
                    throw "The package resource index is missing the $size px $form shell icon."
                }
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $priDump) { Remove-Item -LiteralPath $priDump -Force }
    }
    Remove-Item -LiteralPath $shellAssetList -Force
    $package = Join-Path $packageOutput "Reminders-for-Windows-$Architecture.msix"
    $packOutput = @(& $makeAppx.FullName pack /o /h SHA256 /d $staging /p $package 2>&1)
    if ($LASTEXITCODE -ne 0) { $packOutput | Write-Host; throw 'MakeAppx failed to create the MSIX package.' }
    $packOutput | Select-Object -Last 1 | Write-Host

    if ($CertificateThumbprint) {
        $signTool = Get-ChildItem $sdkRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
        if (-not $signTool) { throw 'SignTool.exe was not found in the installed Windows SDK.' }
        & $signTool.FullName sign /sha1 $CertificateThumbprint /fd SHA256 $package
        if ($LASTEXITCODE -ne 0) { throw 'SignTool failed to sign the MSIX package.' }
    }
    elseif ($Store) {
        Write-Host 'Store submission package created. Upload it to Partner Center; Microsoft signs the approved distribution. This file is not a trusted installer yet.'
    }
    else {
        Write-Warning 'The MSIX is unsigned. Sign it with -CertificateThumbprint or submit it to the Microsoft Store before installation.'
    }
    Write-Host "MSIX package: $package" -ForegroundColor Green
}
finally {
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedStaging.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
