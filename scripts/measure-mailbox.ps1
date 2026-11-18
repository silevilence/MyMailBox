param(
    [ValidateSet(10000,100000)][int[]]$Size = @(10000,100000),
    [switch]$PrepareOnly,
    [switch]$WithSync
)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $project = 'tests/MyMailBox.Checks'
    dotnet build $project --no-restore -v:q
    if ($LASTEXITCODE) { throw '检查程序构建失败。' }
    function Invoke-Check([string[]]$CheckArguments) {
        dotnet run --project $project --no-build -- @CheckArguments
        if ($LASTEXITCODE) { throw "基准检查失败 ($($CheckArguments -join ' '))。" }
    }
    foreach ($count in $Size) {
        Invoke-Check @('perf-prepare',"$count")
        $root = "artifacts/performance-v1/$count"
        $machine = [ordered]@{
            os = (Get-CimInstance Win32_OperatingSystem).Caption
            build = [Environment]::OSVersion.Version.ToString()
            cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
            ramBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
            storage = @(Get-PhysicalDisk | Select-Object FriendlyName, MediaType)
        }
        $machine | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath "$root/machine.json" -Encoding utf8
        if ($PrepareOnly) { continue }
        Invoke-Check @('perf-index',"$count")
        foreach ($state in 'source','indexed') {
            Invoke-Check @('perf-measure',"$count",$state,'idle')
            if ($WithSync) { Invoke-Check @('perf-measure',"$count",$state,'sync') }
        }
    }
} finally { Pop-Location }
