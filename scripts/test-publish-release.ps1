$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../artifacts/checks")) ('publish-release-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$commit = 'a' * 40
$metadata = @{ version = '1.2.3-beta.1'; tag = 'V1.2.3-beta.1'; triggerTag = 'v1.2.3-beta.1'; prerelease = $true; body = "### ✨ 新功能`n`n- 完整发布夹具。" }
$metadataFile = Join-Path $root 'metadata.json'
$metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataFile -Encoding utf8NoBOM
@{ commit = $commit } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'ci-manifest.json') -Encoding utf8NoBOM
@{ version = $metadata.version } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'build-manifest.json') -Encoding utf8NoBOM
$names = @("MyMailBox-V$($metadata.version)-win-x64-setup.exe", 'SHA256SUMS.txt', 'appcast.xml', 'appcast.xml.signature', 'build-manifest.json', 'ci-manifest.json', 'publish-files.json')
$expected = @($names | ForEach-Object {
    $path = Join-Path $root $_
    if (!(Test-Path -LiteralPath $path)) { [IO.File]::WriteAllText($path, "fixture $_") }
    @{ name = $_; size = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$expected | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'release-assets.json') -Encoding utf8NoBOM
$state = @{ Release = $null; Calls = [Collections.Generic.List[string]]::new(); NextId = 10; FailName = $null; Publicized = 0 }
$transport = {
    param($method, $url, $body, $file)
    $state.Calls.Add("$method $url")
    if ($method -eq 'GET') {
        if ($url.Contains('/tags/')) { return $null } # Exercise draft discovery through listing.
        if ($url.Contains('?per_page=')) { if ($state.Release) { return @($state.Release) }; return @() }
        return $state.Release
    }
    if ($method -eq 'POST' -and !$file) {
        if (!$body.draft -or $body.generate_release_notes) { throw 'Release was exposed before uploads or auto-generated notes were enabled' }
        $state.Release = [pscustomobject]@{ id = 1; tag_name = $body.tag_name; target_commitish = $body.target_commitish; body = $body.body; draft = $true; prerelease = $body.prerelease; assets = @() }
        return $state.Release
    }
    if ($method -eq 'POST' -and $file) {
        $name = [Uri]::UnescapeDataString(($url -split '\?name=', 2)[1])
        if ($name -eq $state.FailName) { throw 'simulated upload failure' }
        $state.NextId++
        $asset = [pscustomobject]@{ id = $state.NextId; name = $name; size = (Get-Item -LiteralPath $file).Length; digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); state = 'uploaded' }
        $state.Release.assets += $asset
        return $asset
    }
    if ($method -eq 'DELETE') {
        if (!$state.Release.draft) { throw 'Attempted public asset deletion' }
        $id = [int]($url.Split('/')[-1]); $state.Release.assets = @($state.Release.assets | Where-Object id -NE $id); return
    }
    if ($method -eq 'PATCH') {
        if ($state.Release.assets.Count -ne 7 -or !$body.prerelease -or $body.make_latest -ne 'false') { throw 'Premature publication or preview channel mismatch' }
        $state.Release.draft = $body.draft; $state.Release.body = $body.body; $state.Publicized++; return $state.Release
    }
    throw "Unexpected fixture request: $method $url"
}.GetNewClosure()
function Publish { & "$PSScriptRoot/publish-release.ps1" -Repository fixture/repository -Commit $commit -InstallerDirectory $root -MetadataFile $metadataFile -Transport $transport }
function Reject([scriptblock]$operation, [string]$text) {
    try { & $operation } catch { if ($_.Exception.Message.Contains($text)) { return }; throw }
    throw "Expected failure: $text"
}
Publish
if ($state.Release.draft -or $state.Publicized -ne 1 -or $state.Release.tag_name -cne $metadata.triggerTag) { throw 'Initial publish failed' }
$state.Calls.Clear(); Publish
if ($state.Calls | Where-Object { $_ -notlike 'GET *' }) { throw 'Public rerun changed assets' }
$savedDigest = $state.Release.assets[0].digest; $state.Release.assets[0].digest = 'sha256:broken'; $state.Calls.Clear()
Reject { Publish } '禁止覆盖'
if ($state.Calls | Where-Object { $_ -notlike 'GET *' }) { throw 'Public mismatch mutated release' }
$state.Release.assets[0].digest = $savedDigest
$state.Release.draft = $true; $state.Release.assets = @($state.Release.assets | Select-Object -SkipLast 1); $state.Release.assets[0].digest = 'sha256:broken'; $state.Calls.Clear()
Publish
if (@($state.Calls | Where-Object { $_ -like 'DELETE *' }).Count -ne 1 -or @($state.Calls | Where-Object { $_ -like 'POST https://uploads*' }).Count -ne 2) { throw 'Draft recovery failed to reuse valid assets' }
$state.Release = $null; $state.FailName = 'appcast.xml'; $state.Calls.Clear(); Reject { Publish } 'simulated upload failure'
if (!$state.Release.draft -or ($state.Calls | Where-Object { $_ -like 'PATCH *' })) { throw 'Partial upload was made public' }
$state.FailName = $null; Publish
$state.Release.target_commitish = 'b' * 40; Reject { Publish } '精确提交'
'PASS release upload fixture: draft-first, complete signed asset set, preview, exact SHA, resume/reuse, failed upload stays draft, public rerun read-only and no redownload'
