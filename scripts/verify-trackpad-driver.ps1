[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Write-Host "Apple Bluetooth HID devices:"
Get-PnpDevice -PresentOnly |
    Where-Object { $_.InstanceId -match 'PID&0265|PID&0267|VID&0001004C|AmtPtp|Apple' -or $_.FriendlyName -match 'Apple|Trackpad|Keyboard|Bluetooth HID Device' } |
    Sort-Object Class, FriendlyName |
    Select-Object Status, Class, FriendlyName, InstanceId |
    Format-Table -AutoSize

Write-Host ""
Write-Host "Detailed PID&0265 trackpad properties:"
$trackpadDevices = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match 'PID&0265' }
foreach ($device in $trackpadDevices) {
    Write-Host "## $($device.FriendlyName) [$($device.Class)]"
    Write-Host "InstanceId: $($device.InstanceId)"
    Get-PnpDeviceProperty -InstanceId $device.InstanceId -ErrorAction SilentlyContinue |
        Where-Object { $_.KeyName -match 'DEVPKEY_Device_(HardwareIds|CompatibleIds|Service|Mfg|DriverProvider|DriverInfPath|MatchingDeviceId|BusReportedDeviceDesc|ClassGuid)' } |
        ForEach-Object {
            $data = ($_.Data | Out-String).Trim()
            Write-Host "$($_.KeyName): $data"
        }
    Write-Host ""
}

Write-Host "DriverStore entries mentioning AmtPtpDevice:"
$pnputil = & pnputil.exe /enum-drivers /class HIDClass 2>$null
$blocks = ($pnputil -join "`n") -split "Published Name\s*:"
$matches = foreach ($block in $blocks) {
    if ($block -match 'AmtPtpDevice|Bingxing Wang|Vito Plantamura|Apple Multi-touch Trackpad') {
        "Published Name:$block"
    }
}

if ($matches) {
    $matches | ForEach-Object { $_.Trim(); "" }
}
else {
    Write-Host "No AmtPtpDevice driver package found in HIDClass DriverStore enumeration."
}
