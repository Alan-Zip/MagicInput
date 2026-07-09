[CmdletBinding()]
param(
    [string]$PackageRoot,
    [string]$ZipPath,
    [string]$Architecture = 'AMD64',
    [string]$ReleaseZipUri = 'https://github.com/vitoplantamura/MagicTrackpad2ForWindows/releases/download/v2.0/MT2FW11-20260223-MSSigned.zip',
    [switch]$SkipRestorePoint
)

$ErrorActionPreference = 'Stop'

$expectedZipSha256 = '2870C0C7982CE6AAFC3FF763FEC2999423DC4BDBD1A2C0E31CA216F26A75714F'
$expectedHardwareId = 'HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0265&Col01'
$expectedSigner = 'Microsoft Windows Hardware Compatibility Publisher'

if (-not $PSScriptRoot) {
    $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if (-not $PackageRoot) {
    $PackageRoot = Join-Path $PSScriptRoot '..\packages\MT2FW11-20260223-MSSigned\MT2FW11-20260223-MSSigned'
}

if (-not $ZipPath) {
    $ZipPath = Join-Path $PSScriptRoot '..\downloads\MT2FW11-20260223-MSSigned.zip'
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-ValidSignature {
    param(
        [Parameter(Mandatory)] [string]$LiteralPath,
        [Parameter(Mandatory)] [string]$ExpectedSubjectFragment
    )

    $sig = Get-AuthenticodeSignature -LiteralPath $LiteralPath
    if ($sig.Status -ne 'Valid') {
        throw "Invalid signature for $LiteralPath. Status: $($sig.Status)"
    }

    if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Subject -notlike "*$ExpectedSubjectFragment*") {
        throw "Unexpected signer for $LiteralPath. Signer: $($sig.SignerCertificate.Subject)"
    }
}

if (-not (Test-IsAdministrator)) {
    throw "Run this script from an elevated PowerShell session."
}

$workspaceRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$logDir = Join-Path $workspaceRoot 'logs'
$stateDir = Join-Path $workspaceRoot 'state'
New-Item -ItemType Directory -Force -Path $logDir, $stateDir | Out-Null

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logPath = Join-Path $logDir "install-trackpad-driver-$timestamp.log"
$preStatePath = Join-Path $stateDir "pnp-before-install-$timestamp.txt"

Start-Transcript -Path $logPath -Force | Out-Null
try {
    if (-not (Test-Path -LiteralPath $ZipPath)) {
        Write-Host "Downloading pinned driver package..."
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ZipPath) | Out-Null
        Invoke-WebRequest -Uri $ReleaseZipUri -OutFile $ZipPath
    }

    Write-Host "Verifying release zip hash..."
    $zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ZipPath).Hash
    if ($zipHash -ne $expectedZipSha256) {
        throw "Zip hash mismatch. Expected $expectedZipSha256, got $zipHash"
    }

    if (-not (Test-Path -LiteralPath $PackageRoot)) {
        Write-Host "Extracting pinned driver package..."
        $extractRoot = Split-Path -Parent $PackageRoot
        New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
        Expand-Archive -LiteralPath $ZipPath -DestinationPath $extractRoot -Force
    }

    $archRoot = Join-Path $PackageRoot $Architecture
    $infPath = Join-Path $archRoot 'AmtPtpDevice.inf'
    $catPath = Join-Path $archRoot 'amtptpdevice.cat'
    $sysPath = Join-Path $archRoot 'AmtPtpHidFilter.sys'
    $dllPath = Join-Path $archRoot 'AmtPtpDeviceUsbUm.dll'

    foreach ($path in @($infPath, $catPath, $sysPath, $dllPath)) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Missing package file: $path"
        }
    }

    Write-Host "Verifying Microsoft driver signatures..."
    Assert-ValidSignature -LiteralPath $catPath -ExpectedSubjectFragment $expectedSigner
    Assert-ValidSignature -LiteralPath $sysPath -ExpectedSubjectFragment $expectedSigner
    Assert-ValidSignature -LiteralPath $dllPath -ExpectedSubjectFragment $expectedSigner

    Write-Host "Checking INF hardware match..."
    $infText = Get-Content -LiteralPath $infPath -Raw
    if ($infText -notmatch [regex]::Escape($expectedHardwareId)) {
        throw "INF does not contain expected Magic Trackpad 2 Bluetooth hardware ID: $expectedHardwareId"
    }

    Write-Host "Saving current Apple HID device inventory..."
    Get-PnpDevice -PresentOnly |
        Where-Object { $_.InstanceId -match 'PID&0265|PID&0267|VID&0001004C' } |
        Sort-Object Class, FriendlyName |
        Format-List Status, Class, FriendlyName, InstanceId |
        Out-File -LiteralPath $preStatePath -Encoding utf8

    if (-not $SkipRestorePoint) {
        Write-Host "Attempting restore point..."
        try {
            Checkpoint-Computer -Description 'Before Apple Magic Trackpad driver install' -RestorePointType 'MODIFY_SETTINGS'
            Write-Host "Restore point requested."
        }
        catch {
            Write-Warning "Restore point was not created: $($_.Exception.Message)"
        }
    }

    Write-Host "Installing driver package with pnputil..."
    & pnputil.exe /add-driver $infPath /install
    $pnputilExit = $LASTEXITCODE
    if ($pnputilExit -ne 0) {
        throw "pnputil failed with exit code $pnputilExit"
    }

    Write-Host "Rescanning devices..."
    & pnputil.exe /scan-devices | Out-Host

    Write-Host "Done. Log: $logPath"
}
finally {
    Stop-Transcript | Out-Null
}
