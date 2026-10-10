. (Join-Path $PSScriptRoot 'TestKit.ps1')
$h = Use-TestHomes relaunch
. (Join-Path $script:AppDir 'Guard-Relaunch.ps1') -ImportOnly
$ErrorActionPreference = 'Stop'

# A scripted world; Start() records a launch and emits the same event Invoke-Nt8Start does.
function New-World {
    param($Locked = $false, $Nt8 = $null, [bool]$InFlight = $false, $Session = @{ Id = 2; State = 'Active' },
          [string]$Preset = 'recover', $LockThrows = $false)
    $script:w = @{ Locked = $Locked; Nt8 = $Nt8; InFlight = $InFlight; Session = $Session; Kills = @(); Starts = 0; Tscon = @()
                   T = [DateTime]::UtcNow; LockThrows = $LockThrows }
    $script:cfg = Get-GuardDefaults
    $script:cfg.protection.preset = $Preset
}
$ops = @{
    IsLocked      = { if ($script:w.LockThrows) { throw 'probe exploded' }; $script:w.Locked }
    Nt8           = { $script:w.Nt8 }
    Kill          = { param($id) $script:w.Kills += $id; $script:w.Nt8 = $null }
    LoginInFlight = { $script:w.InFlight }
    Session       = { $script:w.Session }
    Tscon         = { param($id) $script:w.Tscon += $id }
    Start         = { $script:w.Starts++
                      [IO.File]::WriteAllText((Join-Path (Get-GuardPath state) 'nt8.lastlaunch'), $script:w.T.ToString('o'))
                      Write-GuardEvent -Component relaunch -Kind 'nt8.launch' -Severity warn -Text 'Starting NinjaTrader (test)'
                      [pscustomobject]@{ Result = 'launched' } }
    Now           = { $script:w.T }
}
function Cycle { Invoke-RelaunchCycle -Config $script:cfg -Ops $ops }
function Clear-State { Get-ChildItem (Get-GuardPath state) -File | Remove-Item -Force; Get-ChildItem (Get-GuardPath events) -File | Remove-Item -Force }
# (New-World starts the simulated clock at UtcNow, so StartUtc lines up with it)
function Up([string]$Title = 'Control Center', [double]$Min = 30) { @{ Id = 77; Title = $Title; UpMinutes = $Min; StartUtc = [DateTime]::UtcNow.AddMinutes(-$Min) } }

'=== healthy: nothing to do, heartbeat written'
Clear-State; New-World -Nt8 (Up)
Check 'healthy' ((Cycle) -eq 'healthy')
Check 'heartbeat' (Test-Path (Join-Path (Get-GuardPath state) 'relaunch.heartbeat'))
Check 'logged-in NinjaTrader is stamped (lifts a refused-password hold)' (Test-Path (Join-Path (Get-GuardPath state) 'nt8.healthy'))

'=== the heartbeat is written before anything that can fail'
Clear-State; New-World
$script:cfg.protection = 'not a section'
try { $null = Cycle } catch { }
Check 'heartbeat even when the cycle fails' (Test-Path (Join-Path (Get-GuardPath state) 'relaunch.heartbeat'))

'=== down: launches through the start routine'
Clear-State; New-World
Check 'launched' ((Cycle) -eq 'launched' -and $script:w.Starts -eq 1)
Check 'then grace on the next minute' ((Cycle) -eq 'grace' -and $script:w.Starts -eq 1)

'=== paused: nothing at all (but the heartbeat still proves it ran)'
Clear-State; New-World
$null = Set-GuardPause -Minutes 10
Check 'paused' ((Cycle) -eq 'paused' -and $script:w.Starts -eq 0)
Check 'heartbeat even while paused' (Test-Path (Join-Path (Get-GuardPath state) 'relaunch.heartbeat'))
Clear-GuardPause

'=== locked screen: hold, log once, start within a minute of the unlock'
Clear-State; New-World -Locked $true
Check 'held' ((Cycle) -eq 'held-locked' -and $script:w.Starts -eq 0)
$n = (Read-AllText (Join-Path $h.Home 'logs\relaunch.log')).Split("`n").Count
$null = Cycle
Check 'no log spam while locked' ((Read-AllText (Join-Path $h.Home 'logs\relaunch.log')).Split("`n").Count -eq $n)
$script:w.Locked = $false
Check 'unlock -> launch' ((Cycle) -eq 'launched')
Check 'unlock logged' ((Read-AllText (Join-Path $h.Home 'logs\relaunch.log')) -match 'UNLOCKED')

'=== the lock probe failing must not keep NinjaTrader down (fail open)'
Clear-State; New-World -LockThrows $true
Check 'proceeds to launch' ((Cycle) -eq 'launched')

'=== stuck at the login window'
Clear-State; New-World -Nt8 (Up 'Welcome' 2)
Check 'young at Welcome -> give it time' ((Cycle) -eq 'starting' -and $script:w.Kills.Count -eq 0)
$script:w.Nt8 = Up 'Welcome' 7
$r = Cycle
Check 'past stuckMinutes -> stopped' ($script:w.Kills -contains 77)
Check 'stuck evented' (@(Read-GuardEvents -KindPattern '^nt8\.stuck$').Count -eq 1)
Check 'then started again' ($r -eq 'launched')

'=== the login helper died early: no need to wait the full stuckMinutes'
Clear-State; New-World -Nt8 (Up 'Welcome' 2)
[IO.File]::WriteAllText((Join-Path (Get-GuardPath state) 'nt8.lastlaunch'), $script:w.T.AddSeconds(-120).ToString('o'))
$null = Cycle
Check 'killed at 2 min (login dead)' ($script:w.Kills -contains 77)

'=== a NinjaTrader the user opened by hand is never killed early at its login window'
Clear-State; New-World -Nt8 (Up 'Welcome' 2)
[IO.File]::WriteAllText((Join-Path (Get-GuardPath state) 'nt8.lastlaunch'), $script:w.T.AddDays(-2).ToString('o'))
Check 'hand-started, 2 min at Welcome -> left alone' ((Cycle) -eq 'starting' -and $script:w.Kills.Count -eq 0)
$script:w.Nt8 = @{ Id = 77; Title = 'Welcome'; UpMinutes = 2; StartUtc = $null }
Check 'start time unknown -> left alone too' ((Cycle) -eq 'starting' -and $script:w.Kills.Count -eq 0)

'=== two refused logins: the relauncher stops starting NinjaTrader (wrong password is not hammered)'
Clear-State; New-World
1..2 | ForEach-Object { Write-GuardEvent -Component login -Kind 'login.failed' -Severity alert -Text 'gave up' -Data @{ result = 'gave-up' } }
Check 'login-refused, no start' ((Cycle) -eq 'login-refused' -and $script:w.Starts -eq 0)

'=== a login in flight is left alone'
Clear-State; New-World -InFlight $true
Check 'in-flight' ((Cycle) -eq 'in-flight' -and $script:w.Starts -eq 0)

'=== backoff: 4 starts/stops in 30 min -> stop for 60 min and alert'
Clear-State; New-World
1..4 | ForEach-Object { Write-GuardEvent -Component relaunch -Kind 'nt8.launch' -Severity warn -Text 'earlier' }
Check 'backoff' ((Cycle) -eq 'backoff' -and $script:w.Starts -eq 0)
Check 'alerted' ([bool](Read-GuardEvents -KindPattern '^alert\.sent$' | Where-Object { $_.text -like '*backoff*' }))
Check 'next minute: still backing off' ((Cycle) -eq 'backing-off')
$script:w.T = $script:w.T.AddMinutes(61)
Get-ChildItem (Get-GuardPath events) -File | Remove-Item -Force
Check 'after the window: one probe launch' ((Cycle) -eq 'launched')
Clear-State; New-World -Nt8 (Up)
[IO.File]::WriteAllText((Join-Path (Get-GuardPath state) 'start.backoff-until'), $script:w.T.AddMinutes(-1).ToString('o'))
Check 'healthy NinjaTrader clears an elapsed backoff' ((Cycle) -eq 'healthy' -and -not (Test-Path (Join-Path (Get-GuardPath state) 'start.backoff-until')))

'=== relaunch switched off (Watch only): alert once per outage, never act'
Clear-State; New-World -Preset 'watch'
Check 'down -> alert, no launch' ((Cycle) -eq 'down-relaunch-off' -and $script:w.Starts -eq 0)
$null = Cycle
Check 'alerted ONCE' (@(Read-GuardEvents -KindPattern '^alert\.sent$').Count -eq 1)
$script:w.Nt8 = Up
$null = Cycle
$script:w.Nt8 = $null
$null = Cycle
Check 'a new outage alerts again' (@(Read-GuardEvents -KindPattern '^alert\.sent$').Count -eq 2)
Clear-State; New-World -Preset 'watch' -Nt8 (Up 'Welcome' 9)
Check 'stuck + relaunch off -> alert, no kill' ((Cycle) -eq 'stuck-relaunch-off' -and $script:w.Kills.Count -eq 0)

'=== a disconnected remote session is attached to the console first'
Clear-State; New-World -Session @{ Id = 3; State = 'Disc' }
$null = Cycle
Check 'tscon for session 3' ($script:w.Tscon -contains 3)
Clear-State; New-World -Session @{ Id = 0; State = 'Disc' }
$null = Cycle
Check 'never session 0' ($script:w.Tscon.Count -eq 0)

Complete-Test
