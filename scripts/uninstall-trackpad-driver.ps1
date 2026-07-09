[CmdletBinding()]
param(
    [switch]$Execute
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-AmtPtpDriverPackages {
    $raw = & pnputil.exe /enum-drivers /class HIDClass 2>$null
    $text = $raw -join "`n"
    $blocks = $text -split "(?=Published Name\s*:)"

    foreach ($block in $blocks) {
        if ($block -match 'AmtPtpDevice|Bingxing Wang|Vito Plantamura|Apple Multi-touch Trackpad') {
            $published = [regex]::Match($block, 'Published Name\s*:\s*(\S+)').Groups[1].Value
            [pscustomobject]@{
                PublishedName = $published
                Details = $block.Trim()
            }
        }
    }
}

$packages = @(Get-AmtPtpDriverPackages)
if (-not $packages) {
    Write-Host "No AmtPtpDevice driver packages found."
    return
}

Write-Host "Matching driver packages:"
$packages | ForEach-Object {
    Write-Host ""
    Write-Host $_.Details
}

if (-not $Execute) {
    Write-Host ""
    Write-Host "Dry run only. Re-run with -Execute from elevated PowerShell to uninstall these packages."
    return
}

if (-not (Test-IsAdministrator)) {
    throw "Run this script from an elevated PowerShell session to uninstall driver packages."
}

foreach ($package in $packages) {
    if (-not $package.PublishedName) {
        Write-Warning "Skipping package without a parsed Published Name."
        continue
    }

    Write-Host "Deleting $($package.PublishedName)..."
    & pnputil.exe /delete-driver $package.PublishedName /uninstall /force
    if ($LASTEXITCODE -ne 0) {
        throw "pnputil failed while deleting $($package.PublishedName) with exit code $LASTEXITCODE"
    }
}

Write-Host "Rescanning devices..."
& pnputil.exe /scan-devices | Out-Host
