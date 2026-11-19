param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$output = Join-Path $repo 'artifacts/packages'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression
foreach ($sample in @(
    @{ Id='example'; Project='ExamplePlugin'; Version='1.0.0'; Entry='example'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@() },
    @{ Id='example'; Project='ExamplePluginV2'; Version='2.0.0'; Entry='example'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@() },
    @{ Id='alternative-selector'; Project='ExamplePlugin'; Entry='selector'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@() },
    @{ Id='example-exporter'; Project='ExamplePlugin'; Entry='exporter'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@() },
    @{ Id='example-feed'; Project='ExamplePlugin'; Entry='local-feed'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@('updates.check') },
    @{ Id='example-presenter'; Project='ExamplePlugin'; Entry='notice'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@('notification.show') },
    @{ Id='example-search'; Project='ExamplePlugin'; Entry='search-case'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@('mail.read') },
    @{ Id='example-editor'; Project='ExamplePlugin'; Entry='plain-editor'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@('mail.compose') },
    @{ Id='example-addresses'; Project='ExamplePlugin'; Entry='example-addresses'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@('contacts.suggest') },
    @{ Id='example-file-store'; Project='MailboxPlugin'; Kind='Core'; Entry='file-store'; Files=@('MailboxPlugin.dll'); Permissions=@('mail.storage') },
    @{ Id='example-mailbox'; Project='MailboxPlugin'; Entry='loopback-mailbox'; Files=@('MailboxPlugin.dll'); Permissions=@('mail.read','mail.write','mail.send') },
    @{ Id='example-importer'; Project='ExamplePlugin'; Entry='eml-import'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@('files.read','mail.write') },
    @{ Id='automation-example'; Project='ExamplePlugin'; Entry='automation-example'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@('automation.condition','automation.action','automation.schedule','mail.write') },
    @{ Id='automation-example'; Project='ExamplePluginV2'; Version='2.0.0'; Entry='automation-example'; Files=@('ExamplePlugin.dll','PrivateGreeting.dll'); Permissions=@('automation.condition','automation.action','automation.schedule','mail.write') },
    @{ Id='native'; Project='NativePlugin'; Entry='native'; Files=@('NativePlugin.dll'); Permissions=@('ui.native') }
)) {
    $files = @{}
    $binary = Join-Path $repo "samples/$($sample.Project)/bin/$Configuration/net10.0"
    foreach ($name in $sample.Files) { $files[$name] = (Get-FileHash (Join-Path $binary $name) -Algorithm SHA256).Hash }
    $version = if ($sample.Version) { $sample.Version } else { '1.0.0' }
    $manifest = @{ id=$sample.Id; version=$version; sdkMin=1; sdkMax=1; kind=$(if ($sample.Kind) { $sample.Kind } else { 'Ordinary' }); entry=$sample.Entry; assembly=$sample.Files[0]; files=$files; permissions=$sample.Permissions; source='MyMailBox 本地构建示例' } | ConvertTo-Json -Depth 5
    $zipPath = Join-Path $output "$($sample.Id)-$version.zip"
    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('manifest.json')
        $writer = [IO.StreamWriter]::new($entry.Open()); $writer.Write($manifest); $writer.Dispose()
        foreach ($name in $sample.Files) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $binary $name), $name) | Out-Null }
    } finally { $zip.Dispose(); $stream.Dispose() }
    Write-Output $zipPath
}
