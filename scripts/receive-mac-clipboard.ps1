[CmdletBinding()]
param(
    [string]$Text,
    [string]$InboxDirectory
)

$ErrorActionPreference = 'Stop'

if (-not $InboxDirectory) {
    $InboxDirectory = Join-Path $env:APPDATA 'MagicInput\clipboard-inbox'
}

New-Item -ItemType Directory -Force -Path $InboxDirectory | Out-Null

if ($PSBoundParameters.ContainsKey('Text')) {
    $content = $Text
}
else {
    $content = [Console]::In.ReadToEnd()
}

$encoding = [Text.UTF8Encoding]::new($false)
$bytes = $encoding.GetBytes($content)
if ($bytes.Length -gt 4MB) {
    throw "Clipboard text is $($bytes.Length) bytes. Magic Input accepts up to 4 MB."
}

$tempPath = Join-Path $InboxDirectory "$([guid]::NewGuid()).tmp"
$clipPath = [IO.Path]::ChangeExtension($tempPath, '.clip')

[IO.File]::WriteAllBytes($tempPath, $bytes)
Move-Item -LiteralPath $tempPath -Destination $clipPath

Write-Host "Queued clipboard text for Magic Input ($($content.Length) characters)."
