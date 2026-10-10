# The NT8 link (spec 3.1): a request is a JSON file dropped in NinjaTrader's NT8Bridge\trigger folder, the
# answer a file of the same name in NT8Bridge\result. Then the "you are flat" check every risky button
# runs first (spec 5): no position, no working order, no strategy running - and refuse when nobody can say.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
Import-Module (Join-Path $script:AppDir 'lib\Guard.Nt8Link.psm1') -Force -DisableNameChecking
$h = Use-TestHomes 'nt8link'
$now = [DateTime]::UtcNow
$trig = Join-Path $h.Nt8 'NT8Bridge\trigger'
$res = Join-Path $h.Nt8 'NT8Bridge\result'

'--- no NT8 link folder: no answer, and nothing created in NinjaTrader''s folder'
$r = Invoke-Nt8Link -Kind account -TimeoutSeconds 1
Check 'no answer' ($null -eq $r) ''
Check 'no folder made' (-not (Test-Path (Join-Path $h.Nt8 'NT8Bridge'))) ''

'--- a request and its answer, through a fake NT8 link in another runspace'
New-Item -ItemType Directory -Path $trig, $res -Force | Out-Null
$fake = [powershell]::Create()
[void]$fake.AddScript({
    param($trig, $res, $seconds)
    $until = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $until) {
        foreach ($f in [IO.Directory]::GetFiles($trig, '*.json')) {
            $text = [IO.File]::ReadAllText($f)
            [IO.File]::Delete($f)
            $q = $text | ConvertFrom-Json
            $body = [ordered]@{ id = $q.id; status = 'ok'; echo = $text }
            [IO.File]::WriteAllText([IO.Path]::Combine($res, "$($q.kind)_$($q.id).json"), ($body | ConvertTo-Json -Compress))
        }
        Start-Sleep -Milliseconds 100
    }
}).AddArgument($trig).AddArgument($res).AddArgument(20)
$fakeRun = $fake.BeginInvoke()
$r = Invoke-Nt8Link -Kind account -Extra @{ account = 'Sim101' } -TimeoutSeconds 10
Check 'answered' ($r -and $r.status -eq 'ok') ($r | ConvertTo-Json -Compress)
$sent = $(if ($r) { $r.echo | ConvertFrom-Json } else { $null })
Check 'the request carries its kind, an id and the extra fields' ($sent.kind -eq 'account' -and $sent.id -match '^[0-9a-f]{32}$' -and $sent.account -eq 'Sim101') $r.echo
Check 'the answer file is taken away once read' (@(Get-ChildItem $res -Filter 'account_*.json').Count -eq 0) ''
$fake.Stop(); $fake.Dispose()

'--- no answer within the timeout: $null, and the request is taken back'
$t0 = [DateTime]::UtcNow
$r = Invoke-Nt8Link -Kind strategies -TimeoutSeconds 1
Check 'no answer' ($null -eq $r) ''
Check 'bounded' (([DateTime]::UtcNow - $t0).TotalSeconds -lt 4) ''
Check 'no request left to fire late against a later NinjaTrader' (@(Get-ChildItem $trig).Count -eq 0) (@(Get-ChildItem $trig).Name -join ',')

'--- the flat check, with a fake NinjaTrader and a fake NT8 link'
function Set-Beat([string]$Path, [int]$AgeSeconds) {
    $d = Split-Path $Path -Parent
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    [IO.File]::WriteAllText($Path, '{"server":"NT8BridgeServer"}')
    (Get-Item $Path).LastWriteTimeUtc = $now.AddSeconds(-$AgeSeconds)
}
$beat = Join-Path $res 'heartbeat.json'
$running = { , @([pscustomobject]@{ Id = 7; StartUtc = $now.AddHours(-2); Title = 'Control Center' }) }
$script:calls = @()
$script:acct = $null; $script:strat = $null
$link = { param($kind) $script:calls += $kind; if ($kind -eq 'account') { $script:acct } else { $script:strat } }
function Flat { Get-Nt8FlatState -Processes $running -Link $link -NowUtc $now }
function Acct($positions = @(), $orders = @()) {
    [pscustomobject]@{ status = 'ok'; accounts = @([pscustomobject]@{ name = 'Sim101'; positions = @($positions); workingOrders = @($orders) }
                                                   [pscustomobject]@{ name = 'Playback101'; positions = @(); workingOrders = @() }) }
}
function Strat($rows = @()) { [pscustomobject]@{ status = 'ok'; gridResolved = $true; strategies = @($rows) } }
$off = [pscustomobject]@{ name = 'Alpha'; enabled = $false; account = 'Sim101'; instrument = 'NQ 12-26' }

$s = Get-Nt8FlatState -Processes { , @() } -Link $link -NowUtc $now
Check 'NinjaTrader not running: closed, safe, nothing asked' ($s.State -eq 'closed' -and $s.Safe -and $script:calls.Count -eq 0) "$($s.State) $($script:calls -join ',')"
$s = Get-Nt8FlatState -Processes { , @([pscustomobject]@{ Id = 7; StartUtc = $now; Title = 'Welcome' }) } -Link $link -NowUtc $now
Check 'at its login window: nothing loaded, safe' ($s.State -eq 'login' -and $s.Safe -and $script:calls.Count -eq 0) $s.State

$s = Flat
Check 'no NT8 link heartbeat: unknown, refused, nothing asked' ($s.State -eq 'unknown' -and -not $s.Safe -and $s.Text -match "can't confirm you're flat" -and $s.Text -match 'not installed' -and $script:calls.Count -eq 0) $s.Text
Set-Beat $beat 600
$s = Flat
Check 'a stale heartbeat: unknown' ($s.State -eq 'unknown' -and $s.Text -match 'not answered for 10 min' -and $script:calls.Count -eq 0) $s.Text
$s = Get-Nt8FlatState -Processes { , @([pscustomobject]@{ Id = 7; StartUtc = $now.AddSeconds(-60); Title = 'Control Center' }) } -Link $link -NowUtc $now
Check 'a heartbeat older than this NinjaTrader: not loaded' ($s.State -eq 'unknown' -and $s.Text -match 'not loaded') $s.Text
Set-Beat $beat 2
$script:acct = $null
$s = Flat
Check 'no answer: unknown' ($s.State -eq 'unknown' -and $s.Text -match 'did not answer') $s.Text
$script:acct = [pscustomobject]@{ status = 'error'; errors = @([pscustomobject]@{ message = 'boom in the add-on' }) }
$s = Flat
Check 'an error answer: unknown, with the reason' ($s.State -eq 'unknown' -and $s.Text -match 'boom in the add-on') $s.Text
$script:acct = Acct; $script:strat = [pscustomobject]@{ status = 'error'; gridResolved = $false; errors = @([pscustomobject]@{ message = 'could not reach the Control Center StrategiesGrid' }) }
$s = Flat
Check 'the Strategies list unreadable: unknown' ($s.State -eq 'unknown' -and $s.Text -match 'StrategiesGrid') $s.Text

$script:strat = Strat @($off)
$script:calls = @()
$s = Flat
Check 'nothing open: flat, safe' ($s.State -eq 'flat' -and $s.Safe -and @($s.Reasons).Count -eq 0 -and $s.Text -match '^Flat') "$($s.State): $($s.Text)"
Check 'it asked for the accounts and the Strategies list' (($script:calls -join ',') -eq 'account,strategies') ($script:calls -join ',')

$pos = [pscustomobject]@{ instrument = 'NQ 12-26'; marketPosition = 'Long'; quantity = 2 }
$ord = [pscustomobject]@{ instrument = 'ES 12-26'; state = 'Working'; name = 'Stop1' }
$script:acct = Acct @($pos) @()
$s = Flat
Check 'a position: busy, named' ($s.State -eq 'busy' -and -not $s.Safe -and $s.Text -match 'Sim101: Long 2 NQ 12-26') $s.Text
$script:acct = Acct @() @($ord, $ord)
$s = Flat
Check 'working orders: busy, counted' ($s.State -eq 'busy' -and $s.Text -match 'Sim101: 2 working orders \(ES 12-26\)') $s.Text
$script:acct = Acct
$script:strat = Strat @($off, [pscustomobject]@{ name = 'Beta'; enabled = $true; account = 'Sim101'; instrument = 'NQ 12-26' })
$s = Flat
Check 'a running strategy: busy, named' ($s.State -eq 'busy' -and $s.Text -match 'strategy Beta is running \(Sim101, NQ 12-26\)') $s.Text
$script:acct = Acct @($pos) @($ord)
$s = Flat
Check 'everything open is listed' (@($s.Reasons).Count -eq 3) (@($s.Reasons) -join ' | ')
$script:acct = Acct @([pscustomobject]@{ instrument = 'NQ 12-26'; marketPosition = 'Flat'; quantity = 0 }) @()
$script:strat = Strat @($off)
$s = Flat
Check 'a position row reading Flat is not exposure' ($s.State -eq 'flat') $s.Text

'--- answers that say ok but cannot be true (final review I2)'
$script:acct = [pscustomobject]@{ status = 'ok'; accounts = @() }
$s = Flat
Check 'no account at all: unknown (a logged-in NinjaTrader always has one)' ($s.State -eq 'unknown' -and $s.Text -match 'no account') $s.Text
$script:acct = Acct
$script:strat = Strat @([pscustomobject]@{ name = 'Gamma'; enabled = $false; state = 'Realtime'; account = 'Sim101'; instrument = 'NQ 12-26' })
$s = Flat
Check 'a row whose strategy runs counts, ticked or not (the add-on: believe state)' ($s.State -eq 'busy' -and $s.Text -match 'strategy Gamma is running') $s.Text
$script:strat = Strat @([pscustomobject]@{ name = 'Delta'; enabled = $false; state = 'SetDefaults'; account = 'Sim101'; instrument = 'NQ 12-26' })
$s = Flat
Check 'a switched-off row in its set-up state is not running' ($s.State -eq 'flat') $s.Text
$rows = @{ 'Alpha@NinjaTrader.NinjaScript.Strategies.DemoStrategy' = @{ strategies = @{ Id = 1; Name = 'Alpha'; Classname = 'NinjaTrader.NinjaScript.Strategies.DemoStrategy' }; state = 'live'; charts = @() } }
[IO.File]::WriteAllText((Join-Path (Get-GuardPath backups) 'rows.json'), (@{ version = 2; utc = $now.ToString('o'); rows = $rows } | ConvertTo-Json -Depth 8))
$script:strat = Strat @()
$s = Flat
Check 'an empty Strategies list while Guard''s backups say strategies exist: unknown' ($s.State -eq 'unknown' -and $s.Text -match 'Strategies list came back empty') $s.Text
Remove-Item (Join-Path (Get-GuardPath backups) 'rows.json')
$s = Flat
Check 'an empty list with no backups to contradict it: flat' ($s.State -eq 'flat') $s.Text
$script:strat = Strat @($off)

'--- more than one NinjaTrader (final review I3)'
$two = { , @([pscustomobject]@{ Id = 7; StartUtc = $now.AddHours(-2); Title = 'Welcome' }, [pscustomobject]@{ Id = 8; StartUtc = $now.AddHours(-1); Title = 'Control Center' }) }
$script:calls = @()
$s = Get-Nt8FlatState -Processes $two -Link $link -NowUtc $now
Check 'one at Welcome and one logged in: asks the NT8 link, not "login"' ($s.State -eq 'flat' -and $script:calls.Count -eq 2) "$($s.State) $($script:calls -join ',')"
$young = { , @([pscustomobject]@{ Id = 7; StartUtc = $now.AddHours(-2); Title = 'Control Center' }, [pscustomobject]@{ Id = 8; StartUtc = $now.AddSeconds(-30); Title = 'Control Center' }) }
Set-Beat $beat 400
$s = Get-Nt8FlatState -Processes $young -Link $link -NowUtc $now
Check 'a heartbeat older than the NEWEST NinjaTrader: not loaded' ($s.State -eq 'unknown' -and $s.Text -match 'not loaded') $s.Text
Set-Beat $beat 2

'--- the real NT8 link protocol end to end: a fake link answers both questions'
$fake = [powershell]::Create()
[void]$fake.AddScript({
    param($trig, $res, $seconds)
    $until = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $until) {
        foreach ($f in [IO.Directory]::GetFiles($trig, '*.json')) {
            $q = [IO.File]::ReadAllText($f) | ConvertFrom-Json
            [IO.File]::Delete($f)
            $body = $(if ($q.kind -eq 'account') { '{"status":"ok","accounts":[{"name":"Sim101","positions":[],"workingOrders":[]}]}' }
                      else { '{"status":"ok","gridResolved":true,"strategies":[{"name":"Alpha","enabled":false,"account":"Sim101","instrument":"NQ 12-26"}]}' })
            [IO.File]::WriteAllText([IO.Path]::Combine($res, "$($q.kind)_$($q.id).json"), $body)
        }
        Start-Sleep -Milliseconds 100
    }
}).AddArgument($trig).AddArgument($res).AddArgument(20)
$fakeRun = $fake.BeginInvoke()
$s = Get-Nt8FlatState -Processes $running -NowUtc $now
Check 'flat, read through the files' ($s.State -eq 'flat') $s.Text
$fake.Stop(); $fake.Dispose()

Complete-Test
