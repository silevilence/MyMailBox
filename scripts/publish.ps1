$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot/../src/MyMailBox.Desktop" -c Release -r win-x64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:RestoreLockedMode=true -o "$PSScriptRoot/../artifacts/publish"
if ($LASTEXITCODE) { throw 'Publish failed' }
