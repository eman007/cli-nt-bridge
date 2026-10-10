# Get-GuardStatus / Get-GuardBackupList / Get-GuardWatchInfo: what the control panel shows, read from
# fixture files only (heartbeats with set ages, state files, events, rows.json). No real NinjaTrader,
# no real tasks: processes, task states and the NT8 version are passed in.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
Import-Module (Join-Path $script:AppDir 'lib\Guard.Status.psm1') -Force -DisableNameChecking

$utc = [TimeZoneInfo]::Utc
$now = [DateTime]::SpecifyKind([DateTime]::UtcNow.Date.AddHours(15), 'Utc')   # 15:00 UTC today: times read predictably
$topic = 'guard-secret-topic-123'

function Set-Beat([string]$Path, [int]$AgeSeconds, [string]$Text = 'x') {
    $d = Split-Path $Path -Parent
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Text)
    (Get-Item $Path).LastWriteTimeUtc = $now.AddSeconds(-$AgeSeconds)
}

function Set-FeedGuardBeat([int]$AgeSeconds, [hashtable]$Over = @{}) {
    $h = [ordered]@{ component = 'AutoReconnect'; build = 'b'; version = '5.0.0'; utc = $now.ToString('o'); intervalSeconds = 10
                     rung = 0; autoRecover = $true; lastL1AgeSeconds = 2; watchdogStaleSeconds = 75; watchdogSubs = 2
                     watchdogResolved = 'ES 12-26,NQ 12-26'; watchdogConfigured = 'ES ##-##,NQ ##-##'; ladderConn = ''
                     savedEnabledCount = 0; events = $true; scheduledClosed = $false; nextClosure = 'Thanksgiving Thu 26 Nov: closed'
                     feedProviders = 'Sim Feed'; selfChecks = [ordered]@{ feedProvider = 'ok'; rollover = 'nt8'; reEnable = 'ok'; calendar = 'rules' } }
    foreach ($k in $Over.Keys) { if ($null -eq $Over[$k]) { $h.Remove($k) } else { $h[$k] = $Over[$k] } }
    Set-Beat (Join-Path $env:NT8_DIR 'AutoReconnect.heartbeat.json') $AgeSeconds ($h | ConvertTo-Json -Depth 4)
}

function Set-Registry([hashtable[]]$Rows, [int]$AgeMinutes = 5) {
    $doc = @{ version = 2; utc = $now.AddMinutes(-$AgeMinutes).ToString('o'); nt8StartUtc = $null; rows = @{} }
    foreach ($r in $Rows) {
        $doc.rows["$($r.Name)@NinjaTrader.NinjaScript.Strategies.$($r.Class)"] = @{
            strategies = @{ Id = 1; Name = $r.Name; Classname = "NinjaTrader.NinjaScript.Strategies.$($r.Class)" }
            state = $r.State; stateSinceUtc = $now.AddHours(-1).ToString('o'); lastSeenUtc = $now.AddMinutes(-$AgeMinutes).ToString('o')
            labels = @{ accounts = @('Sim101'); instruments = @('NQ 12-26') }
            charts = @($(if ($r.Tab) { @{ workspace = 'Main'; chart = 'Chart-1'; tab = 'Tab-1'; tabName = $r.Tab; slot = 'Strategy0'; barsIndex = 0 } }))
            idHistory = @(); strategy2account = @(); strategy2instrument = @()
        }
    }
    $p = Join-Path (Get-GuardPath backups) 'rows.json'
    [IO.File]::WriteAllText($p, ($doc | ConvertTo-Json -Depth 8))
}

function New-Healthy {
    # A machine where every piece is fine. Each scenario starts here and breaks one thing.
    $h = Use-TestHomes 'status'
    $cfg = Get-GuardDefaults
    $cfg.alerts.ntfyTopic = $topic
    Save-GuardConfig $cfg
    Set-Beat (Join-Path (Get-GuardPath state) 'relaunch.heartbeat') 20
    Set-Beat (Join-Path (Get-GuardPath state) 'overwatch.heartbeat') 30
    Set-Beat (Join-Path (Get-GuardPath secrets) 'nt8.cred') 86400
    Set-FeedGuardBeat 5
    Set-Beat (Join-Path $env:NT8_DIR 'NT8Bridge\result\heartbeat.json') 3 '{"server":"NT8BridgeServer","beat":"x"}'
    Set-Registry @(@{ Name = 'Alpha'; Class = 'DemoStrategy'; State = 'live'; Tab = 'NQ 1m' }, @{ Name = 'Beta'; Class = 'DemoStrategy'; State = 'live' })
    Write-GuardEvent -Component overwatch -Kind 'alert.sent' -Severity info -Text 'NT8 Guard test' -Data @{ ntfy = 'sent'; webhook = 'off' }
    return $h
}

$script:Procs = { , @([pscustomobject]@{ Id = 42; StartUtc = $now.AddMinutes(-30); Responding = $true; Title = 'Control Center' }) }
$script:Tasks = { param($name) 'Ready' }
$script:Version = [version]'8.1.8.3'

function Get-S([hashtable]$Extra = @{}) {
    $a = @{ NowUtc = $now; Processes = $script:Procs; TaskState = $script:Tasks; Nt8Version = $script:Version; TimeZone = $utc }
    foreach ($k in $Extra.Keys) { $a[$k] = $Extra[$k] }
    return Get-GuardStatus @a
}
function Card($s, [string]$Key) { return @($s.Cards | Where-Object { $_.Key -eq $Key })[0] }
function AllText($s) { return (@($s.Cards | ForEach-Object { $_.Headline, $_.Detail, $_.Remedy }) + @($s.Events | ForEach-Object { $_.Text }) + @($s.Banner)) -join "`n" }

'--- healthy'
New-Healthy | Out-Null
$s = Get-S
Check 'overall ALL GOOD' ($s.Overall.State -eq 'ok' -and $s.Overall.Text -eq 'ALL GOOD') "$($s.Overall.State) $($s.Overall.Text)"
Check 'eight cards in panel order' ((@($s.Cards.Key) -join ',') -eq 'nt8,feed,login,watchdogs,feedguard,link,backups,alerts') (@($s.Cards.Key) -join ',')
Check 'every card ok' (@($s.Cards | Where-Object { $_.State -ne 'ok' }).Count -eq 0) (@($s.Cards | Where-Object { $_.State -ne 'ok' } | ForEach-Object { "$($_.Key)=$($_.State) $($_.Headline)" }) -join '; ')
Check 'NT8 running with version' ((Card $s nt8).Headline -eq 'Running' -and (Card $s nt8).Detail -match '8\.1\.8\.3' -and (Card $s nt8).Detail -match '14:30') (Card $s nt8).Detail
Check 'feed live via its connection' ((Card $s feed).Headline -eq 'Live' -and (Card $s feed).Detail -match 'Sim Feed') "$((Card $s feed).Headline) / $((Card $s feed).Detail)"
Check 'backups count + chart count' ((Card $s backups).Headline -match '2 strategies saved' -and (Card $s backups).Detail -match '1 on a chart') "$((Card $s backups).Headline) / $((Card $s backups).Detail)"
Check 'no remedy when ok' (@($s.Cards | Where-Object { $_.Remedy }).Count -eq 0) ''
Check 'not paused' (-not $s.Pause.Paused) ''

'--- NinjaTrader not running'
New-Healthy | Out-Null
$none = { , @() }
$s = Get-S @{ Processes = $none }
Check 'down, Guard starts it: warn' ((Card $s nt8).State -eq 'warn' -and (Card $s nt8).Headline -eq 'Not running' -and (Card $s nt8).Detail -match 'within a minute') (Card $s nt8).Detail
Check 'add-on cards idle while NT8 is down' ((Card $s feed).State -eq 'idle' -and (Card $s feedguard).State -eq 'idle' -and (Card $s link).State -eq 'idle') "$((Card $s feed).State) $((Card $s feedguard).State) $((Card $s link).State)"
Check 'overall CHECK' ($s.Overall.State -eq 'warn' -and $s.Overall.Text -eq 'CHECK') $s.Overall.Text

[IO.File]::WriteAllText((Join-Path (Get-GuardPath state) 'start.backoff-until'), $now.AddMinutes(40).ToString('o'))
$s = Get-S @{ Processes = $none }
Check 'backoff: bad, says until when' ((Card $s nt8).State -eq 'bad' -and (Card $s nt8).Detail -match 'stopped until 15:40') (Card $s nt8).Detail
Check 'overall ATTENTION' ($s.Overall.State -eq 'bad' -and $s.Overall.Text -eq 'ATTENTION') $s.Overall.Text
[IO.File]::WriteAllText((Join-Path (Get-GuardPath state) 'start.backoff-until'), $now.AddMinutes(-1).ToString('o'))
$s = Get-S @{ Processes = $none }
Check 'an elapsed backoff is not reported' ((Card $s nt8).State -eq 'warn') (Card $s nt8).Detail
Check 'reading does not remove the elapsed backoff file' (Test-Path (Join-Path (Get-GuardPath state) 'start.backoff-until')) ''

[IO.File]::WriteAllText((Join-Path (Get-GuardPath state) 'start.loginhold'), $now.ToString('o'))
$s = Get-S @{ Processes = $none }
Check 'login hold: nt8 bad' ((Card $s nt8).State -eq 'bad' -and (Card $s nt8).Detail -match 'password was refused') (Card $s nt8).Detail
Check 'login hold: login card bad with a remedy' ((Card $s login).State -eq 'bad' -and (Card $s login).Remedy -match 'NinjaTrader login') (Card $s login).Remedy
Remove-Item (Join-Path (Get-GuardPath state) 'start.loginhold')

Set-Beat (Join-Path (Get-GuardPath state) 'relaunch.lockedsince') 60 $now.ToString('o')
$s = Get-S @{ Processes = $none }
Check 'locked screen: warn, starts after unlock' ((Card $s nt8).State -eq 'warn' -and (Card $s nt8).Detail -match 'unlock') (Card $s nt8).Detail
Remove-Item (Join-Path (Get-GuardPath state) 'relaunch.lockedsince')

$cfg = Get-GuardConfig; $cfg.protection.preset = 'watch'; Save-GuardConfig $cfg
$s = Get-S @{ Processes = $none }
Check 'Watch only: does not start it' ((Card $s nt8).State -eq 'warn' -and (Card $s nt8).Detail -match 'Watch only') (Card $s nt8).Detail

'--- watchdogs'
New-Healthy | Out-Null
Set-Beat (Join-Path (Get-GuardPath state) 'relaunch.heartbeat') 900
Set-Beat (Join-Path (Get-GuardPath state) 'overwatch.heartbeat') 1200
$asked = New-Object System.Collections.Generic.List[string]
$disabled = { param($name) $asked.Add($name); 'Disabled' }
$s = Get-S @{ Processes = $none; TaskState = $disabled }
Check 'watchdogs bad, names the disabled tasks' ((Card $s watchdogs).State -eq 'bad' -and (Card $s watchdogs).Detail -match 'disabled') (Card $s watchdogs).Detail
Check 'remedy says how' ((Card $s watchdogs).Remedy -match 'Task Scheduler|setup') (Card $s watchdogs).Remedy
Check 'NT8 down and nothing to start it: bad' ((Card $s nt8).State -eq 'bad' -and (Card $s nt8).Detail -match 'nothing') (Card $s nt8).Detail
Check 'task state asked only for stale ones' ((@($asked) -join ',') -eq 'NT8Guard-Relaunch,NT8Guard-Overwatch') (@($asked) -join ',')
$asked.Clear()
Set-Beat (Join-Path (Get-GuardPath state) 'relaunch.heartbeat') 20
Set-Beat (Join-Path (Get-GuardPath state) 'overwatch.heartbeat') 20
$s = Get-S @{ TaskState = $disabled }
Check 'fresh watchdogs: Task Scheduler is not asked' ($asked.Count -eq 0) (@($asked) -join ',')
Remove-Item (Join-Path (Get-GuardPath state) 'overwatch.heartbeat')
$s = Get-S @{ TaskState = { param($n) $null } }
Check 'one task missing: amber, says it is not installed' ((Card $s watchdogs).State -eq 'warn' -and (Card $s watchdogs).Detail -match 'overwatch: its task is not installed' -and (Card $s watchdogs).Remedy -match 'Register-GuardTasks') "$((Card $s watchdogs).State) $((Card $s watchdogs).Detail)"

'--- NinjaTrader running but unwell'
New-Healthy | Out-Null
$s = Get-S @{ Processes = { , @([pscustomobject]@{ Id = 42; StartUtc = $now.AddMinutes(-30); Responding = $false; Title = 'Control Center' }) } }
Check 'not responding: bad' ((Card $s nt8).State -eq 'bad' -and (Card $s nt8).Headline -eq 'Not responding') (Card $s nt8).Headline
$s = Get-S @{ Processes = { , @([pscustomobject]@{ Id = 42; StartUtc = $now.AddMinutes(-1); Responding = $true; Title = 'Welcome' }) } }
Check 'at its login window: warn' ((Card $s nt8).State -eq 'warn' -and (Card $s nt8).Headline -match 'login window') (Card $s nt8).Headline
$s = Get-S @{ Nt8Version = [version]'8.1.5.2' }
Check 'untested 8.1: warn' ((Card $s nt8).State -eq 'warn' -and (Card $s nt8).Detail -match 'not tested') (Card $s nt8).Detail
$s = Get-S @{ Nt8Version = [version]'8.0.28.0' }
Check '8.0: bad, unsupported' ((Card $s nt8).State -eq 'bad' -and (Card $s nt8).Detail -match 'not supported') (Card $s nt8).Detail
$s = Get-S @{ Nt8Version = $null }
Check 'version unknown: still reads' ((Card $s nt8).State -eq 'ok' -and (Card $s nt8).Headline -eq 'Running') (Card $s nt8).Detail

'--- feed guard add-on'
New-Healthy | Out-Null
Remove-Item (Join-Path $env:NT8_DIR 'AutoReconnect.heartbeat.json')
$s = Get-S
Check 'missing: not installed, with the import remedy' ((Card $s feedguard).State -eq 'bad' -and (Card $s feedguard).Headline -eq 'Not installed' -and (Card $s feedguard).Remedy -match 'NT8Guard-AddOns\.zip') (Card $s feedguard).Remedy
Check 'no feed reading without it' ((Card $s feed).State -eq 'idle') (Card $s feed).State
Set-FeedGuardBeat 3600
$s = Get-S
Check 'older than this NT8 start: not loaded' ((Card $s feedguard).State -eq 'bad' -and (Card $s feedguard).Headline -eq 'Not loaded') (Card $s feedguard).Headline
Set-FeedGuardBeat 600
$s = Get-S @{ Processes = { , @([pscustomobject]@{ Id = 42; StartUtc = $now.AddHours(-2); Responding = $true; Title = 'Control Center' }) } }
Check 'stale after loading: stopped reporting' ((Card $s feedguard).State -eq 'bad' -and (Card $s feedguard).Headline -eq 'Stopped reporting') (Card $s feedguard).Headline
Set-FeedGuardBeat 5 @{ version = $null }
$s = Get-S
Check 'no version: older than expected' ((Card $s feedguard).State -eq 'warn' -and (Card $s feedguard).Detail -match '5\.0\.0') (Card $s feedguard).Detail
Set-FeedGuardBeat 5 @{ watchdogResolved = ''; watchdogConfigured = 'MNQ' }
$s = Get-S
Check 'watch list resolves to nothing: warn' ((Card $s feedguard).State -eq 'warn' -and (Card $s feedguard).Detail -match 'MNQ' -and (Card $s feedguard).Remedy -match '##-##') "$((Card $s feedguard).Detail) / $((Card $s feedguard).Remedy)"
Set-Beat (Join-Path $env:NT8_DIR 'AutoReconnect.heartbeat.json') 5 '{"component":"AutoRec'
$s = Get-S
Check 'a heartbeat that cannot be read: amber, not a crash' ((Card $s feedguard).State -eq 'warn' -and (Card $s feedguard).Detail -match 'could not be read' -and (Card $s feed).State -eq 'idle') "$((Card $s feedguard).State) $((Card $s feedguard).Detail)"
Set-FeedGuardBeat 5 @{ selfChecks = [ordered]@{ feedProvider = 'unavailable'; rollover = 'rule'; reEnable = 'unavailable'; calendar = 'rules' } }
$s = Get-S
Check 'self-checks unavailable: warn, in words' ((Card $s feedguard).State -eq 'warn' -and (Card $s feedguard).Detail -match 'switch strategies back on') (Card $s feedguard).Detail

'--- market data'
New-Healthy | Out-Null
Set-FeedGuardBeat 5 @{ lastL1AgeSeconds = 200 }
$s = Get-S
Check 'silent past the threshold: bad' ((Card $s feed).State -eq 'bad' -and (Card $s feed).Headline -match 'Silent for 3 min') (Card $s feed).Headline
Set-FeedGuardBeat 5 @{ lastL1AgeSeconds = 200; rung = 2 }
$s = Get-S
Check 'recovering: warn, says the step' ((Card $s feed).State -eq 'warn' -and (Card $s feed).Headline -match 'Recovering' -and (Card $s feed).Detail -match 'step 2') (Card $s feed).Detail
Set-FeedGuardBeat 5 @{ lastL1AgeSeconds = 5000; scheduledClosed = $true }
$s = Get-S
Check 'scheduled close: idle, not a fault' ((Card $s feed).State -eq 'idle' -and (Card $s feed).Headline -match 'Market closed') (Card $s feed).Headline
Set-FeedGuardBeat 5 @{ lastL1AgeSeconds = $null }
$s = Get-S
Check 'no tick yet: warn' ((Card $s feed).State -eq 'warn' -and (Card $s feed).Headline -match 'No data yet') (Card $s feed).Headline

'--- an older feed guard (no market-closed flag): the clock decides'
$saved = $now
$d = $saved.Date; while ($d.DayOfWeek -ne 'Saturday') { $d = $d.AddDays(1) }
$now = [DateTime]::SpecifyKind($d.AddHours(15), 'Utc')
New-Healthy | Out-Null
Set-FeedGuardBeat 5 @{ version = $null; scheduledClosed = $null; nextClosure = $null; lastL1AgeSeconds = 90000 }
$s = Get-S
Check 'Saturday: market closed, not silent' ((Card $s feed).State -eq 'idle' -and (Card $s feed).Headline -match 'Market closed') (Card $s feed).Headline
$now = $now.AddDays(4)                                    # Wednesday 15:00 UTC = 10:00/11:00 in New York
New-Healthy | Out-Null
Set-FeedGuardBeat 5 @{ version = $null; scheduledClosed = $null; nextClosure = $null; lastL1AgeSeconds = 600; autoRecover = $false }
$s = Get-S
Check 'Wednesday: silent is silent' ((Card $s feed).State -eq 'bad') (Card $s feed).Headline
Check 'says recovery is off when the add-on says so' ((Card $s feed).Detail -match 'auto-recovery is off') (Card $s feed).Detail
$now = $saved

'--- NT8 link add-on'
New-Healthy | Out-Null
Remove-Item (Join-Path $env:NT8_DIR 'NT8Bridge\result\heartbeat.json')
$s = Get-S
Check 'link missing: not installed' ((Card $s link).State -eq 'bad' -and (Card $s link).Headline -eq 'Not installed') (Card $s link).Headline
Set-Beat (Join-Path $env:NT8_DIR 'NT8Bridge\result\heartbeat.json') 400
$s = Get-S @{ Processes = { , @([pscustomobject]@{ Id = 42; StartUtc = $now.AddHours(-2); Responding = $true; Title = 'Control Center' }) } }
Check 'link stale: stopped reporting' ((Card $s link).State -eq 'bad' -and (Card $s link).Headline -eq 'Stopped reporting') (Card $s link).Headline

'--- auto-login'
New-Healthy | Out-Null
Remove-Item (Join-Path (Get-GuardPath secrets) 'nt8.cred')
$s = Get-S
Check 'no saved password: warn with remedy' ((Card $s login).State -eq 'warn' -and (Card $s login).Remedy -match 'NinjaTrader login') (Card $s login).Remedy
$cfg = Get-GuardConfig; $cfg.login.helper = (Join-Path $env:WINDIR 'notepad.exe'); Save-GuardConfig $cfg
$s = Get-S
Check 'own login program: ok, named' ((Card $s login).State -eq 'ok' -and (Card $s login).Headline -match 'notepad\.exe') (Card $s login).Headline
$cfg.login.helper = 'builtin'; Save-GuardConfig $cfg
Set-Beat (Join-Path (Get-GuardPath secrets) 'nt8.cred') 60
Write-GuardEvent -Component login -Kind 'login.failed' -Severity alert -Text 'gave up' -Data @{ result = 'gave-up' }
$s = Get-S
Check 'last login failed: warn' ((Card $s login).State -eq 'warn' -and (Card $s login).Detail -match 'did not log in') (Card $s login).Detail

'--- strategy backups'
New-Healthy | Out-Null
Set-Registry @(@{ Name = 'Alpha'; Class = 'DemoStrategy'; State = 'missing' }, @{ Name = 'Beta'; Class = 'DemoStrategy'; State = 'live' })
$s = Get-S
Check 'missing: warn with count and advice' ((Card $s backups).State -eq 'warn' -and (Card $s backups).Headline -eq '1 strategy saved, 1 missing' -and (Card $s backups).Remedy -match 'automatically') "$((Card $s backups).Headline) / $((Card $s backups).Remedy)"
Set-Registry @(@{ Name = 'Beta'; Class = 'DemoStrategy'; State = 'live' }) -AgeMinutes 200
$s = Get-S
Check 'old save while NT8 runs: warn' ((Card $s backups).State -eq 'warn' -and (Card $s backups).Detail -match 'last saved 3 h 20 min ago') (Card $s backups).Detail
Remove-Item (Join-Path (Get-GuardPath backups) 'rows.json')
$s = Get-S
Check 'no backup yet: warn' ((Card $s backups).State -eq 'warn' -and (Card $s backups).Headline -match 'No backup yet') (Card $s backups).Headline
[IO.File]::WriteAllText((Join-Path (Get-GuardPath backups) 'rows.json'), '{ not json')
$s = Get-S
Check 'unreadable list: bad' ((Card $s backups).State -eq 'bad') (Card $s backups).Headline
Set-Registry @(@{ Name = 'Beta'; Class = 'DemoStrategy'; State = 'live' })
# Written at the test's clock ($now = 15:00 UTC today), not the real one: before 14:55 UTC the real clock is
# earlier than the save it must come after, and this check failed for 15 hours a day.
$ev = [ordered]@{ utc = $now.AddMinutes(-1).ToString('o'); component = 'overwatch'; kind = 'backups.save-failed'; severity = 'warn'; text = 'database copy unreadable'; data = @{} } | ConvertTo-Json -Compress
[IO.File]::AppendAllText((Join-Path (Get-GuardPath events) 'overwatch.jsonl'), $ev + "`n")
$s = Get-S
Check 'a failed save since the last good one: warn' ((Card $s backups).State -eq 'warn' -and (Card $s backups).Detail -match 'unreadable') (Card $s backups).Detail

'--- phone alerts'
New-Healthy | Out-Null
$cfg = Get-GuardConfig; $cfg.alerts.ntfyTopic = ''; Save-GuardConfig $cfg
$s = Get-S
Check 'no channel: warn' ((Card $s alerts).State -eq 'warn' -and (Card $s alerts).Headline -match 'Not set up') (Card $s alerts).Headline
$cfg.alerts.ntfyTopic = $topic; $cfg.alerts.webhookUrl = 'https://hooks.example/x'; Save-GuardConfig $cfg
$s = Get-S
Check 'both channels named' ((Card $s alerts).Headline -eq 'On: phone + webhook') (Card $s alerts).Headline
$cfg.alerts.webhookUrl = ''
$cfg.alerts.ntfyTopic = $topic; Save-GuardConfig $cfg
Write-GuardEvent -Component overwatch -Kind 'alert.sent' -Severity alert -Text 'NT8 frozen' -Data @{ ntfy = "failed: 404 for https://ntfy.sh/$topic"; webhook = 'off' }
$s = Get-S
Check 'last alert failed: bad' ((Card $s alerts).State -eq 'bad' -and (Card $s alerts).Headline -match 'did not reach') (Card $s alerts).Headline
Write-GuardEvent -Component overwatch -Kind 'nt8.fault' -Severity alert -Text "posted to https://ntfy.sh/$topic"
[IO.File]::WriteAllText((Join-Path (Get-GuardPath state) 'overwatch.configerror'), 'x')
$cfg = Get-GuardConfig; $cfg.loadError = "guard.json bad near $topic"
$s = Get-S @{ Config = $cfg }
Check 'the topic appears nowhere' ((AllText $s) -notmatch [regex]::Escape($topic)) (AllText $s)
Check 'a settings problem is a banner' (@($s.Banner | Where-Object { $_ -match 'guard.json bad' }).Count -eq 1) (@($s.Banner) -join ' | ')

'--- events'
New-Healthy | Out-Null
New-Item -ItemType Directory -Path (Join-Path $env:NT8_DIR 'NT8Guard') -Force | Out-Null
$fe = [ordered]@{ utc = $now.AddMinutes(-3).ToString('o'); component = 'feedguard'; kind = 'feed.reconnect'; severity = 'warn'; text = 'Reconnected Sim Feed'; data = @{} } | ConvertTo-Json -Compress
[IO.File]::WriteAllText((Join-Path $env:NT8_DIR 'NT8Guard\feedguard-events.jsonl'), $fe + "`n")
$s = Get-S
$fg = @($s.Events | Where-Object { $_.Kind -eq 'feed.reconnect' })[0]
Check 'feed guard events merged, labelled, local time' ($fg -and $fg.Source -eq 'Feed guard' -and $fg.Time -eq '14:57' -and $fg.Severity -eq 'warn') "$($fg.Source) $($fg.Time)"
Check 'newest first' ($s.Events[0].Utc -ge $s.Events[-1].Utc) ''
$old = [ordered]@{ utc = $now.AddDays(-2).ToString('o'); component = 'feedguard'; kind = 'feed.loaded'; severity = 'info'; text = 'loaded'; data = @{} } | ConvertTo-Json -Compress
[IO.File]::AppendAllText((Join-Path $env:NT8_DIR 'NT8Guard\feedguard-events.jsonl'), $old + "`n")
$s = Get-S
$ld = @($s.Events | Where-Object { $_.Kind -eq 'feed.loaded' })[0]
Check 'another day shows the weekday' ($ld.Time -match '^[A-Z][a-z]{2} \d\d:\d\d$') $ld.Time

'--- an event whose time cannot be read'
New-Healthy | Out-Null
$bad = @(
    [ordered]@{ utc = 'not-a-time'; component = 'login'; kind = 'login.failed'; severity = 'alert'; text = 'gave up'; data = @{ result = 'gave-up' } }
    [ordered]@{ utc = 'not-a-time'; component = 'overwatch'; kind = 'alert.sent'; severity = 'alert'; text = 'NT8 frozen'; data = @{ ntfy = 'failed: 500'; webhook = 'off' } }
) | ForEach-Object { $_ | ConvertTo-Json -Compress }
[IO.File]::AppendAllText((Join-Path (Get-GuardPath events) 'damaged.jsonl'), (($bad -join "`n") + "`n"))
$threw = ''
try { $s = Get-S } catch { $threw = $_.Exception.Message }
Check 'the status still reads' (-not $threw -and @($s.Cards).Count -eq 8) $threw
Check 'the cards still say what happened' ((Card $s login).Headline -eq 'Last login failed' -and (Card $s alerts).State -eq 'bad') "$((Card $s login).Headline) / $((Card $s alerts).State)"

'--- pause'
New-Healthy | Out-Null
Set-GuardPause -Minutes 30 -Reason 'test' | Out-Null
$s = Get-S @{ NowUtc = [DateTime]::UtcNow }
# The pause runs on the real clock: within 30 minutes of midnight (UTC, this test's zone) it ends tomorrow,
# which the pill rightly writes with the day ('Sun 00:06'). Failed 2026-10-10 at 23:36 UTC on the old pattern.
Check 'paused overrides the pill' ($s.Overall.State -eq 'paused' -and $s.Overall.Text -match '^PAUSED until ([A-Z][a-z]{2} )?\d\d:\d\d$') $s.Overall.Text
Check 'watchdogs card off while paused' ((Card $s watchdogs).State -eq 'off') (Card $s watchdogs).State
Check 'pause object carries reason' ($s.Pause.Paused -and $s.Pause.Reason -eq 'test') ''
Clear-GuardPause
[IO.File]::WriteAllText((Join-Path $env:NT8_DIR 'NT8Relaunch.disabled'), '')
$s = Get-S
Check 'an old switch file: banner, names the file' (@($s.Banner | Where-Object { $_ -match 'NT8Relaunch\.disabled' }).Count -eq 1) (@($s.Banner) -join ' | ')
Remove-Item (Join-Path $env:NT8_DIR 'NT8Relaunch.disabled')
$pp = Join-Path (Get-GuardPath state) 'pause.json'
[IO.File]::WriteAllText($pp, ([ordered]@{ setUtc = $now.AddHours(-2).ToString('o'); untilUtc = $now.AddHours(-1).ToString('o'); reason = '' } | ConvertTo-Json))
$s = Get-S
Check 'an expired pause reads as not paused' (-not $s.Pause.Paused -and $s.Overall.State -eq 'ok') $s.Overall.Text
Check 'and the file is left for the watchdogs' (Test-Path $pp) ''
Check 'no pause.expired event written by reading' (@(Read-GuardEvents -KindPattern '^pause\.expired$').Count -eq 0) ''

'--- backup list (Strategy backups tab)'
New-Healthy | Out-Null
Set-Registry @(@{ Name = 'Zed'; Class = 'DemoStrategy'; State = 'removed' }, @{ Name = 'Beta'; Class = 'DemoStrategy'; State = 'live'; Tab = 'ES 5m' },
               @{ Name = 'Alpha'; Class = 'Vendor.Other'; State = 'missing' })
$b = Get-GuardBackupList -NowUtc $now -TimeZone $utc
Check 'no error' (-not $b.Error) $b.Error
Check 'missing first, then on the list, then removed' ((@($b.Rows.Name) -join ',') -eq 'Alpha,Beta,Zed') (@($b.Rows.Name) -join ',')
$beta = @($b.Rows | Where-Object Name -eq 'Beta')[0]
Check 'row fields' ($beta.StateText -eq 'on the list' -and $beta.Account -eq 'Sim101' -and $beta.Instrument -eq 'NQ 12-26' -and $beta.Chart -eq 'Main - ES 5m' -and $beta.LastSaved -eq '14:55') "$($beta.StateText)|$($beta.Account)|$($beta.Instrument)|$($beta.Chart)|$($beta.LastSaved)"
Check 'strategy class shown short' ((@($b.Rows | Where-Object Name -eq 'Alpha')[0]).Strategy -eq 'Vendor.Other') ''
Check 'state texts' ((@($b.Rows | Where-Object Name -eq 'Zed')[0]).StateText -eq 'removed by you' -and (@($b.Rows | Where-Object Name -eq 'Alpha')[0]).StateText -eq 'missing') ''
[IO.File]::WriteAllText((Join-Path (Get-GuardPath backups) 'rows.json'), '{"version": 1, "rows": {}}')
$b = Get-GuardBackupList -NowUtc $now -TimeZone $utc
Check 'wrong version: error, no rows' ($b.Error -and @($b.Rows).Count -eq 0) $b.Error

'--- watch info (Watch list tab)'
New-Healthy | Out-Null
$w = Get-GuardWatchInfo -NowUtc $now
Check 'configured from guard.json' ((@($w.Configured) -join ',') -eq 'ES ##-##,NQ ##-##') (@($w.Configured) -join ',')
Check 'resolved contracts from the heartbeat' ((@($w.Resolved) -join ',') -eq 'ES 12-26,NQ 12-26') (@($w.Resolved) -join ',')
Check 'connection and next closure' ($w.Providers -eq 'Sim Feed' -and $w.NextClosure -match 'Thanksgiving') "$($w.Providers) / $($w.NextClosure)"
Remove-Item (Join-Path $env:NT8_DIR 'AutoReconnect.heartbeat.json')
$w = Get-GuardWatchInfo -NowUtc $now
Check 'no heartbeat: says why nothing is resolved' (@($w.Resolved).Count -eq 0 -and $w.Note -match 'feed guard') $w.Note

'--- expected add-on version'
$src = [IO.Path]::GetFullPath((Join-Path $script:AppDir '..\..\addon\AutoReconnect.cs'))
if (Test-Path $src) {
    $m = [regex]::Match([IO.File]::ReadAllText($src), 'AddOnVersion\s*=\s*"([^"]+)"')
    Check 'Guard expects the add-on version this repo builds' ($m.Success -and $m.Groups[1].Value -eq (Get-GuardExpectedFeedGuardVersion)) "$($m.Groups[1].Value) vs $(Get-GuardExpectedFeedGuardVersion)"
} else { '  (add-on source not beside this copy - version check skipped)' }

Complete-Test
