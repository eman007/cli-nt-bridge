. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes start

'=== command-line quoting (paths with spaces, quotes, trailing backslashes)'
Check 'plain' ((ConvertTo-GuardArg 'abc') -eq 'abc')
Check 'space' ((ConvertTo-GuardArg 'C:\Users\Jo Smith\NT8Guard') -eq '"C:\Users\Jo Smith\NT8Guard"')
Check "apostrophe needs no quoting" ((ConvertTo-GuardArg "C:\Users\O'Brien") -eq "C:\Users\O'Brien")
Check 'trailing backslash doubled when quoted' ((ConvertTo-GuardArg 'C:\A B\') -eq '"C:\A B\\"')
Check 'embedded quote escaped' ((ConvertTo-GuardArg 'a "b"') -eq '"a \"b\""')
Check 'empty' ((ConvertTo-GuardArg '') -eq '""')

'=== Python: guard.json first, then the bundled runtime, never PATH'
$c = Get-GuardDefaults
Check 'nothing bundled, nothing set -> none' ($null -eq (Get-GuardPythonExe $c))
$c.python.exe = 'C:\Py\python.exe'
Check 'guard.json wins' ((Get-GuardPythonExe $c) -eq 'C:\Py\python.exe')

'=== Invoke-GuardPython runs a tool with awkward arguments and bounds it'
$tools = New-TestDir tools
[IO.File]::WriteAllText((Join-Path $tools 'echo.py'), "import sys, json`nprint(json.dumps(sys.argv[1:]))`nsys.exit(3)`n")
[IO.File]::WriteAllText((Join-Path $tools 'slow.py'), "import time`ntime.sleep(30)`n")
$py = (Get-Command py.exe -ErrorAction SilentlyContinue).Source
if (-not $py) { $py = (Get-Command python.exe -ErrorAction SilentlyContinue).Source }
if ($py) {
    $c.python.exe = $py
    $r = Invoke-GuardPython -Script 'echo.py' -Arguments @('--guard-home', "C:\Users\O'Brien Jr\NT8Guard", 'trail\') -Config $c -ToolsDir $tools
    $got = $r.Out | ConvertFrom-Json
    Check 'exit code passed through' ($r.ExitCode -eq 3) $r.ExitCode
    Check 'arguments arrive intact' (($got -join '|') -eq "--guard-home|C:\Users\O'Brien Jr\NT8Guard|trail\") ($got -join '|')
    $r = Invoke-GuardPython -Script 'slow.py' -Config $c -ToolsDir $tools -TimeoutSeconds 2
    Check 'a hung tool is stopped' ($r.TimedOut -and $r.ExitCode -eq -1)
} else { '  SKIP  no Python on this machine for the process test' }
$r = Invoke-GuardPython -Script 'echo.py' -Config (Get-GuardDefaults) -ToolsDir $tools
Check 'no runtime -> -1 with a reason' ($r.ExitCode -eq -1 -and $r.Err -match 'no Python runtime')

'=== login in flight'
$c = Get-GuardDefaults
Check 'nothing in flight' (-not (Test-LoginInFlight -Config $c))
[IO.File]::WriteAllText((Get-LoginInflightPath), (@{ pid = $PID; startedUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json))
Check 'live marker = in flight' (Test-LoginInFlight -Config $c)
[IO.File]::WriteAllText((Get-LoginInflightPath), (@{ pid = $PID; startedUtc = [DateTime]::UtcNow.AddMinutes(-11).ToString('o') } | ConvertTo-Json))
Check 'stale marker (>10 min) ignored' (-not (Test-LoginInFlight -Config $c))
[IO.File]::WriteAllText((Get-LoginInflightPath), (@{ pid = 999999; startedUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json))
Check 'dead pid ignored' (-not (Test-LoginInFlight -Config $c))
Remove-Item (Get-LoginInflightPath) -Force

'=== Config.xml unlock wait'
$cx = Join-Path $h.Nt8 'Config.xml'
Check 'no Config.xml -> fine' (Wait-Nt8ConfigUnlocked -Seconds 1)
[IO.File]::WriteAllText($cx, '<x/>')
$lock = [IO.File]::Open($cx, 'Open', 'ReadWrite', 'None')
Check 'held -> false after the bound' (-not (Wait-Nt8ConfigUnlocked -Seconds 1))
$lock.Close()
Check 'released -> true' (Wait-Nt8ConfigUnlocked -Seconds 1)

'=== Invoke-Nt8Start: restore first, then log in; arguments follow Protection'
$script:py = @(); $script:launched = 0
$fakePy = { param($s, $a, $t) $script:py += , (@($s) + $a)
            [pscustomobject]@{ ExitCode = 0; TimedOut = $false; Err = ''
                               Out = '{"planned":[],"restored":[{"name":"Demo","id":746,"originalId":746}],"refused":[],"notes":[],"relinked":["Demo: put back on chart tab"],"relinkFailed":[],"backup":null}' } }
$launch = { $script:launched++ }
$c = Get-GuardDefaults
$r = Invoke-Nt8Start -Component relaunch -Config $c -IsRunning { $false } -Python $fakePy -Launch $launch
Check 'launched' ($r.Result -eq 'launched' -and $script:launched -eq 1)
Check 'restore ran first, with --apply and charts' (($script:py[0] -contains '--apply') -and ($script:py[0] -notcontains '--no-relink') -and $script:py[0][0] -eq 'backup_restore.py')
Check 'nt8.launch evented' (@(Read-GuardEvents -KindPattern '^nt8\.launch$').Count -eq 1)
Check 'backups.restored evented' (@(Read-GuardEvents -KindPattern '^backups\.restored$').Count -eq 1)
Check 'last-launch stamp written' ($null -ne (Get-Nt8LastLaunchUtc))

$script:py = @()
$c.protection.restoreStrategies = $false
$c.protection.reattachCharts = $false
$reportOnly = { param($s, $a, $t) $script:py += , (@($s) + $a)
                [pscustomobject]@{ ExitCode = 2; TimedOut = $false; Err = ''; Out = '{"planned":[{"name":"Demo","id":746,"originalId":746,"problems":[]}],"restored":[],"refused":[],"notes":[],"relinked":[],"relinkFailed":[],"backup":null}' } }
$null = Invoke-Nt8Start -Component relaunch -Config $c -IsRunning { $false } -Python $reportOnly -Launch $launch
Check 'restore off: report only (no --apply)' ($script:py[0] -notcontains '--apply')
Check 'charts off: --no-relink' ($script:py[0] -contains '--no-relink')
Check 'restore off: warned that strategies are missing' ([bool](Read-GuardEvents -KindPattern '^alert\.sent$' | Where-Object { $_.text -eq 'NT8 strategies missing' }))

$broken = { param($s, $a, $t) [pscustomobject]@{ ExitCode = -1; TimedOut = $false; Out = ''; Err = $null } }
$before = $script:launched
$null = Invoke-Nt8Start -Component relaunch -Config (Get-GuardDefaults) -IsRunning { $false } -Python $broken -Launch $launch
Check 'restore cannot run -> still launches' ($script:launched -eq $before + 1)
Check 'and says so' (@(Read-GuardEvents -KindPattern '^backups\.restore-failed$').Count -eq 1)

$before = $script:launched
$r = Invoke-Nt8Start -Component relaunch -Config (Get-GuardDefaults) -IsRunning { $true } -Python $fakePy -Launch $launch
Check 'NinjaTrader already running -> nothing' ($r.Result -eq 'already-running' -and $script:launched -eq $before)

function Clear-StartState { foreach ($k in 'state', 'events') { Get-ChildItem (Get-GuardPath $k) -File | Remove-Item -Force } }
$quiet = { param($s, $a, $t) [pscustomobject]@{ ExitCode = 0; TimedOut = $false; Err = ''; Out = '{"planned":[],"restored":[],"refused":[],"notes":[],"relinked":[],"relinkFailed":[],"backup":null}' } }
function Start-Once([hashtable]$Config = (Get-GuardDefaults)) {
    Invoke-Nt8Start -Component relaunch -Config $Config -IsRunning { $false } -IsLocked { $false } -Python $quiet -Launch $launch
}

'=== only processes in THIS Windows session count (another user''s NinjaTrader or sign-in screen does not)'
$mine = @(Get-GuardSessionProcess -Name powershell)
Check 'finds this process' (@($mine | Where-Object { $_.Id -eq $PID }).Count -eq 1)
$sid = (Get-Process -Id $PID).SessionId
Check 'nothing from another session' (@($mine | Where-Object { $_.SessionId -ne $sid }).Count -eq 0)

'=== the in-flight marker: written atomically by the starter, ignored by the login it names'
Clear-StartState
Set-LoginInflight -ProcessId $PID
Check 'marker = in flight for everyone else' (Test-LoginInFlight -Config (Get-GuardDefaults))
Check 'but not for the login it names' (-not (Test-LoginInFlight -Config (Get-GuardDefaults) -ExceptPid $PID))
# Cold test 2026-10-10: a login helper that died left its marker; the NEXT start wrote over it through
# File.Replace(tmp, f, $null) - PowerShell passes $null to a string parameter as "" -> "The path is not of a
# legal form", and every start after the first failed one crashed.
$err = $null
try { Set-LoginInflight -ProcessId 4242 } catch { $err = $_.Exception.Message }
Check 'a second start writes over a marker left behind' (-not $err -and ([IO.File]::ReadAllText((Get-LoginInflightPath)) | ConvertFrom-Json).pid -eq 4242) $err
Check 'and leaves no temp file' (-not @(Get-ChildItem (Split-Path (Get-LoginInflightPath)) -Filter 'login.inflight.*.tmp').Count) ''
Remove-Item (Get-LoginInflightPath) -Force

'=== an external login program counts as in flight only while it is young; a hung one is stopped'
$ext = Get-GuardDefaults
$ext.login.helper = 'C:\Tools\NTLogin.exe'
$now = [DateTime]::Now
$young = { param($n) @([pscustomobject]@{ Id = 501; StartTime = $now.AddMinutes(-2) }) }
$old = { param($n) @([pscustomobject]@{ Id = 502; StartTime = $now.AddMinutes(-11) }) }
Check 'young login program = in flight' (Test-LoginInFlight -Config $ext -GetProcess $young)
Check 'one hung for 11 min = not in flight' (-not (Test-LoginInFlight -Config $ext -GetProcess $old))
$script:killed = @()
$both = { param($n) @([pscustomobject]@{ Id = 501; StartTime = $now.AddMinutes(-2) }, [pscustomobject]@{ Id = 502; StartTime = $now.AddMinutes(-11) }) }
Stop-StaleLoginHelper -Component relaunch -Config $ext -GetProcess $both -Kill { param($id) $script:killed += $id }
Check 'only the hung one is stopped' (($script:killed -join ',') -eq '502') ($script:killed -join ',')
Check 'and it is logged' ((Read-AllText (Join-Path $h.Home 'logs\relaunch.log')) -match 'hung')

'=== one start at a time: a second starter backs off while the first holds the start lock'
Clear-StartState
$before = $script:launched
$held = [IO.File]::Open((Get-GuardStartLockPath), 'OpenOrCreate', 'ReadWrite', 'None')
$r = Start-Once
$held.Close()
Check 'busy while held' ($r.Result -eq 'busy' -and $script:launched -eq $before) $r.Result
Check 'no launch evented while busy' (@(Read-GuardEvents -KindPattern '^nt8\.launch$').Count -eq 0)
$r = Start-Once
Check 'released -> launches' ($r.Result -eq 'launched' -and $script:launched -eq $before + 1) $r.Result

'=== the shared gate (spec 3.4 step 1): every starter is held by pause, lock screen and backoff'
Clear-StartState
$t = [DateTime]::UtcNow
$gate = { param([switch]$Launch, $Locked = $false) Test-Nt8StartAllowed -Component relaunch -Config (Get-GuardDefaults) -IsLocked ([scriptblock]::Create("`$$Locked")) -Now { $t } -ForLaunch:$Launch }
Check 'nothing in the way -> ok' ((& $gate -Launch) -eq 'ok')
$null = Set-GuardPause -Minutes 5
Check 'paused' ((& $gate) -eq 'paused')
$r = Start-Once
Check 'Invoke-Nt8Start honours the pause' ($r.Result -eq 'paused') $r.Result
Clear-GuardPause
Check 'locked screen -> held' ((& $gate -Locked $true) -eq 'held-locked')
$n = (Read-AllText (Join-Path $h.Home 'logs\relaunch.log')).Split("`n").Count
$null = & $gate -Locked $true
Check 'held is logged once, not every minute' ((Read-AllText (Join-Path $h.Home 'logs\relaunch.log')).Split("`n").Count -eq $n)
Check 'unlocked -> ok and said so' ((& $gate) -eq 'ok' -and (Read-AllText (Join-Path $h.Home 'logs\relaunch.log')) -match 'UNLOCKED')
Check 'a lock probe that throws fails OPEN' ((Test-Nt8StartAllowed -Component relaunch -Config (Get-GuardDefaults) -IsLocked { throw 'x' } -Now { $t }) -eq 'ok')

'=== backoff: 4 starts/stops in 30 min stop starts for 60 min, then 120, 240 ... (24 h max)'
Clear-StartState
1..4 | ForEach-Object { Write-GuardEvent -Component relaunch -Kind 'nt8.launch' -Severity warn -Text 'earlier' }
Check 'budget spent -> backoff' ((& $gate -Launch) -eq 'backoff')
$until = ConvertFrom-GuardUtc ([IO.File]::ReadAllText((Join-Path (Get-GuardPath state) 'start.backoff-until')).Trim())
Check 'first backoff 60 min' ([math]::Round(($until - $t).TotalMinutes) -eq 60) ($until - $t).TotalMinutes
Check 'alerted' ([bool](Read-GuardEvents -KindPattern '^alert\.sent$' | Where-Object { $_.text -like '*backoff*' }))
# Cold test 2026-10-10: the attempts that tripped it had crashed before NinjaTrader started - "NinjaTrader was
# started or stopped 4 times" overstated it. Guard counts its own attempts.
$logs = (Get-ChildItem (Get-GuardPath logs) -Filter *.log | ForEach-Object { Read-AllText $_.FullName }) -join "`n"
Check 'the alert says Guard TRIED to start or stop it' ($logs -match 'Guard tried to start or stop NinjaTrader 4 times') ''
Check 'still inside -> backing-off' ((& $gate) -eq 'backing-off')
Remove-Item (Join-Path (Get-GuardPath state) 'start.backoff-until') -Force
Check 'a second trip within 24 h -> 120 min' ((& $gate -Launch) -eq 'backoff')
$until = ConvertFrom-GuardUtc ([IO.File]::ReadAllText((Join-Path (Get-GuardPath state) 'start.backoff-until')).Trim())
Check 'doubled' ([math]::Round(($until - $t).TotalMinutes) -eq 120) ($until - $t).TotalMinutes

'=== a refused password is not hammered: two refused logins stop starts until the password is saved again'
Clear-StartState
1..2 | ForEach-Object { Write-GuardEvent -Component login -Kind 'login.failed' -Severity alert -Text 'gave up' -Data @{ result = 'gave-up' } }
Check 'held: login-refused' ((& $gate -Launch) -eq 'login-refused')
Check 'alerted once' (@(Read-GuardEvents -KindPattern '^alert\.sent$').Count -eq 1)
$null = & $gate -Launch
Check 'not re-alerted every minute' (@(Read-GuardEvents -KindPattern '^alert\.sent$').Count -eq 1)
Start-Sleep -Milliseconds 50
$cred = Join-Path (Get-GuardPath secrets) 'nt8.cred'
[IO.File]::WriteAllText($cred, 'x')
Check 'password saved again -> starts allowed' ((& $gate -Launch) -eq 'ok')
Remove-Item $cred -Force
Clear-StartState
1..2 | ForEach-Object { Write-GuardEvent -Component login -Kind 'login.failed' -Severity alert -Text 'gave up' -Data @{ result = 'gave-up' } }
Start-Sleep -Milliseconds 50
Set-Nt8SeenHealthy
Check 'NinjaTrader seen logged in since -> starts allowed' ((& $gate -Launch) -eq 'ok')
Clear-StartState
Write-GuardEvent -Component login -Kind 'login.failed' -Severity alert -Text 'no window' -Data @{ result = 'no-window' }
Write-GuardEvent -Component login -Kind 'login.failed' -Severity alert -Text 'gave up' -Data @{ result = 'gave-up' }
Check 'a failure that is not a refused password does not hold' ((& $gate -Launch) -eq 'ok')
$ext2 = Get-GuardDefaults; $ext2.login.helper = 'C:\Tools\NTLogin.exe'
Check 'external login program: its own business' ((Test-Nt8StartAllowed -Component relaunch -Config $ext2 -IsLocked { $false } -Now { $t } -ForLaunch) -eq 'ok')

'=== an external login program that fails fast is reported (exit code, event, alert)'
Clear-StartState
$cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
$c = Get-GuardDefaults; $c.login.helper = $cmd
$r = Invoke-Nt8Start -Component relaunch -Config $c -IsRunning { $false } -IsLocked { $false } -Python $quiet `
                     -Launch { Start-Process -FilePath $cmd -ArgumentList '/c exit 3' -WindowStyle Hidden -PassThru }
Check 'login.failed with the exit code' ([bool](Read-GuardEvents -KindPattern '^login\.failed$' | Where-Object { $_.text -match 'exit code 3' }))
Check 'alerted' ([bool](Read-GuardEvents -KindPattern '^alert\.sent$' | Where-Object { $_.text -match 'login' }))
Clear-StartState
$null = Invoke-Nt8Start -Component relaunch -Config $c -IsRunning { $false } -IsLocked { $false } -Python $quiet `
                        -Launch { Start-Process -FilePath $cmd -ArgumentList '/c exit 0' -WindowStyle Hidden -PassThru }
Check 'exit 0 -> no failure' (@(Read-GuardEvents -KindPattern '^login\.failed$').Count -eq 0)

'=== relaunch switched off (Watch only): no starter starts NinjaTrader'
Clear-StartState
$wcfg = Get-GuardDefaults; $wcfg.protection.preset = 'watch'
Check 'gate says relaunch-off' ((Test-Nt8StartAllowed -Component relaunch -Config $wcfg -IsLocked { $false } -Now { $t } -ForLaunch) -eq 'relaunch-off')
$before = $script:launched
$r = Start-Once $wcfg
Check 'Invoke-Nt8Start refuses too' ($r.Result -eq 'relaunch-off' -and $script:launched -eq $before) $r.Result
Check 'without -ForLaunch it is not a start, so not refused' ((Test-Nt8StartAllowed -Component relaunch -Config $wcfg -IsLocked { $false } -Now { $t }) -eq 'ok')

'=== the start lock is shared: whoever opens it first holds it'
Clear-StartState
$a = Open-GuardStartLock
Check 'first open holds it' ($null -ne $a)
Check 'a second open is refused while held' ($null -eq (Open-GuardStartLock))
$a.Dispose()
$b = Open-GuardStartLock
Check 'free again once released' ($null -ne $b)
$b.Dispose()

Complete-Test
