param(
    [Parameter(Mandatory)][uri]$ServerUrl,
    [string]$Output = (Join-Path $PSScriptRoot '../artifacts/exchange-discovery/activesync-authenticated.json'),
    [PSCredential]$Credential,
    [switch]$PromptForCredential,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
if ($ServerUrl.Scheme -ne 'https' -or !$ServerUrl.IsDefaultPort -or $ServerUrl.UserInfo -or $ServerUrl.Query -or $ServerUrl.Fragment) {
    throw 'Use a plain HTTPS server URL without credentials or query parameters.'
}
$endpoint = [uri]($ServerUrl.GetLeftPart([UriPartial]::Authority) + '/Microsoft-Server-ActiveSync')

function Read-Capabilities([Net.Http.HttpResponseMessage]$Response) {
    $versions = @(); $commands = @()
    if ($Response.Headers.Contains('MS-ASProtocolVersions')) {
        $versions = @((($Response.Headers.GetValues('MS-ASProtocolVersions') -join ',') -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+\.\d+$' })
    }
    if ($Response.Headers.Contains('MS-ASProtocolCommands')) {
        $commands = @((($Response.Headers.GetValues('MS-ASProtocolCommands') -join ',') -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[A-Za-z]+$' })
    }
    return [ordered]@{
        Status = [int]$Response.StatusCode
        Authentication = @($Response.Headers.WwwAuthenticate | ForEach-Object Scheme)
        Versions = $versions
        Commands = $commands
        CapabilitiesReturned = ($Response.IsSuccessStatusCode -and $versions.Count -gt 0 -and $commands.Count -gt 0)
    }
}

if ($SelfTest) {
    $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::OK)
    try {
        $response.Headers.TryAddWithoutValidation('MS-ASProtocolVersions', '14.1, 16.0,16.1') | Out-Null
        $response.Headers.TryAddWithoutValidation('MS-ASProtocolCommands', 'Sync, FolderSync,SendMail') | Out-Null
        $response.Headers.TryAddWithoutValidation('Set-Cookie', 'private-value') | Out-Null
        $parsed = Read-Capabilities $response
        if (!$parsed.CapabilitiesReturned -or $parsed.Versions.Count -ne 3 -or $parsed.Commands[1] -ne 'FolderSync') { throw 'Capability parsing failed.' }
        if (($parsed | ConvertTo-Json) -match 'private-value') { throw 'Unexpected header disclosure.' }
        $response.StatusCode = [Net.HttpStatusCode]::Unauthorized
        $response.Headers.WwwAuthenticate.ParseAdd('Basic realm="private-realm"')
        $parsed = Read-Capabilities $response
        if ($parsed.CapabilitiesReturned -or $parsed.Authentication[0] -ne 'Basic' -or ($parsed | ConvertTo-Json) -match 'private-realm') { throw 'Authentication result parsing failed.' }
        $response.Headers.Clear()
        $response.StatusCode = [Net.HttpStatusCode]::OK
        if ((Read-Capabilities $response).CapabilitiesReturned) { throw 'Missing capability headers accepted.' }
    } finally { $response.Dispose() }
    Write-Output 'PASS ActiveSync capability parsing, status handling and header filtering; no network requests'
    return
}

if ($PromptForCredential) {
    $Credential = Get-Credential -Message 'MyMailBox: ActiveSync OPTIONS only. No mail sync, device registration or send. Password is not saved.'
    if (!$Credential) { throw 'Credential entry canceled; no requests sent.' }
}
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseDefaultCredentials = $false
$handler.UseCookies = $false
if ($Credential) {
    # Respond only to Basic challenges at this HTTPS endpoint. Do not pre-send credentials.
    $bound = [Net.CredentialCache]::new()
    $bound.Add($endpoint, 'Basic', $Credential.GetNetworkCredential())
    $handler.Credentials = $bound
}
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(20)
$request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Options, $endpoint)
$response = $null
$record = [ordered]@{ Url = $endpoint.AbsoluteUri; Utc = [DateTime]::UtcNow.ToString('O'); Mode = 'ActiveSync OPTIONS'; CredentialSupplied = [bool]$Credential }
try {
    # One logical OPTIONS operation, including HTTP authentication challenge handling.
    # No command body, DeviceId, DeviceType, policy key, provisioning or synchronization.
    $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    $record.TlsValidated = $true
    foreach ($entry in (Read-Capabilities $response).GetEnumerator()) { $record[$entry.Key] = $entry.Value }
} catch {
    $record.ErrorType = $_.Exception.GetBaseException().GetType().Name
    $record.Result = 'Request failed; no automatic retry or credential fallback'
} finally {
    if ($response) { $response.Dispose() }
    $request.Dispose()
    $client.Dispose()
}
$absolute = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute)) | Out-Null
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $absolute -Encoding utf8
$record | ConvertTo-Json -Depth 5
