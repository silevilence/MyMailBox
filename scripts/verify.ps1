param([switch]$Ui)
$ErrorActionPreference = 'Stop'
Push-Location "$PSScriptRoot/.."
try {
    dotnet restore MyMailBox.slnx --locked-mode
    if ($LASTEXITCODE) { throw 'Restore failed' }
    dotnet build MyMailBox.slnx --no-restore -v minimal
    if ($LASTEXITCODE) { throw 'Build failed' }
    & "$PSScriptRoot/package-samples.ps1"
    dotnet run --project tests/MyMailBox.Checks --no-build
    if ($LASTEXITCODE) { throw 'Checks failed' }
    & "$PSScriptRoot/publish.ps1"
    & "$PSScriptRoot/check-publish.ps1"
    if ($Ui) {
        dotnet run --project tests/MyMailBox.UiChecks --no-build
        if ($LASTEXITCODE) { throw 'UI acceptance failed' }
    }
} finally { Pop-Location }
