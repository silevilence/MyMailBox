param(
    [string]$Outlook = 'C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE',
    [string]$Output = (Join-Path $PSScriptRoot '../artifacts/outlook-acceptance'),
    [int]$LargeMessages = 32
)
$ErrorActionPreference = 'Stop'
if (Get-Process OUTLOOK -ErrorAction SilentlyContinue) { throw 'Classic Outlook is already running. Preserve it; run this batch after it closes.' }
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$profile = 'MyMailBoxAcceptance' + [Guid]::NewGuid().ToString('N')
$outlookKey = 'HKCU:\Software\Microsoft\Office\16.0\Outlook'
$profileKey = $outlookKey + '\Profiles\' + $profile
if (Test-Path -LiteralPath $profileKey) { throw 'Test profile unexpectedly exists.' }
$defaults = @()
foreach ($key in @($outlookKey,'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows Messaging Subsystem')) {
    $item = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    foreach ($name in @('DefaultProfile','PromptForProfile')) {
        $property = if ($null -ne $item) { $item.PSObject.Properties[$name] } else { $null }
        $defaults += [pscustomobject]@{Key=$key;Name=$name;Exists=($null -ne $property);Value= $(if ($property) {$property.Value} else {$null})}
    }
}
$defaults | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'profile-defaults-before.json') -Encoding utf8
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class OutlookRunning {
 [DllImport("oleaut32.dll", PreserveSig=false)] private static extern void GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object value);
 public static object Get() { var id=new Guid("0006F03A-0000-0000-C000-000000000046"); object value; GetActiveObject(ref id, IntPtr.Zero, out value); return value; }
}
'@
function Release-Com($Value) { if ($null -ne $Value -and [Runtime.InteropServices.Marshal]::IsComObject($Value)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Value) } }
$process = $null; $app = $null; $session = $null; $verified = $false; $generated = @(); $accountCount = $null; $fixtureRecords = [Collections.Generic.List[object]]::new()
try {
    $process = Start-Process -FilePath $Outlook -ArgumentList @('/PIM',$profile) -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(50)
    do {
        try { $app = [OutlookRunning]::Get() } catch { Start-Sleep -Milliseconds 500 }
    } while ($null -eq $app -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $app) { throw 'Outlook COM activation timed out; inspect first-run UI without opening a personal profile.' }
    $session = $app.Session
    if ($session.CurrentProfileName -ne $profile) { throw 'Outlook attached to a different profile; no data will be touched.' }
    $accountCollection=$session.Accounts; $accountCount=$accountCollection.Count; Release-Com $accountCollection
    if ($accountCount -ne 0) { throw 'Test profile unexpectedly contains email accounts.' }
    $verified = $true
    [ordered]@{Profile=$profile;Accounts=$accountCount;Version=$app.Version;SessionType='OOM x64; accountless profile';AccountFields='DisplayName,AccountType,SmtpAddress,UserName,DeliveryStore (no populated account fixture)'} | ConvertTo-Json | Set-Content (Join-Path $Output 'oom-discovery.json') -Encoding utf8
    $attachment = Join-Path $Output 'attachment.txt'; [IO.File]::WriteAllText($attachment, 'MyMailBox attachment 中文 2026', [Text.UTF8Encoding]::new($false))
    $image = Join-Path $Output 'pixel.png'; [IO.File]::WriteAllBytes($image, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII='))
    $bulk = Join-Path $Output 'bulk.bin'
    if (!(Test-Path $bulk)) { $bytes = [byte[]]::new(4MB); [Security.Cryptography.RandomNumberGenerator]::Fill($bytes); [IO.File]::WriteAllBytes($bulk,$bytes) }
    foreach ($format in @(@{Name='unicode';Type=2},@{Name='ansi';Type=3})) {
        $path = Join-Path $Output ($format.Name + '.pst')
        if (Test-Path -LiteralPath $path) { throw "Fixture already exists; preserve and reuse: $path" }
        $session.AddStoreEx($path,$format.Type)
        $stores=$session.Stores; $store=$null
        for ($i=1; $i -le $stores.Count; $i++) { $candidate=$stores.Item($i); if ($candidate.FilePath -eq $path) {$store=$candidate;break}; Release-Com $candidate }
        Release-Com $stores
        if ($null -eq $store) { throw 'Created PST was not found in the isolated profile.' }
        $root=$store.GetRootFolder(); $root.Name='MyMailBox-' + $format.Name
        $rootFolders=$root.Folders; $folder=$rootFolders.Add('测试邮件',6); Release-Com $rootFolders
        $folders=$folder.Folders; $nested=$folders.Add('嵌套目录',6); Release-Com $folders
        $items=$nested.Items
        $plain=$items.Add('IPM.Note'); $plain.Subject='MMB-plain-中文'; $plain.BodyFormat=1; $plain.Body='纯文本正文 Alpha 中文'; $plain.To='bob@localhost'; $plain.CC='alice@localhost'; $plain.Save()
        $atts=$plain.Attachments; $att=$atts.Add($attachment,1); Release-Com $att; Release-Com $atts; $plain.Save()
        $html=$items.Add('IPM.Note'); $html.Subject='MMB-html-CID'; $html.BodyFormat=2; $html.HTMLBody='<html><body><h1>HTML 中文</h1><p>Beta</p><img src="cid:mmb-pixel"></body></html>'; $html.Save()
        $atts=$html.Attachments; $att=$atts.Add($image,1); $properties=$att.PropertyAccessor
        $properties.SetProperty('http://schemas.microsoft.com/mapi/proptag/0x3712001F','mmb-pixel'); Release-Com $properties; Release-Com $att; Release-Com $atts; $html.Save()
        $rtf=$items.Add('IPM.Note'); $rtf.Subject='MMB-rtf'; $rtf.BodyFormat=3; $rtf.Body='RTF 正文 Gamma'; $rtf.Save()
        $outer=$items.Add('IPM.Note'); $outer.Subject='MMB-embedded'; $outer.Body='Outer message'; $outer.Save()
        $atts=$outer.Attachments; $att=$atts.Add($plain,5); Release-Com $att; Release-Com $atts; $outer.Save()
        if ($format.Name -eq 'unicode') {
            $folders=$folder.Folders; $large=$folders.Add('扩容样本',6); Release-Com $folders; $largeItems=$large.Items
            for ($n=0;$n -lt $LargeMessages;$n++) {
                $mail=$largeItems.Add('IPM.Note'); $mail.Subject='MMB-large-' + $n; $mail.Body='Large attachment fixture'; $mail.Save(); $atts=$mail.Attachments; $att=$atts.Add($bulk,1); Release-Com $att; Release-Com $atts; $mail.Save(); Release-Com $mail
            }
            Release-Com $largeItems; Release-Com $large
            $folders=$root.Folders; $contacts=$folders.Add('非邮件联系人',10); Release-Com $folders; $contactItems=$contacts.Items; $contact=$contactItems.Add('IPM.Contact'); $contact.FullName='MyMailBox Test Contact'; $contact.Save(); Release-Com $contact; Release-Com $contactItems; Release-Com $contacts
        }
        foreach ($item in @($plain,$html,$rtf,$outer)) {
            $fixtureRecords.Add([ordered]@{Pst=$format.Name;Subject=$item.Subject;Body=$item.Body;Html=$item.HTMLBody;BodyFormat=$item.BodyFormat;Attachments=$item.Attachments.Count})
        }
        foreach ($item in @($plain,$html,$rtf,$outer,$items,$nested,$folder)) { Release-Com $item }
        $session.RemoveStore($root); Release-Com $root; Release-Com $store; $generated += $path
    }
    $fixtureRecords | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Output 'outlook-expected.json') -Encoding utf8
    @($attachment,$image,$bulk) | ForEach-Object { $hash=Get-FileHash -LiteralPath $_ -Algorithm SHA256; [ordered]@{Name=[IO.Path]::GetFileName($_);Bytes=(Get-Item -LiteralPath $_).Length;Sha256=$hash.Hash} } | ConvertTo-Json | Set-Content (Join-Path $Output 'attachments-expected.json') -Encoding utf8
}
finally {
    if ($verified -and $null -ne $app) { try {$app.Quit()} catch { Write-Warning $_ } }
    Release-Com $session; Release-Com $app; [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    if ($verified -and $null -ne $process -and !$process.WaitForExit(15000)) { Write-Warning 'Test Outlook has not exited; retain profile and PST until released.' }
    foreach ($entry in $defaults) {
        $current=Get-ItemProperty -LiteralPath $entry.Key -Name $entry.Name -ErrorAction SilentlyContinue
        if ($entry.Exists) { if ($current.($entry.Name) -ne $entry.Value) { Set-ItemProperty -LiteralPath $entry.Key -Name $entry.Name -Value $entry.Value } }
        elseif ($null -ne $current) { Remove-ItemProperty -LiteralPath $entry.Key -Name $entry.Name -ErrorAction Stop }
    }
    if (!(Get-Process OUTLOOK -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $profileKey)) {
        # Verified newly generated key only; never remove another profile.
        if ($profileKey -ne ($outlookKey + '\Profiles\' + $profile) -or !$profile.StartsWith('MyMailBoxAcceptance')) { throw 'Profile cleanup scope mismatch.' }
        Remove-Item -LiteralPath $profileKey -Recurse -ErrorAction Stop
    }
}
foreach ($path in $generated) {
    $copy=Join-Path $Output ('read-' + [IO.Path]::GetFileName($path)); Copy-Item -LiteralPath $path -Destination $copy -ErrorAction Stop
    Get-FileHash -LiteralPath $path,$copy -Algorithm SHA256
}
Write-Output "Completed isolated Outlook fixture session: $profile"
