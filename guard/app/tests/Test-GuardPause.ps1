. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes pause

'=== not paused by default'
Check 'relaunch runs' (-not (Get-GuardPauseState relaunch).Paused)

'=== a pause covers both watchdogs, not the log keeper'
$p = Set-GuardPause -Minutes 30 -Reason 'closing NT8 on purpose'
Check 'relaunch paused' ((Get-GuardPauseState relaunch).Paused)
Check 'overwatch paused' ((Get-GuardPauseState overwatch).Paused)
Check 'log keeper keeps archiving' (-not (Get-GuardPauseState logarchive).Paused)
Check 'reason kept' ((Get-GuardPauseState relaunch).Reason -eq 'closing NT8 on purpose')
Check 'pause.set evented' (@(Read-GuardEvents -KindPattern '^pause\.set$').Count -eq 1)

'=== it ends by itself'
$after = (ConvertFrom-GuardUtc $p.untilUtc).AddSeconds(1)
$s = Get-GuardPauseState relaunch -NowUtc $after
Check 'not paused after the end time' (-not $s.Paused)
Check 'file removed' (-not (Test-Path (Get-GuardPausePath)))
Check 'pause.expired evented once' (@(Read-GuardEvents -KindPattern '^pause\.expired$').Count -eq 1)
$null = Get-GuardPauseState relaunch -NowUtc $after
Check '...and only once' (@(Read-GuardEvents -KindPattern '^pause\.expired$').Count -eq 1)

'=== never longer than 24 h, even if the file is edited by hand'
$now = [DateTime]::UtcNow
[IO.File]::WriteAllText((Get-GuardPausePath), (@{ setUtc = $now.ToString('o'); untilUtc = $now.AddDays(3).ToString('o'); reason = 'edited' } | ConvertTo-Json))
Check 'paused at +23 h' ((Get-GuardPauseState relaunch -NowUtc $now.AddHours(23)).Paused)
Check 'NOT paused at +25 h' (-not (Get-GuardPauseState relaunch -NowUtc $now.AddHours(25)).Paused)

'=== an unreadable pause file pauses nothing'
[IO.File]::WriteAllText((Get-GuardPausePath), '{ broken')
$s = Get-GuardPauseState overwatch
Check 'not paused' (-not $s.Paused)
Check 'says corrupt' ($s.Source -eq 'corrupt')
Remove-Item (Get-GuardPausePath) -Force

'=== the old per-component switches are still honoured'
New-Item -ItemType File -Path (Join-Path $h.Nt8 'NT8Relaunch.disabled') | Out-Null
Check 'relaunch paused by NT8Relaunch.disabled' ((Get-GuardPauseState relaunch).Source -eq 'legacy')
Check 'overwatch not paused by it' (-not (Get-GuardPauseState overwatch).Paused)

'=== limits and resume'
foreach ($bad in 0, 1441) {
    $refused = $false; try { Set-GuardPause -Minutes $bad | Out-Null } catch { $refused = $true }
    Check "$bad minutes refused" $refused
}
Remove-Item (Join-Path $h.Nt8 'NT8Relaunch.disabled') -Force
$null = Set-GuardPause -Minutes 5
Clear-GuardPause
Check 'resume clears it' (-not (Get-GuardPauseState relaunch).Paused)
Check 'pause.cleared evented' (@(Read-GuardEvents -KindPattern '^pause\.cleared$').Count -eq 1)

Complete-Test
