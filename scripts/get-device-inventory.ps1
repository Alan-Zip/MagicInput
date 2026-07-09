[CmdletBinding()]
param(
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

if (-not $PSScriptRoot) {
    $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if (-not $OutFile) {
    $OutFile = Join-Path $PSScriptRoot "..\state\device-inventory-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"
}

$devices = Get-PnpDevice -PresentOnly |
    Where-Object {
        $_.InstanceId -match 'PID&0265|PID&0267|VID&0001004C' -or
        $_.FriendlyName -match 'Apple|Magic|Trackpad|Keyboard|Bluetooth HID Device'
    } |
    Sort-Object Class, FriendlyName

$lines = foreach ($device in $devices) {
    "## $($device.FriendlyName) [$($device.Class)]"
    "Status: $($device.Status)"
    "InstanceId: $($device.InstanceId)"
    Get-PnpDeviceProperty -InstanceId $device.InstanceId -ErrorAction SilentlyContinue |
        Where-Object { $_.KeyName -match 'DEVPKEY_Device_(HardwareIds|CompatibleIds|Service|Mfg|DriverProvider|DriverInfPath|MatchingDeviceId|BusReportedDeviceDesc|ClassGuid|ContainerId)' } |
        ForEach-Object {
            $data = ($_.Data | Out-String).Trim()
            "$($_.KeyName): $data"
        }
    ""
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
$lines | Out-File -LiteralPath $OutFile -Encoding utf8
Write-Host "Wrote $OutFile"
