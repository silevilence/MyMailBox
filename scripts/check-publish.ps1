$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$publish = Join-Path $repo 'artifacts/publish'
$runtime = Get-Content (Join-Path $publish 'MyMailBox.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.framework.name -ne 'Microsoft.NETCore.App') { throw 'Not framework dependent' }
if (Test-Path (Join-Path $publish 'coreclr.dll')) { throw 'Bundled runtime detected' }
foreach ($required in 'MyMailBox.Pst.dll','MyMailBox.Pst.deps.json','MyMailBox.Pst.runtimeconfig.json','MimeKit.Core.dll','Notices/Pst-reader.txt','Notices/XstReader-MS-PL.txt') {
    if (!(Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) { throw "Published PST reader is missing $required" }
}
$exe = Join-Path $publish 'MyMailBox.Desktop.exe'
$normal = Start-Process -FilePath $exe -ArgumentList '--runtime-check' -WindowStyle Hidden -PassThru -Wait
if ($normal.ExitCode -ne 0) { throw 'Runtime startup failed' }
$empty = Join-Path $repo 'artifacts/empty-dotnet'
New-Item -ItemType Directory -Path $empty -Force | Out-Null
$info = [Diagnostics.ProcessStartInfo]::new($exe, '--runtime-check')
$info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.RedirectStandardError = $true
$info.Environment['DOTNET_ROOT_X64'] = $empty
$info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$info.Environment['DOTNET_DISABLE_GUI_ERRORS'] = '1'
$missing = [Diagnostics.Process]::Start($info)
$message = $missing.StandardError.ReadToEnd(); $missing.WaitForExit()
if ($missing.ExitCode -eq 0 -or $message -notmatch 'install|安装|framework|运行') { throw "Missing-runtime check failed: $message" }
$message | Set-Content (Join-Path $repo 'artifacts/missing-runtime.txt')
'PASS publish: framework dependent, installed runtime launch, missing runtime install guidance (no runtime downloaded)'
