param(
    [Parameter(Mandatory)]$Session,
    [Parameter(Mandatory)][string]$Profile,
    [Parameter(Mandatory)][string]$SourcePst,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
if ($Session.CurrentProfileName -cne $Profile -or !$Profile.StartsWith('MyMailBoxAcceptance') -or $Session.Accounts.Count -ne 0) {
    throw 'This comparison runs only in the dedicated accountless test profile.'
}
$source = (Resolve-Path -LiteralPath $SourcePst).Path
$fixture = Join-Path $Output 'rule-copy.pst'
if (Test-Path -LiteralPath $fixture) { throw 'Use a new evidence directory to preserve earlier results.' }
$originalHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
Copy-Item -LiteralPath $source -Destination $fixture -ErrorAction Stop
$stage = 'open isolated PST copy'
$rules = $null
try {
    $Session.AddStore($fixture)
    $store = @($Session.Stores | Where-Object { $_.FilePath -eq $fixture })[0]
    $root = $store.GetRootFolder()
    $originals = $root.Folders.Item('测试邮件').Folders.Item('嵌套目录')
    $default = $Session.DefaultStore
    $stage = 'GetRules'
    $probes = foreach ($candidate in @(@{name='default';store=$default}, @{name='attached-pst';store=$store})) {
        try {
            $collection = $candidate.store.GetRules()
            [pscustomobject]@{name=$candidate.name; available=$true; count=$collection.Count}
            if ($null -eq $rules) { $rules=$collection; $selected=$candidate.store }
        } catch {
            [pscustomobject]@{name=$candidate.name; available=$false; reason=$_.Exception.Message; hresult=$_.Exception.HResult}
        }
    }
    $probes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'rule-stores.json') -Encoding utf8
    if ($null -eq $rules) {
        [ordered]@{available=$false; stage=$stage; stores=$probes; sourceHash=$originalHash} |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Output 'rule-results.json') -Encoding utf8
        if (@($probes | Where-Object { $_.reason -notmatch '此存储区不支持规则|does not support rules' }).Count) {
            throw 'Unexpected Outlook error while probing the isolated stores; inspect rule-stores.json.'
        }
        Write-Output 'Outlook did not expose rules on either isolated store; see rule-stores.json.'
        return
    }
    $inbox = $selected.GetDefaultFolder(6)
    $sample = $inbox.Folders.Add('MyMailBox rule samples')
    $target = $inbox.Folders.Add('MyMailBox rule results')
    $subjects = @(foreach ($message in $originals.Items) {
        $clone = $message.Copy()
        $moved = $clone.Move($sample)
        [string]$moved.Subject
    })
    if ($subjects.Count -ne 4) { throw 'Expected the existing four-message PST corpus.' }
    $before = $rules.Count
    $stage = 'Rules.Create'
    $rule = $rules.Create('MyMailBox PST comparison',0)
    $rule.Conditions.Subject.Text = [string[]]@('MMB-plain','MMB-html','MMB-rtf')
    $rule.Conditions.Subject.Enabled = $true
    $rule.Exceptions.Subject.Text = [string[]]@('rtf')
    $rule.Exceptions.Subject.Enabled = $true
    $rule.Actions.MoveToFolder.Folder = $target
    $rule.Actions.MoveToFolder.Enabled = $true
    $rule.Actions.Stop.Enabled = $true
    $rule.Enabled = $false
    $stage = 'Rules.Save'
    $rules.Save($false)
    $stage = 'read-only inventory'
    & "$PSScriptRoot/inspect-outlook-rules.ps1" -Profile $Profile -StoreName $selected.DisplayName -Output (Join-Path $Output 'rule-inventory.json')
    $stage = 'Rule.Execute'
    $rule.Execute($false, $sample, $false, 0)
    $matched = @($target.Items | ForEach-Object { [string]$_.Subject })
    if ($matched.Count -ne 2) { throw "Unexpected Outlook match count: $($matched.Count)." }
    [ordered]@{available=$true; before=$before; rule=$rule.Name; subjects=$subjects; matched=$matched; sourceHash=$originalHash} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Output 'rule-results.json') -Encoding utf8
} catch {
    [ordered]@{available=$false; stage=$stage; hresult=$_.Exception.HResult; reason=$_.Exception.Message; sourceHash=$originalHash} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'rule-results.json') -Encoding utf8
    throw
} finally {
    if ($null -ne $root) { $Session.RemoveStore($root) }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $originalHash) { throw 'Original fixture changed.' }
}
