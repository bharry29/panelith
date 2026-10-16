<#
.SYNOPSIS
    Authenticode-signs files with SHA-256 and an RFC 3161 timestamp.

.DESCRIPTION
    Used by build\publish.ps1 for Panelith's own binaries and by Inno Setup for
    the installer and uninstaller. The certificate comes from the environment,
    so no certificate details live in the repository:

        PANELITH_SIGN_THUMBPRINT   SHA-1 thumbprint of a code-signing certificate in
                                   Cert:\CurrentUser\My or Cert:\LocalMachine\My, or
        PANELITH_SIGN_PFX          path to a .pfx file, with
        PANELITH_SIGN_PASSWORD     its password.
        PANELITH_SIGN_TIMESTAMP    timestamp server (default http://timestamp.digicert.com).

    signtool.exe is used when the Windows SDK is installed; otherwise
    Set-AuthenticodeSignature, which needs nothing extra. A self-signed
    certificate signs correctly with either, but Windows only trusts the
    signature on machines that trust the certificate.

.EXAMPLE
    ./build/sign-file.ps1 artifacts\installer\Panelith-0.1.0-setup.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromRemainingArguments)]
    [string[]] $Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$timestamp = if ($env:PANELITH_SIGN_TIMESTAMP) { $env:PANELITH_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }

function Get-SigningCertificate {
    if ($env:PANELITH_SIGN_THUMBPRINT) {
        $thumbprint = $env:PANELITH_SIGN_THUMBPRINT -replace '\s', ''
        foreach ($store in 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') {
            $found = Get-ChildItem $store | Where-Object Thumbprint -eq $thumbprint | Select-Object -First 1
            if ($found) { return $found }
        }
        throw "No certificate with thumbprint $thumbprint is in the current user's or the machine's personal store."
    }

    if ($env:PANELITH_SIGN_PFX) {
        $password = if ($env:PANELITH_SIGN_PASSWORD) { ConvertTo-SecureString $env:PANELITH_SIGN_PASSWORD -AsPlainText -Force } else { $null }
        return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $env:PANELITH_SIGN_PFX, $password, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)
    }

    throw 'No signing certificate is configured. Set PANELITH_SIGN_THUMBPRINT, or PANELITH_SIGN_PFX and PANELITH_SIGN_PASSWORD.'
}

function Find-SignTool {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        $found = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    return $null
}

$certificate = Get-SigningCertificate
$signTool = Find-SignTool

foreach ($file in $Path) {
    $full = (Resolve-Path $file).Path
    if ($signTool) {
        $arguments = @('sign', '/fd', 'SHA256', '/tr', $timestamp, '/td', 'SHA256', '/sha1', $certificate.Thumbprint, $full)
        & $signTool @arguments
        if ($LASTEXITCODE -ne 0) { throw "signtool could not sign $full (exit code $LASTEXITCODE)." }
        continue
    }

    $result = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        # Timestamp servers drop the odd request; a retry is the usual fix.
        $result = Set-AuthenticodeSignature -FilePath $full -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $timestamp -IncludeChain NotRoot
        if ($result.SignerCertificate) { break }
        Start-Sleep -Seconds (2 * $attempt)
    }

    # An untrusted self-signed root reports UnknownError but still writes a valid signature.
    if (-not $result -or -not $result.SignerCertificate -or $result.Status -notin 'Valid', 'UnknownError') {
        throw "Could not sign ${full}: $($result.StatusMessage)"
    }

    Write-Host "Signed $full"
}
