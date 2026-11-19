# Attach only to the already-open, explicitly named classic Outlook test profile.
[CmdletBinding(DefaultParameterSetName='Live')]
param(
    [Parameter(Mandatory, ParameterSetName='Live')][string]$Profile,
    [Parameter(Mandatory, ParameterSetName='Live')][string]$StoreName,
    [Parameter(Mandatory, ParameterSetName='Live')][string]$Output,
    [Parameter(Mandatory, ParameterSetName='SelfTest')][switch]$SelfTest
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

function Read-RuleParts($Collection, [bool]$Actions) {
    for ($index=1; $index -le $Collection.Count; $index++) {
        $part = $Collection.Item($index)
        if (!$part.Enabled) { continue }
        $type = if ($Actions) { [int]$part.ActionType } else { [int]$part.ConditionType }
        $result = [ordered]@{ index=$index; type=$type; field=''; values=@(); candidate=$false; reason='' }
        try {
            if (!$Actions -and $type -in 2,13,14,16,17) {
                $result.field = @{2='subject';13='body';14='subject-or-body';16='recipient-address';17='sender-address'}[$type]
                $result.values = @($part.Text | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
                $result.candidate = $result.values.Count -gt 0
            } elseif ($Actions -and $type -in 19,21) {
                $result.field = if ($type -eq 19) {'mark-read'} else {'stop'}
                $result.candidate = $true
            } elseif ($Actions -and $type -in 1,5) {
                $result.field = if ($type -eq 1) {'move'} else {'copy'}
                $folder = $part.Folder
                $result.values = @($folder.FolderPath, $folder.EntryID, $folder.StoreID)
                $result.reason = 'Requires explicit destination mapping.'
            } elseif ($Actions -and $type -eq 2) {
                $result.field = 'categories'
                $result.values = @($part.Categories | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
                $result.candidate = $result.values.Count -gt 0
            } else {
                $result.field = if ($Actions) {"OlRuleActionType/$type"} else {"OlRuleConditionType/$type"}
                $result.reason = 'Unsupported or opaque to the Outlook object model. Review the original rule.'
            }
            if (!$result.candidate -and !$result.reason) { $result.reason='Source value missing.' }
        } catch { $result.candidate=$false; $result.reason="Source field unavailable (HRESULT $($_.Exception.HResult))." }
        [pscustomobject]$result
    }
}
function Read-Rule($Rule) {
    $conditions = @(Read-RuleParts $Rule.Conditions $false)
    $exceptions = @(Read-RuleParts $Rule.Exceptions $false)
    $actions = @(Read-RuleParts $Rule.Actions $true)
    $unknown = @(@($conditions + $exceptions + $actions) | Where-Object { !$_.candidate })
    [pscustomobject][ordered]@{
        name=$Rule.Name; order=[int]$Rule.ExecutionOrder; sourceEnabled=[bool]$Rule.Enabled
        clientOnly=[bool]$Rule.IsLocalRule; direction=[int]$Rule.RuleType
        conditions=$conditions; exceptions=$exceptions; actions=$actions
        candidate=([int]$Rule.RuleType -eq 0 -and $unknown.Count -eq 0 -and $actions.Count -gt 0)
        importEnabled=$false
    }
}
if ($SelfTest) {
    function Collection($Items) {
        $list = [pscustomobject]@{ Count=$Items.Count; Entries=$Items }
        $list | Add-Member ScriptMethod Item { param($n) $this.Entries[$n-1] }
        $list
    }
    $r = [pscustomobject]@{ Name='Sample';ExecutionOrder=3;Enabled=$true;IsLocalRule=$false;RuleType=0
        Conditions=(Collection @([pscustomobject]@{ Enabled=$true;ConditionType=2;Text=@('receipt','order') }))
        Exceptions=(Collection @([pscustomobject]@{ Enabled=$true;ConditionType=13;Text=@('ignore') }))
        Actions=(Collection @([pscustomobject]@{Enabled=$true;ActionType=19},[pscustomobject]@{Enabled=$true;ActionType=21})) }
    $valid=Read-Rule $r
    if (!$valid.candidate -or $valid.importEnabled -or $valid.exceptions.Count -ne 1 -or $valid.actions[1].field -ne 'stop') {throw 'Candidate semantics lost.'}
    $r.Exceptions=Collection @([pscustomobject]@{Enabled=$true;ConditionType=222})
    $r.Actions=Collection @([pscustomobject]@{Enabled=$true;ActionType=1},[pscustomobject]@{Enabled=$true;ActionType=20})
    $blocked=Read-Rule $r
    if ($blocked.candidate -or $blocked.exceptions[0].type -ne 222 -or !$blocked.actions[0].reason -or $blocked.actions[1].type -ne 20) {throw 'Unknown or missing source fields were dropped.'}
    Write-Output 'PASS offline Outlook rule inventory: candidate, exception, order, stop, script, missing destination and unknown type'
    return
}
$path = [IO.Path]::GetFullPath($Output)
if ([IO.File]::Exists($path)) { throw 'Choose a new inventory file.' }
if ($PSVersionTable.PSEdition -ne 'Desktop' -and !$IsWindows) { throw 'Classic Outlook requires Windows.' }
if (!('OutlookRuleInventory.RunningObject' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace OutlookRuleInventory {
    public static class RunningObject {
        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(ref Guid id, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        public static object Outlook() {
            var id = Type.GetTypeFromProgID("Outlook.Application", true).GUID;
            object value;
            GetActiveObject(ref id, IntPtr.Zero, out value);
            return value;
        }
    }
}
'@
}
$app = [OutlookRuleInventory.RunningObject]::Outlook()
$session=$app.Session
if ($session.CurrentProfileName -cne $Profile) { throw 'The open Outlook profile does not match the requested profile.' }
$stores=@($session.Stores | Where-Object { $_.DisplayName -ceq $StoreName })
if ($stores.Count -ne 1) { throw 'Choose a unique store display name in this profile.' }
$rules=$stores[0].GetRules()
$entries=@(for ($index=1; $index -le $rules.Count; $index++) { Read-Rule ($rules.Item($index)) })
$data=[ordered]@{ schema=1; profile=$Profile; store=$StoreName; rules=$entries }
$stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {
    $writer=New-Object IO.StreamWriter($stream)
    try { $writer.Write(($data | ConvertTo-Json -Depth 12)) } finally { $writer.Dispose() }
} finally { $stream.Dispose() }
Write-Output ("Captured {0} rules." -f $entries.Count)
