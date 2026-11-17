param(
    [string]$Java = 'C:\Program Files\Android\openjdk\jdk-21.0.8\bin\java.exe',
    [string]$Output = (Join-Path $PSScriptRoot '../artifacts/protocol-acceptance'),
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$jar = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/tools/greenmail-standalone-2.1.15.jar'))
if (!(Test-Path -LiteralPath $Java)) { throw '需要已有 Java 11+；请通过 -Java 指定本地路径。' }
if (!(Test-Path -LiteralPath $jar)) { throw '请复用或一次性下载 GreenMail 2.1.15 standalone jar 到 artifacts/tools。脚本不会重复下载。' }
if ((Get-FileHash -LiteralPath $jar -Algorithm SHA256).Hash -ne '91D58798698CAC357F015365240383B936CED2F2A1A9F7DB37D9E3E1A31391B1') { throw 'GreenMail 产物摘要不匹配。' }
$pfx = Join-Path $Output 'localhost.pfx'
if (!(Test-Path -LiteralPath $pfx)) {
    $key = [Security.Cryptography.RSA]::Create(2048)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $key, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('localhost'); $san.AddIpAddress([Net.IPAddress]::Loopback)
    $request.CertificateExtensions.Add($san.Build())
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(30))
    [IO.File]::WriteAllBytes($pfx, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, 'local-test-only'))
    [IO.File]::WriteAllText((Join-Path $Output 'certificate-sha256.txt'), $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256))
    $certificate.Dispose(); $key.Dispose()
}
# No certificate is installed in the user's trust store. Tests explicitly pin this local certificate.
if ($PrepareOnly) { Write-Output "Prepared $Output"; return }
foreach ($port in 3025,3143,3110,3465,3993,3995,8080) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "端口 $port 已被占用，保留现有进程。" }
}
$arguments = @('-Dgreenmail.setup.test.all', '-Dgreenmail.hostname=127.0.0.1', '-Dgreenmail.users=alice:local-alice@localhost,bob:local-bob@localhost', '-Dgreenmail.users.login=email', "-Dgreenmail.tls.keystore.file=`"$pfx`"", '-Dgreenmail.tls.keystore.password=local-test-only', '-jar', "`"$jar`"")
$process = Start-Process -FilePath $Java -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $Output 'greenmail.log') -RedirectStandardError (Join-Path $Output 'greenmail-error.log')
$process.Id | Set-Content (Join-Path $Output 'greenmail.pid')
Write-Output "GreenMail PID $($process.Id); loopback only; output $Output"
