<#
.SYNOPSIS
    Downloads the third-party programs Panelith ships with, at pinned versions.

.DESCRIPTION
    Each file is checked against the SHA-256 its project published, and kept in
    artifacts\tools so later builds work offline. The app build copies them next
    to Panelith.exe under tools\, and the installer takes them from there.

    yt-dlp       downloads YouTube videos for "New video screen" (Unlicense).
    QuickJS-ng   the JavaScript runtime yt-dlp needs for YouTube (MIT).
    PawnIO       the signed driver LibreHardwareMonitor reads CPU sensors through (GPL-2.0).

    To update one, change its version, URL and hash below together.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$toolsDir = Join-Path $root 'artifacts\tools'
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

$tools = @(
    @{
        File   = 'yt-dlp.exe'
        Url    = 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe'
        Sha256 = '66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a'
    },
    @{
        File   = 'qjs.exe'
        Url    = 'https://github.com/quickjs-ng/quickjs/releases/download/v0.17.0/qjs-windows-x86_64.exe'
        Sha256 = '2aeabf0092c3262d6b2609824418f7dd7ed1f1df939f73b2b15645230cac0d77'
    },
    @{
        File   = 'PawnIO_setup.exe'
        Url    = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
        Sha256 = '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032'
    }
)

foreach ($tool in $tools) {
    $target = Join-Path $toolsDir $tool.File
    if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash -eq $tool.Sha256) {
        continue
    }

    Write-Host "Downloading $($tool.File)"
    $partial = "$target.partial"
    Invoke-WebRequest -Uri $tool.Url -OutFile $partial -UseBasicParsing
    $hash = (Get-FileHash $partial -Algorithm SHA256).Hash
    if ($hash -ne $tool.Sha256) {
        Remove-Item $partial -Force
        throw "$($tool.File) does not match its published SHA-256 (got $hash). Not using it."
    }

    Move-Item $partial $target -Force
}

Write-Host "Bundled tools are in $toolsDir"
