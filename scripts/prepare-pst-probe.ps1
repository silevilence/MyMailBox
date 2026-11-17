$ErrorActionPreference = 'Stop'
$cache = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/tools'))
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$zip = Join-Path $cache 'xstreader-source.zip'
$sha = '3b7856c8b3d01bdf8aa13744e476d1ef7761b832'
if (!(Test-Path -LiteralPath $zip)) { Invoke-WebRequest "https://api.github.com/repos/Dijji/XstReader/zipball/$sha" -OutFile $zip }
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne '537CF22DB66EA267F2F9A5E9DF2ED2EF5DA93BC23E3C94C53A4DB8403040ACCC') { throw 'Pinned XstReader source archive hash mismatch.' }
$source = Join-Path $cache 'xstreader'
if (!(Test-Path -LiteralPath $source)) { Expand-Archive -LiteralPath $zip -DestinationPath $source }
dotnet build (Join-Path $PSScriptRoot '../tools/MyMailBox.PstProbe') -r win-x64 --self-contained false
if ($LASTEXITCODE) { throw 'PST probe build failed.' }
