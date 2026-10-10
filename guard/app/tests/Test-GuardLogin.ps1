. (Join-Path $PSScriptRoot 'TestKit.ps1')
$h = Use-TestHomes login
. (Join-Path $script:AppDir 'Guard-Login.ps1') -ImportOnly

# A scripted NinjaTrader: when the process appears, when its window appears, and what the window
# title becomes after each submit. Time is simulated - Sleep advances the clock.
#   SetValueWorks : UI Automation can set the password box directly (no keyboard at all)
#   InFront       : Windows lets NinjaTrader's window come to the front with keyboard focus
#   HasButton     : UI Automation finds the Log In button
function New-World {
    param([bool]$Running = $false, [int]$ProcessAfterPolls = 0, [int]$WindowAfterPolls = 0,
          [string[]]$TitleAfterSubmit = @('Control Center'), [string]$Title = 'Welcome',
          [bool]$ExitAfterSubmit = $false, [int]$ThrowOnAttempt = 0,
          [bool]$SetValueWorks = $true, [bool]$InFront = $true, [bool]$HasButton = $true, [bool]$ValueIgnored = $false)
    $script:w = @{ Running = $Running; ProcPolls = $ProcessAfterPolls; WinPolls = $WindowAfterPolls; Title = $Title
                   After = $TitleAfterSubmit; Submits = 0; Starts = 0; Typed = @(); Values = @(); Enters = 0; Attempt = 0
                   ExitAfterSubmit = $ExitAfterSubmit; ThrowOnAttempt = $ThrowOnAttempt; ValueIgnored = $ValueIgnored
                   SetValueWorks = $SetValueWorks; InFront = $InFront; HasButton = $HasButton; Entered = $false
                   T = [DateTime]'2026-10-09T12:00:00Z' }
    $script:log = @()
}
function Submit-Fake {
    $script:w.Submits++
    if ($script:w.ExitAfterSubmit) { $script:w.Running = $false; return }
    if (-not $script:w.Entered) { return }          # submitted with nothing entered: still at Welcome
    $i = [Math]::Min($script:w.Submits, $script:w.After.Count) - 1
    $script:w.Title = $script:w.After[$i]
}
$ops = @{
    IsRunning   = { if (-not $script:w.Running -and $script:w.Starts -gt 0) { if ($script:w.ProcPolls -le 0) { $script:w.Running = $true } else { $script:w.ProcPolls-- } }; $script:w.Running }
    Start       = { param($exe) $script:w.Starts++ }
    Window      = { if (-not $script:w.Running) { return $null }
                    if ($script:w.WinPolls -gt 0) { $script:w.WinPolls--; return $null }
                    return @{ Handle = [IntPtr]42; Title = $script:w.Title; Pid = 4242 } }
    SetValue    = { param($hwnd, $s)
                    $script:w.Attempt++
                    if ($script:w.Attempt -eq $script:w.ThrowOnAttempt) { throw 'automation failed' }
                    if (-not $script:w.SetValueWorks) { return $false }
                    $script:w.Values += $s; $script:w.Entered = -not $script:w.ValueIgnored; return $true }
    Foreground  = { param($win) $script:w.InFront }
    Type        = { param($s) $script:w.Typed += $s; $script:w.Entered = $true; 'paste' }
    PressButton = { param($hwnd) if (-not $script:w.HasButton) { return $false }; Submit-Fake; $true }
    Enter       = { $script:w.Enters++; Submit-Fake }
    Sleep       = { param($ms) $script:w.T = $script:w.T.AddMilliseconds($ms) }
    Now         = { $script:w.T }
}
$secret = 'S3cr+t^%~(pw)'
function Login { Invoke-GuardLogin -Ops $ops -Nt8Exe 'C:\NT\bin\NinjaTrader.exe' -GetSecret { $secret } -Log { param($m) $script:log += $m } }

'=== already logged in: nothing touched'
New-World -Running $true -Title 'Control Center'
Check 'already' ((Login) -eq 'already')
Check 'no start, no typing' ($script:w.Starts -eq 0 -and $script:w.Typed.Count -eq 0 -and $script:w.Values.Count -eq 0)

'=== cold start: waits for the process and window; UI Automation sets the password - no keyboard, no clipboard'
New-World -ProcessAfterPolls 3 -WindowAfterPolls 4
Check 'ok' ((Login) -eq 'ok')
Check 'started once' ($script:w.Starts -eq 1)
Check 'password set once, straight into the box' ($script:w.Values.Count -eq 1 -and $script:w.Values[0] -eq $secret)
Check 'nothing typed, no Enter' ($script:w.Typed.Count -eq 0 -and $script:w.Enters -eq 0)
Check 'the password never reaches the log' (-not ($script:log -join "`n").Contains($secret))
Check 'log says how it was entered' ([bool]($script:log -match 'entry=uia-value'))

'=== the keyboard is used only when NinjaTrader is verified in front'
New-World -Running $true -SetValueWorks $false
Check 'in front -> typed, ok' ((Login) -eq 'ok' -and $script:w.Typed.Count -eq 1 -and $script:w.Typed[0] -eq $secret)
New-World -Running $true -SetValueWorks $false -InFront $false
$t0 = $script:w.T
Check 'NOT in front -> never typed, never Enter' ((Login) -eq 'not-in-front' -and $script:w.Typed.Count -eq 0 -and $script:w.Enters -eq 0) "$($script:w.Typed.Count) typed"
Check 'says why' ([bool]($script:log -match 'not in front'))
Check 'bounded (3 short attempts)' ((($script:w.T - $t0).TotalSeconds) -lt 60)

'=== Enter only to a verified NinjaTrader window'
New-World -Running $true -HasButton $false
Check 'no button + in front -> Enter, ok' ((Login) -eq 'ok' -and $script:w.Enters -eq 1)
New-World -Running $true -HasButton $false -InFront $false
$r = Login
Check 'no button + NOT in front -> never Enter' ($script:w.Enters -eq 0) $r
Check 'and that is not a refused password' ($r -eq 'not-in-front') $r

'=== a box that ignores the value: the next attempt types instead (when in front)'
New-World -Running $true -ValueIgnored $true -TitleAfterSubmit @('Control Center')
Check 'ok on attempt 2 by keyboard' ((Login) -eq 'ok' -and $script:w.Typed.Count -eq 1) "$($script:w.Typed.Count) typed"

'=== a failed attempt is retried'
New-World -Running $true -TitleAfterSubmit @('Welcome', 'Control Center')
Check 'ok on attempt 2' ((Login) -eq 'ok')
Check 'two submits' ($script:w.Submits -eq 2)
New-World -Running $true -ThrowOnAttempt 1
Check 'a throw on attempt 1 is survived' ((Login) -eq 'ok')

'=== bounded: never logs in -> gives up after exactly 3 attempts'
New-World -Running $true -TitleAfterSubmit @('Welcome') -SetValueWorks $false
$t0 = $script:w.T
Check 'gave-up' ((Login) -eq 'gave-up')
Check 'three attempts' ($script:w.Submits -eq 3)
Check 'about 3 x 60 s of simulated time' ((($script:w.T - $t0).TotalSeconds) -ge 180 -and (($script:w.T - $t0).TotalSeconds) -lt 200)

'=== the process never appears'
New-World -ProcessAfterPolls 100000
Check 'no-process after the bound' ((Login) -eq 'no-process')

'=== NinjaTrader exits after the password'
New-World -Running $true -ExitAfterSubmit $true
Check 'exited' ((Login) -eq 'exited')

'=== every outcome is evented; failures reach the phone'
$cfg = Get-GuardDefaults
Publish-LoginResult -Result 'ok' -Config $cfg
Check 'ok -> login.ok, no alert' (@(Read-GuardEvents -KindPattern '^login\.ok$').Count -eq 1 -and @(Read-GuardEvents -KindPattern '^alert\.sent$').Count -eq 0)
Publish-LoginResult -Result 'gave-up' -Config $cfg
$f = @(Read-GuardEvents -KindPattern '^login\.failed$')
Check 'gave-up -> login.failed with the result' ($f.Count -eq 1 -and $f[0].data.result -eq 'gave-up')
Check 'gave-up -> alerted, naming the fix' ([bool](Read-GuardEvents -KindPattern '^alert\.sent$' | Where-Object { $_.text -match 'login' }))
Check 'the alert body says how to fix it' ((Read-AllText (Join-Path $h.Home 'logs\login.log')) -match 'NinjaTrader login')
Publish-LoginResult -Result 'no-password' -Config $cfg
Check 'no password -> alerted' (@(Read-GuardEvents -KindPattern '^alert\.sent$').Count -eq 2)

'=== the saved password: DPAPI for this user, never plain text on disk'
$sec = ConvertTo-SecureString $secret -AsPlainText -Force
Save-GuardPassword -Password $sec
$file = Get-GuardCredentialPath
Check 'saved under secrets' ((Test-Path $file) -and $file -like "$($h.Home)*secrets*")
Check 'not plain text' (-not (Read-AllText $file).Contains($secret))
Check 'reads back' ((Get-GuardPasswordPlain) -eq $secret)
Check 'Test-GuardPassword true' (Test-GuardPassword)
Remove-Item $file -Force
Check 'Test-GuardPassword false when missing' (-not (Test-GuardPassword))

'=== keyboard fallbacks: SendKeys escaping; the clipboard copy stays out of history and the cloud'
Check 'specials braced' ((ConvertTo-SendKeysLiteral 'a+b^c%d~e(f)g{h}i[j]') -eq 'a{+}b{^}c{%}d{~}e{(}f{)}g{{}h{}}i{[}j{]}')
Check 'plain untouched' ((ConvertTo-SendKeysLiteral 'Plain123') -eq 'Plain123')
Add-Type -AssemblyName System.Windows.Forms
$do = New-SecretDataObject 'x'
$fmts = @($do.GetFormats())
foreach ($f in 'ExcludeClipboardContentFromMonitorProcessing', 'CanIncludeInClipboardHistory', 'CanUploadToCloudClipboard') {
    Check "clipboard data carries $f" ($fmts -contains $f) ($fmts -join ', ')
}
Check 'history/cloud flags are 0' ((([IO.MemoryStream]$do.GetData('CanIncludeInClipboardHistory')).ToArray() -join ',') -eq '0,0,0,0')

'=== "clear cache" clears only what NinjaTrader re-creates - never its historical data'
foreach ($d in 'cache', 'db\cache', 'tmp', 'db\day', 'db\minute', 'db\tick') {
    New-Item -ItemType Directory -Path (Join-Path $h.Nt8 $d) -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $h.Nt8 "$d\f.bin"), 'x')
}
Clear-Nt8Cache -Nt8Dir $h.Nt8 -Log { param($m) }
Check 'caches emptied' (-not (Test-Path (Join-Path $h.Nt8 'cache\f.bin')) -and -not (Test-Path (Join-Path $h.Nt8 'db\cache\f.bin')) -and -not (Test-Path (Join-Path $h.Nt8 'tmp\f.bin')))
Check 'historical data untouched' ((Test-Path (Join-Path $h.Nt8 'db\day\f.bin')) -and (Test-Path (Join-Path $h.Nt8 'db\minute\f.bin')) -and (Test-Path (Join-Path $h.Nt8 'db\tick\f.bin')))

'=== the helper marks its login inside its try (cold test 2026-10-10)'
# The starter writes the in-flight marker, then the helper writes it again. That second write threw (see
# Test-GuardStart) OUTSIDE the helper's try: it died with no log line, no NinjaTrader, and its marker left
# behind. Anything that can fail must be inside the try, whose finally removes the marker.
$body = [IO.File]::ReadAllText((Join-Path $script:AppDir 'Guard-Login.ps1'))
Check 'Set-LoginInflight runs inside the try' ($body -match '(?s)\$code = 1\s*\r?\ntry \{\s*\r?\n\s*Set-LoginInflight -ProcessId \$PID') ''

Complete-Test
