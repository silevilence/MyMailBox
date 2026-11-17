$ErrorActionPreference = 'Stop'
$check = Join-Path $PSScriptRoot 'check-release.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('MyMailBox-release-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $root 'src/App'), (Join-Path $root 'src/Plugin') | Out-Null
function Write-Utf8([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false)) }
function Version([string]$Value) { Write-Utf8 (Join-Path $root 'Directory.Build.props') "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><Version>$Value</Version></PropertyGroup></Project>" }
function Log([string]$Text) { Write-Utf8 (Join-Path $root 'changelog.md') $Text }
function Reject([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message.Contains($Expected)) { return }; throw }
    throw "Expected rejection: $Expected"
}
function Invoke-FixtureGit([string[]]$Arguments) { & git.exe -C $root @Arguments | Out-Null; if ($LASTEXITCODE) { throw 'Fixture git command failed' } }
Write-Utf8 (Join-Path $root 'src/App/App.csproj') '<Project Sdk="Microsoft.NET.Sdk" />'
Write-Utf8 (Join-Path $root 'src/Plugin/Plugin.csproj') '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Independent.Dependency" Version="99.2.1" /></ItemGroup></Project>'
Invoke-FixtureGit @('init','--quiet'); Invoke-FixtureGit @('config','user.name','Release Fixture'); Invoke-FixtureGit @('config','user.email','fixture@example.test')
Version '1.2.3'
Log "# Change Log`n`n## V1.2.3`n`n### ✨ 新功能`n`n- 邮件阅读。`n`n### 🚀 优化`n`n### 🐛 Bug 修复`n"
$first = & $check -Tag v1.2.3 -Repository $root
if ($first.tag -cne 'V1.2.3' -or !$first.firstRelease -or $first.projects.Count -ne 2 -or $first.body.Contains('优化')) { throw 'First-release extraction failed' }
foreach ($invalid in @('V01.2.3','V1.2','1.2.3','V1.2.3-01','V1.2.3-','V1.2.3x')) { Reject { & $check -Tag $invalid -Repository $root } '合法语义版本' }
Log "# Change Log`n`n## V1.2.3`n`n### 🐛 Bug 修复`n`n- 不允许首发修复。`n"
Reject { & $check -Tag V1.2.3 -Repository $root } '首次发布'
Log "# Change Log`n`n## V1.2.3`n`n### ✨ 新功能`n`n- 首发。`n"
Invoke-FixtureGit @('add','.'); Invoke-FixtureGit @('commit','--quiet','-m','fixture'); Invoke-FixtureGit @('tag','V1.0.0')
Log "# Change Log`n`n## V1.2.3`n`n### 🚀 优化`n`n- 更快。`n`n### 🐛 Bug 修复`n`n- 修复读取。`n`n## V1.0.0`n`n### ✨ 新功能`n`n- 历史内容。`n"
$later = & $check -Tag V1.2.3 -Repository $root
if ($later.firstRelease -or !$later.body.Contains('修复读取') -or $later.body.Contains('历史内容')) { throw 'Target release body extraction failed' }
Write-Utf8 (Join-Path $root 'src/Plugin/Plugin.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><Version>9.9.9</Version></PropertyGroup></Project>'
Reject { & $check -Tag V1.2.3 -Repository $root } '项目版本不一致'
Write-Utf8 (Join-Path $root 'src/Plugin/Plugin.csproj') '<Project Sdk="Microsoft.NET.Sdk" />'
Invoke-FixtureGit @('add','.'); Invoke-FixtureGit @('commit','--quiet','-m','release fixture'); Invoke-FixtureGit @('tag','v1.2.3')
Reject { & $check -Tag V1.2.3 -Repository $root } '版本已存在'
$tagged = & $check -Tag v1.2.3 -Repository $root -Phase Tagged
if ($tagged.triggerTag -cne 'v1.2.3') { throw 'Trigger tag changed' }
Invoke-FixtureGit @('pack-refs','--all','--prune')
Invoke-FixtureGit @('tag','V1.2.3'); Reject { & $check -Tag v1.2.3 -Repository $root -Phase Tagged } '大小写重复'
Invoke-FixtureGit @('tag','-d','V1.2.3')
Version '1.3.0-rc.1+build.2'; Log "# Change Log`n`n## V1.3.0-rc.1+build.2`n`n### ✨ 新功能`n`n- 预发布。`n"
$preview = & $check -Tag V1.3.0-rc.1+build.2 -Repository $root
if (!$preview.prerelease) { throw 'Prerelease was not recognized' }
Invoke-FixtureGit @('add','.'); Invoke-FixtureGit @('commit','--quiet','-m','next fixture')
Reject { & $check -Tag v1.2.3 -Repository $root -Phase Tagged } '精确提交'
Version '1.3.0'; Log "# Change Log`n`n## V1.3.0`n`n### ✨ 新功能`n"
Reject { & $check -Tag V1.3.0 -Repository $root } '为空'
Write-Output 'PASS release metadata: strict SemVer, V/v, duplicate tags, exact commit, effective root/subproject versions, dependency exclusion, first/later release categories, empty sections, target-only body'
