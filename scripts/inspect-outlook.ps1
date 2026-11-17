param([string]$Output = (Join-Path $PSScriptRoot '../artifacts/outlook-acceptance/inventory.json'))
$ErrorActionPreference = 'Stop'
function Read-PeArchitecture([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try { $reader = [IO.BinaryReader]::new($stream); $stream.Position=0x3c; $offset=$reader.ReadInt32(); $stream.Position=$offset+4; $machine=$reader.ReadUInt16(); if ($machine -eq 0x8664) {'x64'} elseif ($machine -eq 0x14c) {'x86'} else { 'unknown:0x{0:X4}' -f $machine } }
    finally { $stream.Dispose() }
}
$configuration = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\Configuration' -ErrorAction SilentlyContinue
$executables = @('C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE','C:\Program Files (x86)\Microsoft Office\root\Office16\OUTLOOK.EXE') | Where-Object { Test-Path -LiteralPath $_ }
$classic = @($executables | ForEach-Object { [ordered]@{ path=$_; version=(Get-Item -LiteralPath $_).VersionInfo.FileVersion; architecture=Read-PeArchitecture $_ } })
$profiles = @()
foreach ($key in @('HKCU:\Software\Microsoft\Office\16.0\Outlook\Profiles','HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows Messaging Subsystem\Profiles')) {
    $profiles += @(Get-ChildItem -LiteralPath $key -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName })
}
$default = Get-ItemProperty 'HKCU:\Software\Microsoft\Office\16.0\Outlook' -Name DefaultProfile -ErrorAction SilentlyContinue
$new = @(Get-AppxPackage '*OutlookForWindows*' | Select-Object Name,Version,Architecture)
$result = [ordered]@{
    capturedUtc=[DateTimeOffset]::UtcNow.ToString('O'); classic=$classic; clickToRunPlatform=$configuration.Platform
    newOutlook=$new; classicProfileNames=$profiles; defaultProfile=$default.DefaultProfile
    running=@(Get-Process OUTLOOK,olk -ErrorAction SilentlyContinue | Select-Object ProcessName,Id)
    access='Registry installation/profile names and PE header only; no COM activation, credentials, mailbox data or network requests.'
}
$Output=[IO.Path]::GetFullPath($Output); New-Item -ItemType Directory -Path (Split-Path $Output) -Force | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding utf8
$result | ConvertTo-Json -Depth 8
