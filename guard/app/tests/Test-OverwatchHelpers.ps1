. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes owhelpers

'=== settings: Protection decides act or alert; the checks themselves always run'
$s = Get-OverwatchSettings (Get-GuardDefaults)
Check 'Recover NT8: restart a frozen NT8 and reconnect' ($s.restartWhenFrozen -and $s.reconnect)
Check 'Recover NT8: re-enable and flatten are ALERT ONLY' (-not $s.reenableStrategies -and -not $s.flattenNakedBeforeEnable)
Check 'crash reports off by default' (-not $s.dumpBeforeKill)
Check 'record, restore pass and exposure check always on' ($s.restoreStrategies -and $s.restoreConnections -and $s.exposureGate)
Check 'thresholds carried' ($s.guardianStaleSeconds -eq 90 -and $s.consecutiveToAct -eq 3 -and $s.backupEveryMinutes -eq 30)
Check 'overwatch timings carried' ($s.startupGraceSeconds -eq 300 -and $s.relauncherStaleSeconds -eq 180 -and $s.dumpKeep -eq 2)
Check 'market time zone from the watch settings' ($s.marketTimeZoneId -eq 'Eastern Standard Time')
$c = Get-GuardDefaults; $c.protection.preset = 'handsoff'; $c.protection.crashDumps = $true
$s = Get-OverwatchSettings $c
Check 'Hands-off: re-enable and flatten act; crash reports when switched on' ($s.reenableStrategies -and $s.flattenNakedBeforeEnable -and $s.dumpBeforeKill)
$c = Get-GuardDefaults; $c.protection.preset = 'watch'
$s = Get-OverwatchSettings $c
Check 'Watch only: nothing acts' (-not $s.restartWhenFrozen -and -not $s.reconnect -and -not $s.reenableStrategies -and -not $s.flattenNakedBeforeEnable)
Check 'Watch only: starting NinjaTrader is off too (the restart path must know)' ($s.relaunch -eq $false)
Check 'Recover NT8: starting NinjaTrader is on' ((Get-OverwatchSettings (Get-GuardDefaults)).relaunch -eq $true)

'=== alert priorities map to severities'
Check '5 alert, 4 warn, 3 info' ((ConvertTo-GuardSeverity '5') -eq 'alert' -and (ConvertTo-GuardSeverity '4') -eq 'warn' -and (ConvertTo-GuardSeverity '3') -eq 'info')

'=== is the relauncher running? decided from its heartbeat as it is now'
$hb = Join-Path (Get-GuardPath state) 'relaunch.heartbeat'
$now = [DateTime]::UtcNow
Check 'no heartbeat -> not running' (-not (Test-RelauncherFresh -NowUtc $now))
[IO.File]::WriteAllText($hb, $now.AddSeconds(-50).ToString('o'))
Check '50 s old -> running' (Test-RelauncherFresh -NowUtc $now)
[IO.File]::WriteAllText($hb, $now.AddMinutes(-4).ToString('o'))
Check '4 min old -> not running (task gone, disabled or broken)' (-not (Test-RelauncherFresh -NowUtc $now))
[IO.File]::WriteAllText($hb, 'garbage')
Check 'unreadable -> not running' (-not (Test-RelauncherFresh -NowUtc $now))

'=== feed-guard events are read from NinjaTrader''s folder'
$fg = Get-FeedGuardEventsPath $h.Nt8
New-Item -ItemType Directory -Path (Split-Path $fg -Parent) -Force | Out-Null
$t1 = [DateTime]::UtcNow.AddMinutes(-5)
[IO.File]::WriteAllLines($fg, @(
    (@{ utc = $t1.ToString('o'); component = 'feedguard'; kind = 'feed.source-outage'; severity = 'warn'; text = 'x' } | ConvertTo-Json -Compress),
    (@{ utc = $t1.AddMinutes(1).ToString('o'); component = 'feedguard'; kind = 'feed.strategies-disabled'; severity = 'warn'; text = 'y' } | ConvertTo-Json -Compress)))
$got = Get-FeedEventUtc -Nt8Dir $h.Nt8 -KindPattern '^feed\.source-outage$' -SinceUtc ([DateTime]::UtcNow.AddMinutes(-20))
Check 'finds the source-outage verdict' ($null -ne $got -and [math]::Abs(($got - $t1).TotalSeconds) -lt 1) "$got"
Check 'respects the window' ($null -eq (Get-FeedEventUtc -Nt8Dir $h.Nt8 -KindPattern '^feed\.source-outage$' -SinceUtc ([DateTime]::UtcNow.AddMinutes(-2))))
Check 'only the feed guard''s events count' ($null -eq (Get-FeedEventUtc -Nt8Dir $h.Nt8 -KindPattern '^nt8\.' -SinceUtc ([DateTime]::UtcNow.AddHours(-1))))

'=== strategy backups: saved under the start lock; each change alerted once, in plain words'
$cfg = Get-GuardDefaults
$script:calls = @()
function Report([string]$Json) { $script:out = $Json }
$py = { param($s, $a, $t) $script:calls += , (@($s) + $a); [pscustomobject]@{ ExitCode = 0; TimedOut = $false; Err = ''; Out = $script:out } }
Report '{"saved":[{"name":"A"},{"name":"B"}],"failed":[],"duplicates":[],"transitions":[],"warnings":[]}'
$r = Invoke-BackupSave -Config $cfg -Nt8Dir $h.Nt8 -Python $py
Check 'ran backup_export.py with --json' ($r.Result -eq 'saved' -and $script:calls[0][0] -eq 'backup_export.py' -and $script:calls[0] -contains '--json')
Check 'summary counts' ($r.Summary -match '2 saved, 0 failed, 0 missing, 0 removed') $r.Summary
Check 'a quiet save alerts nobody' (@(Read-GuardEvents -KindPattern '^alert\.sent$').Count -eq 0)
Report '{"saved":[],"failed":[],"duplicates":[],"transitions":[{"name":"Gone One","old":"live","new":"removed"},{"name":"Lost One","old":"live","new":"missing"}],"warnings":[]}'
$null = Invoke-BackupSave -Config $cfg -Nt8Dir $h.Nt8 -Python $py
$al = @(Read-GuardEvents -KindPattern '^alert\.sent$')
Check 'removed while running -> an alert' ([bool]($al | Where-Object { $_.text -match 'removed while running' }))
Check 'missing after a restart -> a notice' ([bool]($al | Where-Object { $_.text -match 'strategies missing' }))
Check 'the names reach the events' ([bool](Read-GuardEvents -KindPattern '^backups\.removed$' | Where-Object { $_.text -match 'Gone One' }))
$wcfg = Get-GuardDefaults; $wcfg.protection.preset = 'watch'
Check 'missing, Recover NT8: Guard puts them back itself' ((Get-BackupAdvice -Config $cfg -Kind missing) -match 'automatically')
$adv = Get-BackupAdvice -Config $wcfg -Kind missing
Check 'missing, Watch only: never promises an automatic put-back' ($adv -notmatch 'automatically the next' -and $adv -match 'alert only') $adv
Check 'missing, Watch only: points at the panel''s Put back now (it pauses Guard itself)' ($adv -match 'Put back now' -and $adv -match 'pauses Guard' -and $adv -notmatch 'backup_restore') $adv
$adv = Get-BackupAdvice -Config $cfg -Kind removed
Check 'removed: points at Bring back' ($adv -match 'Bring back' -and $adv -notmatch 'backup_restore') $adv
Report '{"saved":[],"failed":[{"name":"Broken","error":"ValueError: x"}],"duplicates":[],"transitions":[],"warnings":[]}'
$n0 = @(Read-GuardEvents -KindPattern '^alert\.sent$').Count
$null = Invoke-BackupSave -Config $cfg -Nt8Dir $h.Nt8 -Python $py
$null = Invoke-BackupSave -Config $cfg -Nt8Dir $h.Nt8 -Python $py
Check 'a strategy that keeps failing alerts once, not every save' ((@(Read-GuardEvents -KindPattern '^alert\.sent$').Count - $n0) -eq 1)
$bad = { param($s, $a, $t) [pscustomobject]@{ ExitCode = 1; TimedOut = $false; Out = ''; Err = 'ERROR: no NinjaTrader database' } }
$n0 = @(Read-GuardEvents -KindPattern '^alert\.sent$').Count
$r = Invoke-BackupSave -Config $cfg -Nt8Dir $h.Nt8 -Python $bad
$null = Invoke-BackupSave -Config $cfg -Nt8Dir $h.Nt8 -Python $bad
Check 'cannot save -> failed, says why' ($r.Result -eq 'failed' -and $r.Summary -match 'no NinjaTrader database') $r.Summary
Check 'the same error alerts once' ((@(Read-GuardEvents -KindPattern '^alert\.sent$').Count - $n0) -eq 1)
$held = Open-GuardStartLock
$r = Invoke-BackupSave -Config $cfg -Nt8Dir $h.Nt8 -Python $py
$held.Dispose()
Check 'a start in progress -> busy, nothing run' ($r.Result -eq 'busy')

'=== two live copies of one strategy are spotted in NinjaTrader''s log'
$L = @(
    "2026-09-21 16:58:01:100|1|4|Enabling NinjaScript strategy 'Alpha/101' : On starting a real-time strategy",
    "2026-09-21 16:58:02:900|1|4|Enabling NinjaScript strategy 'Alpha/101' : On starting a real-time strategy",
    "2026-09-21 16:58:03:000|1|4|Enabling NinjaScript strategy 'Bravo/102' : On starting",
    "2026-09-21 16:58:03:500|1|4|Disabling NinjaScript strategy 'Bravo/102'",
    "2026-09-21 16:58:04:000|1|4|Enabling NinjaScript strategy 'Bravo/102' : On starting",
    "2026-09-21 16:58:20:000|1|4|Enabling NinjaScript strategy 'Charlie/103' : On starting",
    "2026-09-21 16:58:30:000|1|4|Enabling NinjaScript strategy 'Charlie/103' : On starting",
    "garbage line")
$d = Find-DuplicateStrategyStarts -Lines $L
Check 'the 1.8 s pair is found' (@($d.Duplicates).Count -eq 1 -and $d.Duplicates[0].Strategy -eq 'Alpha/101') (@($d.Duplicates | ForEach-Object { $_.Strategy }) -join ',')
Check 'enable -> disable -> enable is a normal restart' (-not ($d.Duplicates | Where-Object { $_.Strategy -eq 'Bravo/102' }))
Check 'ten seconds apart is not a pair' (-not ($d.Duplicates | Where-Object { $_.Strategy -eq 'Charlie/103' }))
Check 'newest banner time returned' ($d.NewestLocal -eq [DateTime]'2026-09-21 16:58:30')
$d2 = Find-DuplicateStrategyStarts -Lines $L -SinceLocal $d.NewestLocal
Check 'already-reported pairs are not reported again' (@($d2.Duplicates).Count -eq 0)
$d3 = Find-DuplicateStrategyStarts -Lines $L -SinceLocal ([DateTime]'2026-09-21 16:58:02')
Check 'a pair split across two scans is still found' (@($d3.Duplicates).Count -eq 1)
New-Item -ItemType Directory -Path (Join-Path $h.Nt8 'log') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $h.Nt8 'log\log.20261009.00000.txt'), 'x')
Check 'only the localized log -> that one' ((Split-Path (Get-Nt8CurrentLog $h.Nt8) -Leaf) -eq 'log.20261009.00000.txt')
[IO.File]::WriteAllText((Join-Path $h.Nt8 'log\log.20261009.00000.en.txt'), 'x')
(Get-Item (Join-Path $h.Nt8 'log\log.20261009.00000.txt')).LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(5)
Check 'the English twin wins (a German NinjaTrader writes "Aktiviere NinjaScript Strategie")' ((Split-Path (Get-Nt8CurrentLog $h.Nt8) -Leaf) -eq 'log.20261009.00000.en.txt')

Complete-Test
