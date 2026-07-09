[CmdletBinding()]
param(
    [string]$InstallRoot,
    [string]$Architecture,
    [switch]$SkipDriver,
    [switch]$NoStartup,
    [switch]$NoLaunch,
    [switch]$SkipRestorePoint
)

$ErrorActionPreference = 'Stop'

if (-not $PSScriptRoot) {
    $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

if (-not $InstallRoot) {
    $InstallRoot = Join-Path $env:LOCALAPPDATA 'Programs\MagicInput'
}

if (-not $Architecture) {
    $Architecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'AMD64' }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DotNetPath {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        return $dotnet.Source
    }

    $defaultDotNet = 'C:\Program Files\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $defaultDotNet) {
        return $defaultDotNet
    }

    throw 'The .NET SDK was not found. Install .NET 10 SDK or add dotnet.exe to PATH.'
}

if (-not $SkipDriver -and -not (Test-IsAdministrator)) {
    throw 'Run this script from an elevated PowerShell session, or pass -SkipDriver to install only the app.'
}

$appDir = Join-Path $InstallRoot 'app'
$appExe = Join-Path $appDir 'MagicInput.exe'
$clipboardReceiverScript = Join-Path $InstallRoot 'receive-mac-clipboard.ps1'
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Magic Input.lnk'
$projectPath = Join-Path $repoRoot 'src\MagicInput\MagicInput.csproj'
$dotnetPath = Get-DotNetPath

$running = Get-Process -Name MagicInput -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping running Magic Input instance...'
    $running | Stop-Process -Force
}

Write-Host "Publishing Magic Input to $appDir..."
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
& $dotnetPath publish $projectPath -c Release -o $appDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $appExe)) {
    throw "Published app was not found: $appExe"
}

Write-Host 'Installing clipboard receiver helper...'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'receive-mac-clipboard.ps1') -Destination $clipboardReceiverScript -Force

Write-Host 'Creating Start Menu shortcut...'
$shortcutDirectory = Split-Path -Parent $startMenuShortcut
New-Item -ItemType Directory -Force -Path $shortcutDirectory | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($startMenuShortcut)
$shortcut.TargetPath = $appExe
$shortcut.Arguments = ''
$shortcut.WorkingDirectory = $appDir
$shortcut.IconLocation = $appExe
$shortcut.Description = 'Magic Input'
$shortcut.Save()

if (-not $SkipDriver) {
    Write-Host 'Installing Magic Trackpad dependency driver...'
    $driverScript = Join-Path $PSScriptRoot 'install-trackpad-driver.ps1'
    $driverArgs = @('-Architecture', $Architecture)
    if ($SkipRestorePoint) {
        $driverArgs += '-SkipRestorePoint'
    }

    & $driverScript @driverArgs
}

if (-not $NoStartup) {
    Write-Host 'Registering Magic Input to launch at login...'
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $true)
    $runKey.SetValue('MagicInput', "`"$appExe`" --tray")
    $runKey.Dispose()
}

if (-not $NoLaunch) {
    Write-Host 'Launching Magic Input...'
    Start-Process -FilePath $appExe -ArgumentList '--tray' -WorkingDirectory $appDir -WindowStyle Hidden
}

Write-Host ''
Write-Host "Magic Input installed at: $appExe"
Write-Host "Clipboard receiver helper: $clipboardReceiverScript"
Write-Host "Start Menu shortcut: $startMenuShortcut"
if ($NoStartup) {
    Write-Host 'Launch at login was not enabled because -NoStartup was used.'
}
else {
    Write-Host 'Launch at login is enabled for the current Windows user.'
}
