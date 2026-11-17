param([Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$ExpectedCommit)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/..")
Push-Location $repo
try {
    $actual = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -or $actual -ne $ExpectedCommit) { throw 'Checkout does not match the requested commit' }
    if (git status --porcelain --untracked-files=no) { throw 'Tracked working tree changes are not allowed in CI' }
    $requiredSdk = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
    $sdk = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -or $sdk -ne $requiredSdk) { throw "SDK mismatch: expected $requiredSdk, found $sdk" }
    & "$PSScriptRoot/verify.ps1"
    & "$PSScriptRoot/test-release.ps1"
    & "$PSScriptRoot/package-installer.ps1"
    $publish = Join-Path $repo 'artifacts/publish'
    $inventory = @(Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($publish, $_.FullName); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $output = Join-Path $repo 'artifacts/installer'
    $inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'publish-files.json') -Encoding utf8NoBOM
    [ordered]@{ commit = $actual; sdk = $sdk; rid = 'win-x64'; selfContained = $false; publishAot = $false; publishTrimmed = $false; checkedUtc = [DateTimeOffset]::UtcNow.ToString('O'); runId = $env:GITHUB_RUN_ID } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'ci-manifest.json') -Encoding utf8NoBOM
    'PASS CI build: exact commit, locked dependencies, checks, framework-dependent startup, NSIS and hashes'
} finally { Pop-Location }
