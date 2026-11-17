param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Repository = "$PSScriptRoot/..",
    [ValidateSet('Prepare', 'Tagged')][string]$Phase = 'Prepare',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$Repository = (Resolve-Path -LiteralPath $Repository).Path
$semver = '(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?'
if ($Tag -cnotmatch "^[Vv]($semver)$") { throw 'Tag 必须是 V/v 前缀的合法语义版本。' }
$version = $Tag.Substring(1)
$normalizedTag = "V$version"
$tags = @(git -C $Repository tag --list)
if ($LASTEXITCODE) { throw '无法读取 Git Tags。' }
$same = @($tags | Where-Object { $_ -cmatch '^[Vv]' -and $_.Substring(1) -ceq $version })
if ($same.Count -gt 1) { throw "同一规范化版本存在大小写重复 Tag：$($same -join ', ')" }
if ($Phase -eq 'Prepare' -and $same.Count) { throw "版本已存在：$($same[0])，不能重复准备发布。" }
if ($Phase -eq 'Tagged') {
    if ($same.Count -ne 1 -or $same[0] -cne $Tag) { throw '触发 Tag 不存在或大小写与实际 Tag 不一致。' }
    $tagCommit = git -C $Repository rev-parse "$Tag^{commit}"
    if ($LASTEXITCODE) { throw '无法解析 Tag 提交。' }
    $headCommit = git -C $Repository rev-parse HEAD
    if ($LASTEXITCODE -or $tagCommit -cne $headCommit) { throw '必须从触发 Tag 的精确提交构建。' }
    git -C $Repository diff --quiet HEAD
    if ($LASTEXITCODE) { throw 'Tag 构建工作区存在未提交变更。' }
    git -C $Repository ls-files --error-unmatch changelog.md | Out-Null
    if ($LASTEXITCODE) { throw 'Tag 提交未包含 changelog.md。' }
}

$properties = Join-Path $Repository 'Directory.Build.props'
if (!(Test-Path -LiteralPath $properties)) { throw '缺少根版本声明 Directory.Build.props。' }
[xml]$rootProperties = Get-Content -LiteralPath $properties -Raw
$rootVersions = @($rootProperties.SelectNodes('//PropertyGroup/Version') | ForEach-Object { $_.InnerText })
if ($rootVersions.Count -ne 1 -or $rootVersions[0] -cne $version) { throw "根项目版本与 Tag 不一致，应为 $version。" }
$projectVersions = [ordered]@{}
$projects = @(Get-ChildItem -LiteralPath (Join-Path $Repository 'src') -Filter '*.csproj' -Recurse | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | Sort-Object FullName)
if (!$projects.Count) { throw '未找到发布耦合的 src 项目。' }
foreach ($project in $projects) {
    $effective = (& dotnet msbuild $project.FullName -nologo -getProperty:Version | Out-String).Trim()
    if ($LASTEXITCODE) { throw "无法计算项目版本：$($project.Name)" }
    if ($effective -cne $version) { throw "项目版本不一致：$($project.Name) = $effective，应为 $version。" }
    $projectVersions[[IO.Path]::GetRelativePath($Repository, $project.FullName).Replace('\', '/')] = $effective
}

$changelogPath = Join-Path $Repository 'changelog.md'
if (!(Test-Path -LiteralPath $changelogPath)) { throw '缺少已提交的 changelog.md；检查工具不会生成变更记录。' }
$changelog = (Get-Content -LiteralPath $changelogPath -Raw).Replace("`r`n", "`n")
if (($changelog -split "`n")[0].Trim() -cne '# Change Log') { throw 'changelog.md 必须以 # Change Log 开头。' }
$sections = [regex]::Matches($changelog, '(?ms)^## ([Vv][^\r\n]+)\s*\n(.*?)(?=^## |\z)')
$matchesForVersion = @($sections | Where-Object { $_.Groups[1].Value.Trim().Substring(1) -ceq $version })
if ($matchesForVersion.Count -ne 1) { throw "changelog 必须有且仅有一个 $normalizedTag 条目。" }
if ($matchesForVersion[0].Groups[1].Value.Trim() -cne $normalizedTag) { throw 'changelog 展示版本必须使用大写 V 前缀。' }
$body = $matchesForVersion[0].Groups[2].Value.Trim()
$categories = [regex]::Matches($body + "`n", '(?ms)^### ([^\r\n]+)\n(.*?)(?=^### |\z)')
if (!$categories.Count -or $body -notmatch '^### ') { throw '变更记录必须包含非空的标准分类。' }
$priorTags = @($tags | Where-Object { $_ -cmatch "^[Vv]$semver$" -and $_.Substring(1) -cne $version })
$firstRelease = $priorTags.Count -eq 0
$allowed = @('✨ 新功能', '🚀 优化', '🐛 Bug 修复')
$seen = [Collections.Generic.HashSet[string]]::new()
$clean = [Collections.Generic.List[string]]::new()
foreach ($category in $categories) {
    $title = $category.Groups[1].Value.Trim()
    $content = $category.Groups[2].Value.Trim()
    if ($title -cnotin $allowed -or !$seen.Add($title)) { throw "未知或重复分类：$title" }
    if (!$content) { continue }
    if ($firstRelease -and $title -cne '✨ 新功能') { throw '首次发布只能包含非空的“✨ 新功能”分类。' }
    $clean.Add("### $title`n`n$content")
}
if (!$clean.Count) { throw '目标版本的变更记录为空。' }
$releaseBody = $clean -join "`n`n"
$result = [ordered]@{ version = $version; tag = $normalizedTag; triggerTag = $Tag; prerelease = $version.Split('+')[0].Contains('-'); firstRelease = $firstRelease; projects = $projectVersions; body = $releaseBody }
if ($OutputDirectory) {
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $OutputDirectory -ErrorAction Stop | Out-Null
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'release-body.md'), $releaseBody + "`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'release-metadata.json'), ($result | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
}
[pscustomobject]$result
