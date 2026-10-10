# The strategy record must survive a bad grid read (2026-09-08: a pre-kill read answered ok with ZERO
# enabled while eight were running, the record was overwritten, and the box came back with everything
# off). Runs the REAL Update-StrategyCache, cut out of Guard-Overwatch.ps1.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
$tmp = New-TestDir owrecord
$script:cacheFile = Join-Path $tmp 'cache.json'
$script:stratLog  = Join-Path $tmp 'trail.log'
$script:logLines  = @()
function Log($m) { $script:logLines += $m }

Invoke-Expression (Get-OverwatchFunction 'Read-CachedEnabled')
Invoke-Expression (Get-OverwatchFunction 'StratKey')
Invoke-Expression (Get-OverwatchFunction 'Update-StrategyCache')

function Rows([string[]]$names) {
    $names | ForEach-Object { [pscustomobject]@{ name = $_; account = "Sim$_"; instrument = 'MNQ 12-26'; enabled = $true } }
}
$EIGHT = Rows @('Alpha', 'Bravo', 'Charlie', 'Delta', 'Echo', 'Foxtrot', 'Golf', 'Hotel')

'=== the actual failure - pre-kill grid answers ok with ZERO enabled'
Update-StrategyCache $EIGHT 'periodic' | Out-Null
$before = Get-Content $cacheFile -Raw | ConvertFrom-Json
Check 'record starts with 8' ($before.enabled.Count -eq 8) "got $($before.enabled.Count)"
Update-StrategyCache @() 'pre-kill' | Out-Null
$after = Get-Content $cacheFile -Raw | ConvertFrom-Json
Check 'pre-kill empty read does NOT erase the record' ($after.enabled.Count -eq 8) "got $($after.enabled.Count)"
Check 'utc NOT refreshed while refusing (so it can still age out)' ($after.utc -eq $before.utc) "$($after.utc) vs $($before.utc)"
Check 'refusal is logged' ([bool]($logLines -match 'REFUSING to erase')) 'no refusal line'

'=== pre-kill stays untrusted no matter how many times it repeats'
Update-StrategyCache @() 'pre-kill' | Out-Null
Update-StrategyCache @() 'pre-kill' | Out-Null
$after = Get-Content $cacheFile -Raw | ConvertFrom-Json
Check 'still 8 after three pre-kill empties' ($after.enabled.Count -eq 8) "got $($after.enabled.Count)"

'=== a pre-kill empty must not COUNT toward the two-strike threshold'
Remove-Item $cacheFile -Force
Update-StrategyCache $EIGHT 'periodic' | Out-Null
Update-StrategyCache @() 'pre-kill' | Out-Null
Update-StrategyCache @() 'periodic' | Out-Null
$after = Get-Content $cacheFile -Raw | ConvertFrom-Json
Check 'one untrusted + one good read does NOT erase the record' ($after.enabled.Count -eq 8) "got $($after.enabled.Count)"
Update-StrategyCache @() 'periodic' | Out-Null
$after = Get-Content $cacheFile -Raw | ConvertFrom-Json
Check 'two GOOD reads still do' ($after.enabled.Count -eq 0) "got $($after.enabled.Count)"

'=== a HEALTHY empty read needs two strikes before it counts'
Remove-Item $cacheFile -Force
Update-StrategyCache $EIGHT 'periodic' | Out-Null
Update-StrategyCache @() 'periodic' | Out-Null
$mid = Get-Content $cacheFile -Raw | ConvertFrom-Json
Check 'first healthy empty is refused' ($mid.enabled.Count -eq 8) "got $($mid.enabled.Count)"
Check 'empty streak recorded' ($mid.emptySeen -eq 1) "emptySeen=$($mid.emptySeen)"
Update-StrategyCache @() 'periodic' | Out-Null
$end = Get-Content $cacheFile -Raw | ConvertFrom-Json
Check 'second healthy empty IS accepted' ($end.enabled.Count -eq 0) "got $($end.enabled.Count)"

'=== a normal non-empty read still overwrites and clears the streak'
Remove-Item $cacheFile -Force
Update-StrategyCache $EIGHT 'periodic' | Out-Null
Update-StrategyCache @() 'periodic' | Out-Null
Update-StrategyCache (Rows @('OnlyOne')) 'periodic' | Out-Null
$one = Get-Content $cacheFile -Raw | ConvertFrom-Json
Check 'non-empty read wins' ($one.enabled.Count -eq 1) "got $($one.enabled.Count)"
Check 'streak reset' ($one.emptySeen -eq 0) "emptySeen=$($one.emptySeen)"

'=== empty read with NO prior record is still allowed (first run)'
Remove-Item $cacheFile -Force
Update-StrategyCache @() 'periodic' | Out-Null
$fresh = Get-Content $cacheFile -Raw | ConvertFrom-Json
Check 'empty record written when there was nothing to protect' ($fresh.enabled.Count -eq 0) "got $($fresh.enabled.Count)"

Complete-Test
