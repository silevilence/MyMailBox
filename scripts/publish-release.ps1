param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$')][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$Commit,
    [Parameter(Mandatory)][string]$InstallerDirectory,
    [Parameter(Mandatory)][string]$MetadataFile,
    [scriptblock]$Transport
)
$ErrorActionPreference = 'Stop'
$InstallerDirectory = (Resolve-Path -LiteralPath $InstallerDirectory).Path
$metadata = Get-Content -LiteralPath $MetadataFile -Raw | ConvertFrom-Json
$ci = Get-Content -LiteralPath (Join-Path $InstallerDirectory 'ci-manifest.json') -Raw | ConvertFrom-Json
$build = Get-Content -LiteralPath (Join-Path $InstallerDirectory 'build-manifest.json') -Raw | ConvertFrom-Json
if ($ci.commit -cne $Commit -or $build.version -cne $metadata.version) { throw '构建提交或产物版本与发布目标不一致。' }
$assets = @(Get-Content -LiteralPath (Join-Path $InstallerDirectory 'release-assets.json') -Raw | ConvertFrom-Json)
$required = @("MyMailBox-V$($metadata.version)-win-x64-setup.exe", 'SHA256SUMS.txt', 'appcast.xml', 'appcast.xml.signature', 'build-manifest.json', 'ci-manifest.json', 'publish-files.json')
if ($assets.Count -ne $required.Count -or @(Compare-Object ($assets.name | Sort-Object) ($required | Sort-Object)).Count) { throw '发布资产清单不完整或包含未知文件。' }
foreach ($asset in $assets) {
    $path = Join-Path $InstallerDirectory $asset.name
    if ((Get-Item -LiteralPath $path).Length -ne $asset.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $asset.sha256) { throw "本地产物校验失败：$($asset.name)" }
}
if (!$Transport) {
    if (!$env:GH_TOKEN) { throw '缺少发布作业的 GH_TOKEN。' }
    $Transport = {
        param($method, $url, $body, $file)
        $headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
        $parameters = @{ Method = $method; Uri = $url; Headers = $headers; TimeoutSec = 300; SkipHttpErrorCheck = $true }
        if ($file) { $parameters.InFile = $file; $parameters.ContentType = 'application/octet-stream' }
        elseif ($null -ne $body) { $parameters.Body = $body | ConvertTo-Json -Depth 8 -Compress; $parameters.ContentType = 'application/json; charset=utf-8' }
        $response = Invoke-WebRequest @parameters
        if ($response.StatusCode -eq 404 -and $method -eq 'GET') { return $null }
        if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) { throw "GitHub API $method 失败：HTTP $($response.StatusCode)。保留当前草稿以便重跑。" }
        if ($response.Content) { return $response.Content | ConvertFrom-Json }
    }
}
function Request($method, $url, $body = $null, $file = $null) { & $Transport $method $url $body $file }
function ValidAsset($actual, $expected) { return $null -ne $actual -and $actual.state -eq 'uploaded' -and $actual.size -eq $expected.size -and $actual.digest -ieq "sha256:$($expected.sha256)" }
$api = "https://api.github.com/repos/$Repository/releases"
$tag = [Uri]::EscapeDataString($metadata.triggerTag)
$release = Request GET "$api/tags/$tag"
if (!$release) {
    # The tag endpoint may omit drafts; authenticated release listings include them.
    for ($page = 1; $page -le 10; $page++) {
        $listed = @(Request GET "${api}?per_page=100&page=$page")
        $found = @($listed | Where-Object tag_name -CEQ $metadata.triggerTag)
        if ($found.Count -gt 1) { throw '同一 Tag 存在多个 Release，停止处理。' }
        if ($found.Count -eq 1) { $release = $found[0]; break }
        if ($listed.Count -lt 100) { break }
        if ($page -eq 10) { throw '未能遍历全部 Release，不能确认不存在草稿。' }
    }
}
if ($release) {
    if ($release.tag_name -cne $metadata.triggerTag -or $release.target_commitish -cne $Commit) { throw '已有 Release 不属于当前 Tag 精确提交，拒绝更改。' }
    if (!$release.draft) {
        foreach ($asset in $assets) { if (!(ValidAsset ($release.assets | Where-Object name -CEQ $asset.name) $asset)) { throw '公开 Release 资产与当前构建不同，禁止覆盖。' } }
        if ($release.body -cne $metadata.body -or $release.prerelease -ne $metadata.prerelease) { throw '公开 Release 正文或通道不同，禁止修改。' }
        Write-Output 'PASS existing public Release verified by platform metadata; no assets downloaded or changed'
        return
    }
} else {
    $release = Request POST $api @{ tag_name = $metadata.triggerTag; target_commitish = $Commit; name = $metadata.tag; body = $metadata.body; draft = $true; prerelease = $metadata.prerelease; generate_release_notes = $false }
}
foreach ($asset in $assets) {
    $existing = @($release.assets | Where-Object name -CEQ $asset.name)
    if ($existing.Count -eq 1 -and (ValidAsset $existing[0] $asset)) { continue }
    foreach ($old in $existing) { Request DELETE "$api/assets/$($old.id)" | Out-Null }
    $upload = "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($asset.name))"
    $uploaded = Request POST $upload $null (Join-Path $InstallerDirectory $asset.name)
    if (!(ValidAsset $uploaded $asset)) { throw "上传平台摘要与本地不一致：$($asset.name)。Release 保持草稿。" }
}
$release = Request GET "$api/$($release.id)"
if (!$release.draft) { throw 'Release 在校验期间被外部公开，停止更改。' }
if (@($release.assets).Count -ne $assets.Count -or @(Compare-Object ($release.assets.name | Sort-Object) ($assets.name | Sort-Object)).Count) { throw '草稿含有未验证的额外资产或重复名称，停止公开。' }
foreach ($asset in $assets) { if (!(ValidAsset ($release.assets | Where-Object name -CEQ $asset.name) $asset)) { throw "公开前资产检查失败：$($asset.name)。" } }
$latest = if ($metadata.prerelease) { 'false' } else { 'legacy' }
$published = Request PATCH "$api/$($release.id)" @{ name = $metadata.tag; body = $metadata.body; draft = $false; prerelease = $metadata.prerelease; make_latest = $latest }
if ($published.draft -or $published.tag_name -cne $metadata.triggerTag) { throw '公开状态未确认，请检查该 Tag 的 Release。' }
Write-Output "PASS published $($metadata.tag): platform names/sizes/SHA256 verified, no asset redownload"
