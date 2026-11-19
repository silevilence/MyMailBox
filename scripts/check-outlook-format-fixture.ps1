# Called by create-outlook-fixtures.ps1 after verifying its disposable, accountless profile.
param([Parameter(Mandatory)]$Session,[Parameter(Mandatory)][string]$Profile,
      [Parameter(Mandatory)][string]$SourcePst,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
if ($Session.CurrentProfileName -cne $Profile -or !$Profile.StartsWith('MyMailBoxAcceptance') -or $Session.Accounts.Count -ne 0) {
    throw 'Expected the dedicated accountless Outlook profile.'
}
$source=(Resolve-Path -LiteralPath $SourcePst).Path
$digest=(Get-FileHash -LiteralPath $source).Hash
$fixture=Join-Path $Output 'input-copy.pst'
$written=Join-Path $Output 'export-unicode.pst'
if ((Test-Path -LiteralPath $fixture) -or (Test-Path -LiteralPath $written)) {throw 'Use a fresh output directory.'}
Copy-Item -LiteralPath $source -Destination $fixture -ErrorAction Stop
function RootFor($path) {
    foreach ($store in $Session.Stores) {
        try { if ($store.FilePath -eq $path) { return $store.GetRootFolder() } }
        finally { Release-Com $store }
    }
    throw 'Expected test store is missing.'
}
function Snapshot($mail,[string]$prefix) {
    $hashes=@(for ($n=1;$n -le $mail.Attachments.Count;$n++) {
        $part=$mail.Attachments.Item($n)
        try {
            $path=Join-Path $Output "$prefix-part-$n.bin"
            $part.SaveAsFile($path)
            [string]$part.FileName + '/' + (Get-FileHash -LiteralPath $path).Hash
        } finally {Release-Com $part}
    })
    [ordered]@{subject=[string]$mail.Subject;body=[string]$mail.Body;html=[string]$mail.HTMLBody;
        sent=[string]$mail.SentOn;received=[string]$mail.ReceivedTime;created=[string]$mail.CreationTime;
        attachments=($hashes -join '|')}
}
function Differences($before,$after) {
    @($before.Keys | Where-Object {$before[$_] -cne $after[$_]})
}
$originalRoot=$null; $newRoot=$null
try {
    $Session.AddStore($fixture); $originalRoot=RootFor $fixture
    $Session.AddStoreEx($written,2); $newRoot=RootFor $written
    $from=$originalRoot.Folders.Item('测试邮件').Folders.Item('嵌套目录')
    $top=$newRoot.Folders.Add('测试邮件'); $dest=$top.Folders.Add('嵌套目录')
    $baseline=@{}; $report=@()
    for ($i=1;$i -le $from.Items.Count;$i++) {
        $mail=$from.Items.Item($i); $before=Snapshot $mail "source-$i"; $baseline[$before.subject]=$before
        $msg=Join-Path $Output "item-$i.msg"; $mail.SaveAs($msg,9)
        $read=$Session.OpenSharedItem($msg)
        $report+=@{format='MSG';item=$i;differences=@(Differences $before (Snapshot $read "msg-$i"))}
        $read.Close(1); Release-Com $read
        $clone=$mail.Copy(); $moved=$clone.Move($dest); Release-Com $moved; Release-Com $clone; Release-Com $mail
    }
    Release-Com $from; Release-Com $dest; Release-Com $top
    $Session.RemoveStore($newRoot); Release-Com $newRoot; $newRoot=$null
    $Session.AddStore($written); $newRoot=RootFor $written
    $dest=$newRoot.Folders.Item('测试邮件').Folders.Item('嵌套目录')
    if ($baseline.Count -ne 4 -or $dest.Items.Count -ne 4) {throw 'Expected four fixture messages in the reopened nested folder.'}
    for ($i=1;$i -le $dest.Items.Count;$i++) {
        $mail=$dest.Items.Item($i); $before=$baseline[[string]$mail.Subject]
        if ($null -eq $before) {throw 'Unexpected item in reopened PST'}
        $report+=@{format='PST';item=$i;differences=@(Differences $before (Snapshot $mail "pst-$i"))}
        Release-Com $mail
    }
    Release-Com $dest
    [ordered]@{version=$Session.Application.Version;accounts=0;messages=$baseline.Count;sourceHash=$digest;
        nestedPath='测试邮件/嵌套目录';comparison=$report} | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $Output 'format-results.json') -Encoding utf8
}
finally {
    if ($null -ne $newRoot) {$Session.RemoveStore($newRoot); Release-Com $newRoot}
    if ($null -ne $originalRoot) {$Session.RemoveStore($originalRoot); Release-Com $originalRoot}
    if ((Get-FileHash -LiteralPath $source).Hash -ne $digest) {throw 'Original sample changed.'}
}
