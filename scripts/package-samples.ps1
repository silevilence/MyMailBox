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
    @{ Id='native'; Project='NativePlugin'; Entry='native'; Files=@('NativePlugin.dll'); Permissions=@('ui.native') }
)) {
    $files = @{}
    $binary = Join-Path $repo "samples/$($sample.Project)/bin/$Configuration/net10.0"
    foreach ($name in $sample.Files) { $files[$name] = (Get-FileHash (Join-Path $binary $name) -Algorithm SHA256).Hash }
    $version = if ($sample.Version) { $sample.Version } else { '1.0.0' }
    $manifest = @{ id=$sample.Id; version=$version; sdkMin=1; sdkMax=1; kind='Ordinary'; entry=$sample.Entry; assembly=$sample.Files[0]; files=$files; permissions=$sample.Permissions; source='MyMailBox 本地构建示例' } | ConvertTo-Json -Depth 5
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
