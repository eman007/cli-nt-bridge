. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes config
$path = Join-Path $h.Home 'guard.json'

'=== no file: complete defaults, preset Recover NT8'
$c = Get-GuardConfig
$p = Get-GuardProtection $c
Check 'schemaVersion 1' ($c.schemaVersion -eq 1)
Check 'preset recover' ($p.preset -eq 'recover')
Check 'recover: relaunch + restart + reconnect on' ($p.relaunch -and $p.restartWhenFrozen -and $p.reconnect -and $p.recoverSilentFeed)
Check 'recover: put back + charts on' ($p.restoreStrategies -and $p.reattachCharts)
Check 'recover: re-enable and flatten are ALERT ONLY' (-not $p.reenableStrategies -and -not $p.flattenUnprotected)
Check 'crash dumps off by default' (-not $p.crashDumps)
Check 'watch list default ES/NQ current contract' ((@($c.watch.instruments) -join ',') -eq 'ES ##-##,NQ ##-##')
Check 'defaults are valid' ((Test-GuardConfig $c).Count -eq 0) ((Test-GuardConfig $c) -join '; ')

'=== partial file merges onto the defaults'
[IO.File]::WriteAllText($path, '{ "alerts": { "ntfyTopic": "abc_123" }, "protection": { "preset": "handsoff" } }')
$c = Get-GuardConfig
Check 'set value kept' ($c.alerts.ntfyTopic -eq 'abc_123')
Check 'sibling default kept' ($c.alerts.ntfyServer -eq 'https://ntfy.sh')
Check 'untouched section kept' ($c.relaunch.stuckMinutes -eq 6)
Check 'handsoff re-enables and flattens' ((Get-GuardProtection $c).reenableStrategies -and (Get-GuardProtection $c).flattenUnprotected)

'=== a toggle that departs from its preset makes it Custom'
[IO.File]::WriteAllText($path, '{ "protection": { "preset": "recover", "reenableStrategies": true } }')
$p = Get-GuardProtection (Get-GuardConfig)
Check 'override applied' $p.reenableStrategies
Check "preset reads 'custom'" ($p.preset -eq 'custom')
[IO.File]::WriteAllText($path, '{ "protection": { "preset": "watch", "relaunch": false } }')
Check 'a toggle equal to its preset stays on the preset' ((Get-GuardProtection (Get-GuardConfig)).preset -eq 'watch')
Check 'watch: nothing acts' (-not (Get-GuardProtection (Get-GuardConfig)).relaunch)

'=== unreadable file and no last good copy: defaults plus a reason, never a crash'
Remove-Item "$path.lastgood" -Force -ErrorAction SilentlyContinue
[IO.File]::WriteAllText($path, '{ "protection": ')
$c = Get-GuardConfig
Check 'defaults used' ((Get-GuardProtection $c).preset -eq 'recover')
Check 'loadError says why' ($c.loadError -match 'could not be read')
[IO.File]::WriteAllText($path, '')
Check 'empty file = defaults, and says it is empty' ((Get-GuardConfig).loadError -match 'empty')

'=== a broken file keeps the user''s last good settings (never silently switches preset)'
Remove-Item $path -Force
$good = Get-GuardDefaults
$good.protection.preset = 'watch'
$good.alerts.ntfyTopic = 'my_topic'
Save-GuardConfig $good
Check 'save keeps a last-good copy' (Test-Path "$path.lastgood")
[IO.File]::WriteAllText($path, '{ "protection": ')
$c = Get-GuardConfig
Check 'truncated -> last good preset kept' ((Get-GuardProtection $c).preset -eq 'watch') ((Get-GuardProtection $c).preset)
Check 'truncated -> last good topic kept' ($c.alerts.ntfyTopic -eq 'my_topic')
Check 'truncated -> says it used the last good copy' ($c.loadError -match 'last good')
[IO.File]::WriteAllText($path, '')
$c = Get-GuardConfig
Check 'empty -> last good preset kept' ((Get-GuardProtection $c).preset -eq 'watch')

'=== a file that parses but has the wrong shape: bad pieces fall back, the rest is kept, it says which'
[IO.File]::WriteAllText($path, '{ "protection": "handsoff", "relaunch": null, "alerts": { "ntfyTopic": "my_topic" } }')
$c = Get-GuardConfig
Check 'protection as a string -> last good preset' ((Get-GuardProtection $c).preset -eq 'watch')
Check 'relaunch null -> numbers usable' ($c.relaunch.maxActions -eq 4 -and $c.relaunch.backoffMinutes -eq 60)
Check 'names the bad settings' ($c.loadError -match 'protection' -and $c.loadError -match 'relaunch') $c.loadError
Check 'Get-GuardProtection does not throw' ([bool](Get-GuardProtection $c))
[IO.File]::WriteAllText($path, '{ "protection": { "preset": "watch" }, "relaunch": { "maxActions": 0, "stuckMinutes": "six", "graceMinutes": 1.5 } }')
$c = Get-GuardConfig
Check 'zero maxActions -> usable value' ($c.relaunch.maxActions -eq 4)
Check 'text where a number belongs -> usable value' ($c.relaunch.stuckMinutes -eq 6)
Check 'a decimal number is a number' ($c.relaunch.graceMinutes -eq 1.5)
Check 'only the bad ones are named' ($c.loadError -match 'maxActions' -and $c.loadError -match 'stuckMinutes' -and $c.loadError -notmatch 'graceMinutes') $c.loadError
[IO.File]::WriteAllText($path, '{ "protection": { "preset": "yolo", "relaunch": "false" } }')
$c = Get-GuardConfig
$p = Get-GuardProtection $c
Check 'unknown preset -> last good preset, named' ($p.preset -eq 'watch' -and $c.loadError -match 'preset') $c.loadError
Check 'toggle as text -> ignored, named (never "false" read as true)' (-not $p.relaunch -and $c.loadError -match 'protection.relaunch') $c.loadError
[IO.File]::WriteAllText($path, '{ "relaunch": { "graceMinutes": 1.5 } }')
$c = Get-GuardConfig
Check 'decimal: no problem reported' (-not $c.loadError) $c.loadError
$saved = $true; try { Save-GuardConfig $c } catch { $saved = $false }
Check 'decimal: saves' $saved

'=== a clean hand edit becomes the new last good copy'
[IO.File]::WriteAllText($path, '{ "protection": { "preset": "handsoff" } }')
$null = Get-GuardConfig
[IO.File]::WriteAllText($path, '{ "protection": ')
Check 'truncation after a hand edit keeps the hand edit' ((Get-GuardProtection (Get-GuardConfig)).preset -eq 'handsoff')
Remove-Item "$path.lastgood" -Force
Check 'no last good copy -> defaults, with a reason' ((Get-GuardProtection (Get-GuardConfig)).preset -eq 'recover' -and (Get-GuardConfig).loadError -match 'default')

'=== a settings problem is logged, evented and alerted once per change, not every minute'
$c = Get-GuardConfig
Publish-GuardConfigProblem -Config $c -Component relaunch
Publish-GuardConfigProblem -Config $c -Component relaunch
Check 'evented once' (@(Read-GuardEvents -KindPattern '^guard\.config$').Count -eq 1)
[IO.File]::WriteAllText($path, '{}')
Publish-GuardConfigProblem -Config (Get-GuardConfig) -Component relaunch
Check 'fixed -> said so' ((Read-AllText (Join-Path $h.Home 'logs\relaunch.log')) -match 'readable again')

'=== validation'
$c = Get-GuardDefaults
$c.alerts.ntfyTopic = 'has spaces!'
$c.watch.instruments = @()
$c.watch.staleSeconds = 10
$c.login.helper = 'C:\nope\NTLogin.exe'
$c.relaunch.maxActions = 0
$c.protection.preset = 'yolo'
$probs = Test-GuardConfig $c
foreach ($want in 'ntfyTopic', 'instruments', 'staleSeconds', 'login.helper', 'relaunch.maxActions', 'preset') {
    Check "flags $want" ([bool]($probs -match [regex]::Escape($want))) ($probs -join '; ')
}
$bad = $false
try { Save-GuardConfig $c } catch { $bad = $_.Exception.Message -match 'not saved' }
Check 'an invalid object is refused, not saved' $bad

'=== save round-trips, atomically, without loadError'
Remove-Item $path -Force
$c = Get-GuardDefaults
$c.alerts.ntfyTopic = 'guard_test_topic'
$c.protection.reenableStrategies = $true
$c.loadError = 'should never be written'
Save-GuardConfig $c
$back = Get-GuardConfig
Check 'value survives' ($back.alerts.ntfyTopic -eq 'guard_test_topic')
Check 'toggle survives' ((Get-GuardProtection $back).reenableStrategies)
Check 'array survives' ((@($back.watch.instruments) -join ',') -eq 'ES ##-##,NQ ##-##')
Check 'no temp file left' (-not (Test-Path "$path.tmp"))
Check 'loadError not written' ((Read-AllText $path) -notmatch 'loadError')
Check 'written without a BOM (plain JSON)' ([IO.File]::ReadAllBytes($path)[0] -eq [byte][char]'{')

'=== the overwatch''s settings: complete defaults; on/off settings must be true or false'
$c = Get-GuardDefaults
Check 'overwatch section present' ($c.overwatch.startupGraceSeconds -eq 300 -and $c.overwatch.restoreAfterAnyRestart -eq $true -and $c.overwatch.relauncherStaleSeconds -eq 180)
Check 'defaults still valid' ((Test-GuardConfig $c).Count -eq 0) ((Test-GuardConfig $c) -join '; ')
$c.overwatch.dryRunRemedy = 'yes'
$c.overwatch.dumpKeep = 0
$probs = Test-GuardConfig $c
Check 'text where true/false belongs is flagged' ([bool]($probs -match 'overwatch.dryRunRemedy must be true or false')) ($probs -join '; ')
Check 'zero count is flagged' ([bool]($probs -match 'overwatch.dumpKeep must be a positive number')) ($probs -join '; ')
[IO.File]::WriteAllText($path, '{ "overwatch": { "dryRunRemedy": "yes", "dumpKeep": 0 } }')
$c = Get-GuardConfig
Check 'unusable overwatch values fall back' ($c.overwatch.dryRunRemedy -eq $false -and $c.overwatch.dumpKeep -eq 2)
Check 'and are named' ($c.loadError -match 'overwatch.dryRunRemedy' -and $c.loadError -match 'overwatch.dumpKeep') $c.loadError

Complete-Test
