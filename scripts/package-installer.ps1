param([string]$PublishDirectory = "$PSScriptRoot/../artifacts/publish", [string]$OutputDirectory = "$PSScriptRoot/../artifacts/installer", [string]$MakeNsis = "${env:ProgramFiles(x86)}/NSIS/makensis.exe")
$ErrorActionPreference = 'Stop'
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (!(Test-Path -LiteralPath $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory | Out-Null }
if (!(Test-Path -LiteralPath $MakeNsis)) { throw 'NSIS 未安装。请指定已有 makensis.exe；本脚本不会下载工具。' }
$version = (& dotnet msbuild "$PSScriptRoot/../src/MyMailBox.Desktop/MyMailBox.Desktop.csproj" -nologo -getProperty:Version | Out-String).Trim()
if ($LASTEXITCODE -or $version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { throw '应用版本无效。' }
$runtime = Get-Content -LiteralPath (Join-Path $PublishDirectory 'MyMailBox.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.framework.name -ne 'Microsoft.NETCore.App' -or (Test-Path -LiteralPath (Join-Path $PublishDirectory 'coreclr.dll'))) { throw '仅允许框架依赖产物。' }
$files = @(Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse | Sort-Object FullName)
$relative = @($files | ForEach-Object { [IO.Path]::GetRelativePath($PublishDirectory, $_.FullName) })
if ($relative | Where-Object { $_ -match '["$]' }) { throw '产物路径包含 NSIS 特殊字符。' }
$uninstall = Join-Path $OutputDirectory 'uninstall-files.nsh'
$lines = @($relative | ForEach-Object { 'Delete "$INSTDIR\' + $_ + '"' })
$directories = @(Get-ChildItem -LiteralPath $PublishDirectory -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending)
$lines += @($directories | ForEach-Object { 'RMDir "$INSTDIR\' + [IO.Path]::GetRelativePath($PublishDirectory, $_.FullName) + '"' })
[IO.File]::WriteAllLines($uninstall, $lines, [Text.UTF8Encoding]::new($false))
$output = Join-Path $OutputDirectory "MyMailBox-V$version-win-x64-setup.exe"
& $MakeNsis /V2 /INPUTCHARSET UTF8 "/DAPP_VERSION=$version" "/DPUBLISH_DIR=$PublishDirectory" "/DOUTPUT_FILE=$output" "/DUNINSTALL_FILES=$uninstall" "$PSScriptRoot/../installer/MyMailBox.nsi"
if ($LASTEXITCODE) { throw 'NSIS 编译失败。' }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($output))`n", [Text.UTF8Encoding]::new($false))
$manifest = [ordered]@{ version = $version; runtime = 'Microsoft.NETCore.App 10 x64'; frameworkDependent = $true; installer = [IO.Path]::GetFileName($output); size = (Get-Item -LiteralPath $output).Length; sha256 = $hash; files = $relative }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'build-manifest.json'), ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Output "PASS NSIS package: $output ($hash)"
