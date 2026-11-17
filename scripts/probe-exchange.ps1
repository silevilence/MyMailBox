param(
    [Parameter(Mandatory)][uri]$OwaUrl,
    [string]$Output = (Join-Path $PSScriptRoot '../artifacts/exchange-discovery/probe.json'),
    [PSCredential]$Credential,
    [switch]$PromptForCredential,
    [ValidateSet('Auto','NTLM')][string]$Authentication = 'Auto',
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
if ($OwaUrl.Scheme -ne 'https' -or !$OwaUrl.IsDefaultPort -or $OwaUrl.UserInfo -or $OwaUrl.Query -or $OwaUrl.Fragment) { throw 'Use a plain HTTPS OWA URL, without credentials or query parameters.' }
if ($PromptForCredential) {
    $Credential=Get-Credential -Message 'MyMailBox: read-only EWS GetFolder. No mail read/send/change. Password is not saved.'
    if (!$Credential) { throw 'Credential entry canceled; no requests sent.' }
}
function Read-EwsMetadata([string]$Content) {
    $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create([IO.StringReader]::new($Content), $settings)
    try {
        $doc = [Xml.XmlDocument]::new(); $doc.XmlResolver = $null; $doc.Load($reader)
        $ns = [Xml.XmlNamespaceManager]::new($doc.NameTable)
        $ns.AddNamespace('m', 'http://schemas.microsoft.com/exchange/services/2006/messages')
        $ns.AddNamespace('t', 'http://schemas.microsoft.com/exchange/services/2006/types')
        $code = $doc.SelectSingleNode('//m:ResponseCode', $ns)
        $version = $doc.SelectSingleNode('//t:ServerVersionInfo', $ns)
        return [ordered]@{ResponseCode=if($code){$code.InnerText}else{'MissingResponseCode'};ServerVersion=if($version){$version.GetAttribute('Version')}else{$null};Major=if($version){$version.GetAttribute('MajorVersion')}else{$null};Minor=if($version){$version.GetAttribute('MinorVersion')}else{$null};AuthenticatedRootRead=($null -ne $code -and $code.InnerText -eq 'NoError')}
    } finally { $reader.Dispose() }
}
if ($SelfTest) {
    $xml='<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Header><t:ServerVersionInfo xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types" Version="V2018_01_08" MajorVersion="15" MinorVersion="2"/></s:Header><s:Body><m:GetFolderResponse xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages"><m:ResponseCode>NoError</m:ResponseCode></m:GetFolderResponse></s:Body></s:Envelope>'
    $parsed=Read-EwsMetadata $xml
    if (!$parsed.AuthenticatedRootRead -or $parsed.Major -ne '15') { throw 'Metadata parser failed.' }
    $rejected=$false
    try { Read-EwsMetadata '<!DOCTYPE x [<!ENTITY e SYSTEM "file:///not-allowed">]><x>&e;</x>' | Out-Null } catch { $rejected=$true }
    if (!$rejected) { throw 'DTD was accepted.' }
    Write-Output 'PASS Exchange metadata parser and DTD rejection; no network requests'; return
}
$origin=$OwaUrl.GetLeftPart([UriPartial]::Authority)
$ews=[uri]($origin + '/EWS/Exchange.asmx')
$handler=[Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect=$false; $handler.UseDefaultCredentials=$false
if($Credential) {
    if($Authentication -eq 'NTLM') {
        $bound=[Net.CredentialCache]::new(); $bound.Add($ews,'NTLM',$Credential.GetNetworkCredential()); $handler.Credentials=$bound
    } else { $handler.Credentials=$Credential.GetNetworkCredential() }
}
$client=[Net.Http.HttpClient]::new($handler); $client.Timeout=[TimeSpan]::FromSeconds(20)
$records=[Collections.Generic.List[object]]::new()
try {
    # Anonymous discovery uses two GET requests; authenticated mode only sends one read-only EWS operation.
    $targets=if($Credential){@($ews)}else{@($OwaUrl,$ews)}
    foreach($url in $targets) {
        $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$url)
        if($Credential) {
            $request.Method=[Net.Http.HttpMethod]::Post
            $soap=@'
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages" xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"><s:Header><t:RequestServerVersion Version="Exchange2010_SP2"/></s:Header><s:Body><m:GetFolder><m:FolderShape><t:BaseShape>IdOnly</t:BaseShape></m:FolderShape><m:FolderIds><t:DistinguishedFolderId Id="msgfolderroot"/></m:FolderIds></m:GetFolder></s:Body></s:Envelope>
'@
            $request.Content=[Net.Http.StringContent]::new($soap,[Text.Encoding]::UTF8,'text/xml')
            $request.Headers.TryAddWithoutValidation('SOAPAction','http://schemas.microsoft.com/exchange/services/2006/messages/GetFolder') | Out-Null
        }
        $response=$null
        try {
            $response=$client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $record=[ordered]@{Url=$url.AbsoluteUri;Utc=[DateTime]::UtcNow.ToString('O');Status=[int]$response.StatusCode;TlsValidated=$true;Authentication=@($response.Headers.WwwAuthenticate | ForEach-Object Scheme);Redirect=if($response.Headers.Location){$response.Headers.Location.OriginalString.Split('?')[0]}else{$null};Mode=if($Credential){'EWS GetFolder IdOnly'}else{'Anonymous GET'}}
            $record.SelectedAuthentication=$Authentication
            if($Credential -and $response.IsSuccessStatusCode) {
                $response.Content.LoadIntoBufferAsync(1048576).GetAwaiter().GetResult()
                $record.Metadata=Read-EwsMetadata ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
            }
            $records.Add($record)
        } catch { $records.Add([ordered]@{Url=$url.AbsoluteUri;Utc=[DateTime]::UtcNow.ToString('O');ErrorType=$_.Exception.GetBaseException().GetType().Name;Result='Request failed; no automatic retry or credential fallback'}) }
        finally { if($response){$response.Dispose()}; $request.Dispose() }
    }
} finally { $client.Dispose() }
$absolute=[IO.Path]::GetFullPath($Output); [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute)) | Out-Null
$records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $absolute -Encoding utf8
$records | ConvertTo-Json -Depth 5
# No response bodies, usernames, passwords, cookies, tokens or real folder IDs are persisted.
