<#
.SYNOPSIS
    Publishes Panelith for Windows, optionally signs it, and builds the installer.

.DESCRIPTION
    1. dotnet publish of the desktop app, self-contained for win-x64, to artifacts\publish\app,
       with the bundled tools that build\fetch-tools.ps1 downloads and checks.
    2. Code signing, when a certificate is configured (see below).
    3. The Inno Setup installer, to artifacts\installer, when ISCC.exe is available.

    Signing is opt-in through environment variables, so no certificate details
    live in the repository. See build\sign-file.ps1 for the variables.
    build\release.ps1 runs this script and then publishes the GitHub release.

.EXAMPLE
    ./build/publish.ps1
.EXAMPLE
    $env:PANELITH_SIGN_THUMBPRINT = '0123...'; ./build/publish.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish\app'
$installerDir = Join-Path $root 'artifacts\installer'

if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
    $Version = $props.SelectSingleNode('//VersionPrefix').InnerText
}

# The release is built locally rather than in CI. Keep the dependency gate
# here so an installer cannot be produced from packages NuGet knows are
# vulnerable, including vulnerabilities in transitive dependencies.
Write-Host 'Checking NuGet packages for known vulnerabilities'
$audit = & dotnet list (Join-Path $root 'Panelith.slnx') package --vulnerable --include-transitive --format json --output-version 1 2>&1
$auditText = $audit -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) {
    Write-Host $auditText
    throw "The dependency vulnerability check failed with exit code $LASTEXITCODE. No installer was built."
}

try {
    $null = $auditText | ConvertFrom-Json
}
catch {
    Write-Host $auditText
    throw 'The dependency vulnerability check returned unreadable results. No installer was built.'
}

# In the JSON format, this property only appears on an affected package.
if ($auditText -match '"vulnerabilities"\s*:') {
    Write-Host $auditText
    throw 'A direct or transitive NuGet package has a known vulnerability. Update it before building an installer.'
}
Write-Host 'No known vulnerable NuGet packages were found.'

& (Join-Path $PSScriptRoot 'fetch-tools.ps1')

Write-Host "Publishing Panelith $Version ($Configuration, $Runtime)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root 'src\Panelith.App\Panelith.App.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $publishDir `
    -p:Version=$Version `
    -p:DebugType=none `
    -p:GenerateDocumentationFile=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# Native packages (SkiaSharp, HarfBuzz) ship debug symbols that would triple the download.
Get-ChildItem $publishDir -Recurse -Filter *.pdb | Remove-Item -Force

$signScript = Join-Path $PSScriptRoot 'sign-file.ps1'
$signing = [bool]($env:PANELITH_SIGN_THUMBPRINT -or $env:PANELITH_SIGN_PFX)
if ($signing) {
    $toSign = Get-ChildItem $publishDir -File | Where-Object { $_.Name -like 'Panelith*.exe' -or $_.Name -like 'Panelith*.dll' }
    Write-Host "Signing $($toSign.Count) files"
    & $signScript -Path ([string[]] $toSign.FullName)
}
else {
    Write-Warning 'No signing certificate configured (PANELITH_SIGN_THUMBPRINT or PANELITH_SIGN_PFX); the build is unsigned.'
}

if ($SkipInstaller) {
    Write-Host "Published to $publishDir"
    return
}

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | ForEach-Object Source),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning 'Inno Setup 6 (ISCC.exe) was not found, so no installer was built. Install it from https://jrsoftware.org/isinfo.php or with: winget install JRSoftware.InnoSetup'
    Write-Host "Published to $publishDir"
    return
}

$isccArgs = @("/DAppVersion=$Version", "/DPublishDir=$publishDir", '/Q')
if ($signing) {
    # Inno Setup turns $q into a quote and $f into the quoted file to sign;
    # literal quotes would not survive Windows PowerShell's argument passing.
    $isccArgs += @('/DSign', "/Spanelith=powershell.exe -NoProfile -ExecutionPolicy Bypass -File `$q$signScript`$q `$f")
}

& $iscc @isccArgs (Join-Path $root 'installer\panelith.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

Write-Host "Installer written to $installerDir"
