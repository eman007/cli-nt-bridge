# Guard-Login.ps1 - start NinjaTrader and log in with the saved password.
#
# NinjaTrader has no command-line login, so the password is entered into its login window. That only
# works on an UNLOCKED, connected desktop (every starter holds off while the screen is locked).
#   * First choice, no keyboard at all: Windows UI Automation finds the password box by what it is
#     (not where it sits) and sets its value, then presses the Log In button. Nothing can land in
#     another app, and the clipboard is never used.
#   * Only if that is not possible, the keyboard - and only after checking that NinjaTrader's window
#     really is in front with the keyboard focus. A watchdog runs in the background, and Windows often
#     refuses to bring a window forward while you are typing elsewhere: then nothing is typed (the
#     password would go into your chat or browser) and the attempt is counted as 'not in front'.
#     The keyboard path pastes from a clipboard copy marked to stay out of clipboard history and
#     cloud sync (cleared right after), or types if the clipboard is unavailable.
#   * Enter is pressed only to a verified NinjaTrader window, when there is no Log In button.
#   * NinjaTrader must remember the USERNAME (tick "Remember username" in its login window once):
#     only the password is entered.
# Bounded everywhere: 120 s for the process and its window, 3 attempts, 60 s to leave 'Welcome'.
# Every outcome is an event; every failure is a phone alert that says what to do.
#
# Usage:
#   Guard-Login.ps1                 start NinjaTrader if needed and log in
#   Guard-Login.ps1 -SetPassword    save the NinjaTrader password (encrypted for this Windows user)
#   Guard-Login.ps1 -Check          is a readable password saved?
# Exit: 0 logged in / already up / saved; 1 failed.
[CmdletBinding()]
param([string]$GuardHome, [switch]$SetPassword, [switch]$Check, [switch]$ImportOnly)

$ErrorActionPreference = 'Stop'
# The saved password (DPAPI, this Windows user only) lives in lib\Guard.Password.psm1: the control panel saves it too.
foreach ($m in 'Guard.Paths', 'Guard.Log', 'Guard.Config', 'Guard.Events', 'Guard.Alerts', 'Guard.Start', 'Guard.Password') {
    Import-Module (Join-Path $PSScriptRoot "lib\$m.psm1")
}

function ConvertTo-SendKeysLiteral {
    # SendKeys treats + ^ % ~ ( ) { } [ ] as syntax; brace each one so it is typed literally.
    param([AllowEmptyString()][string]$Text)
    return (($Text.ToCharArray() | ForEach-Object { if ('+^%~(){}[]'.IndexOf($_) -ge 0) { '{' + $_ + '}' } else { [string]$_ } }) -join '')
}

# ------------------------------------------------------------------------------------------------
# The login flow. Pure logic over $Ops (scriptblocks) so tests can drive it with a fake NinjaTrader:
#   IsRunning() -> bool                 Start($exe)
#   Window() -> @{ Handle; Title; Pid } | $null
#   SetValue($h, $secret) -> bool       set the password box through UI Automation (no keyboard)
#   Foreground($window) -> bool         bring NinjaTrader forward; TRUE only if it then owns the
#                                       foreground window and the keyboard focus
#   Type($secret) -> 'paste'|'typed'    PressButton($h) -> bool (Log In via UI Automation)
#   Enter()                             Sleep($ms)          Now() -> [DateTime] UTC
# Returns 'ok' | 'already' | 'no-process' | 'no-window' | 'exited' | 'gave-up' (the password was
# submitted and refused) | 'not-in-front' (it never could be entered safely). Never logs the secret.
# ------------------------------------------------------------------------------------------------
function Invoke-GuardLogin {
    param([Parameter(Mandatory)][hashtable]$Ops, [Parameter(Mandatory)][string]$Nt8Exe,
          [Parameter(Mandatory)][scriptblock]$GetSecret, [scriptblock]$Log = { param($m) },
          [int]$ProcessWaitSeconds = 120, [int]$WindowWaitSeconds = 120, [int]$Attempts = 3, [int]$VerifySeconds = 60,
          [int]$NotInFrontWaitMs = 5000)

    if (& $Ops.IsRunning) {
        $w = & $Ops.Window
        if ($w -and $w.Title -and $w.Title -ne 'Welcome') { & $Log "NinjaTrader is already up ('$($w.Title)') - nothing to do"; return 'already' }
    } else {
        & $Log "starting $Nt8Exe"
        & $Ops.Start $Nt8Exe
        $until = (& $Ops.Now).AddSeconds($ProcessWaitSeconds)
        while (-not (& $Ops.IsRunning)) {
            if ((& $Ops.Now) -ge $until) { & $Log "ERROR: NinjaTrader did not start within ${ProcessWaitSeconds}s"; return 'no-process' }
            & $Ops.Sleep 200
        }
    }

    $until = (& $Ops.Now).AddSeconds($WindowWaitSeconds)
    while (-not (& $Ops.Window)) {
        if (-not (& $Ops.IsRunning)) { & $Log 'ERROR: NinjaTrader exited while we waited for its window'; return 'exited' }
        if ((& $Ops.Now) -ge $until) { & $Log "ERROR: no NinjaTrader window after ${WindowWaitSeconds}s"; return 'no-window' }
        & $Ops.Sleep 250
    }

    $useValue = $true        # UI Automation first; after it fails to log in once, the keyboard
    $submitted = $false
    for ($i = 1; $i -le $Attempts; $i++) {
        $w = & $Ops.Window
        if (-not $w) {
            if (-not (& $Ops.IsRunning)) { & $Log 'ERROR: NinjaTrader exited during the login'; return 'exited' }
            & $Ops.Sleep 1000; continue
        }
        if ($w.Title -and $w.Title -ne 'Welcome') { & $Log "login OK - window is '$($w.Title)'"; return 'ok' }
        $entry = $null
        try {
            if ($useValue -and (& $Ops.SetValue $w.Handle (& $GetSecret))) { $entry = 'uia-value' }
            elseif (& $Ops.Foreground $w) { $entry = & $Ops.Type (& $GetSecret) }
            else {
                & $Log "attempt ${i}: NinjaTrader's login window is not in front (Windows would not bring it forward) - not typing, the password would go to another app"
                & $Ops.Sleep $NotInFrontWaitMs; continue
            }
            if (& $Ops.PressButton $w.Handle) { $sub = 'button' }
            elseif (& $Ops.Foreground $w) { & $Ops.Enter; $sub = 'enter' }
            else {
                & $Log "attempt ${i}: entry=$entry, but no Log In button and NinjaTrader is not in front - not pressing Enter"
                & $Ops.Sleep $NotInFrontWaitMs; continue
            }
            $submitted = $true
            & $Log "attempt ${i}: entry=$entry submit=$sub"
        } catch {
            & $Log "attempt $i failed: $($_.Exception.GetType().Name): $($_.Exception.Message)"
            & $Ops.Sleep 2000
            continue
        }
        $until = (& $Ops.Now).AddSeconds($VerifySeconds)
        while ((& $Ops.Now) -lt $until) {
            if (-not (& $Ops.IsRunning)) { & $Log 'ERROR: NinjaTrader exited after the password was entered'; return 'exited' }
            $w2 = & $Ops.Window
            if ($w2 -and $w2.Title -and $w2.Title -ne 'Welcome') { & $Log "login OK - window is now '$($w2.Title)'"; return 'ok' }
            & $Ops.Sleep 500
        }
        & $Log "attempt ${i}: still at the login screen after ${VerifySeconds}s"
        if ($entry -eq 'uia-value') { $useValue = $false; & $Log 'the password box did not take the value - the next attempt uses the keyboard' }
    }
    if (-not $submitted) {
        & $Log "ERROR: the password could not be entered safely in $Attempts attempts - NinjaTrader is left at its login screen"
        return 'not-in-front'
    }
    & $Log "ERROR: gave up after $Attempts attempts - NinjaTrader is left at its login screen"
    return 'gave-up'
}

function Publish-LoginResult {
    # Event for every outcome (data.result = the outcome: the start gate reads 'gave-up' as a refused
    # password); phone alert for every failure, saying what to do.
    param([Parameter(Mandatory)][string]$Result, [Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [string]$Detail)
    if ($Result -eq 'ok' -or $Result -eq 'already') {
        Write-GuardEvent -Component login -Kind 'login.ok' -Severity info -Text "NinjaTrader login: $Result" -Data @{ result = $Result } -GuardHome $GuardHome
        return
    }
    $body = switch ($Result) {
        'gave-up'      { 'NinjaTrader is at its login window: the saved password was entered and it did not log in. If the password changed, save it again in NT8 Guard: Tools > NinjaTrader login.' }
        'not-in-front' { 'NinjaTrader is at its login window, but Windows would not bring it to the front, so the password was not entered (it would have gone into another app). Log in by hand, or leave the PC idle for a few minutes and Guard tries again.' }
        'no-password'  { 'No NinjaTrader password is saved for Guard, so it cannot log in. Save it in NT8 Guard: Tools > NinjaTrader login.' }
        'no-process'   { 'NinjaTrader did not start. Check that it opens by hand.' }
        'no-window'    { 'NinjaTrader started but never showed its login window.' }
        'exited'       { 'NinjaTrader closed during the login.' }
        default        { "The login helper failed: $Detail" }
    }
    Write-GuardLog -Component login -Message "LOGIN FAILED ($Result): $body" -GuardHome $GuardHome
    Write-GuardEvent -Component login -Kind 'login.failed' -Severity alert -Text "NinjaTrader login failed: $Result" -Data @{ result = $Result } -GuardHome $GuardHome
    Send-GuardAlert -Title "NT8 login failed ($Result)" -Body $body -Severity alert -Config $Config -Component login -GuardHome $GuardHome | Out-Null
}

# ------------------------------------------------------------------------------------------------
# The real operations: Win32 + Windows UI Automation (+ clipboard/SendKeys as the fallback).
# ------------------------------------------------------------------------------------------------
function Initialize-LoginNative {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
    if (-not ('NT8Guard.LoginWin' -as [type])) {
        Add-Type -Namespace NT8Guard -Name LoginWin -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool IsIconic(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint processId);
'@
    }
}

function Find-Nt8PasswordBox {
    param([IntPtr]$Handle)
    try {
        $root = [Windows.Automation.AutomationElement]::FromHandle($Handle)
        $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::IsPasswordProperty, $true)
        return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
    } catch { return $null }
}

function Find-Nt8LoginButton {
    param([IntPtr]$Handle)
    try {
        $root = [Windows.Automation.AutomationElement]::FromHandle($Handle)
        $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
        foreach ($b in $root.FindAll([Windows.Automation.TreeScope]::Descendants, $c)) {
            if ($b.Current.Name -match '^\s*(log\s*in|sign\s*in)\s*$') { return $b }
        }
    } catch { }
    return $null
}

function New-SecretDataObject {
    # Clipboard data for the password, marked so Windows keeps it out of clipboard history (Win+V),
    # cloud clipboard sync and clipboard monitors.
    param([string]$Secret)
    $do = New-Object System.Windows.Forms.DataObject
    $do.SetData([System.Windows.Forms.DataFormats]::UnicodeText, $Secret)
    foreach ($f in 'ExcludeClipboardContentFromMonitorProcessing', 'CanIncludeInClipboardHistory', 'CanUploadToCloudClipboard') {
        $do.SetData($f, (New-Object IO.MemoryStream (, [byte[]](0, 0, 0, 0))))
    }
    return $do
}

function Send-LoginSecret {
    param([string]$Secret)
    try {
        [Windows.Forms.Clipboard]::SetDataObject((New-SecretDataObject $Secret), $false, 10, 100)
        [Windows.Forms.SendKeys]::SendWait('^v')
        Start-Sleep -Milliseconds 150
        return 'paste'
    } catch {
        # The clipboard belongs to the RDP redirector and throws in a disconnected session.
        [Windows.Forms.SendKeys]::SendWait((ConvertTo-SendKeysLiteral $Secret))
        return 'typed'
    } finally {
        try { [Windows.Forms.Clipboard]::Clear() } catch { }
    }
}

$script:RealOps = @{
    IsRunning   = { [bool](@(Get-GuardSessionProcess -Name NinjaTrader).Count) }
    Start       = { param($exe) Start-Process -FilePath $exe | Out-Null }
    Window      = {
        $p = @(Get-GuardSessionProcess -Name NinjaTrader) | Select-Object -First 1
        if (-not $p) { return $null }
        $p.Refresh()
        if ($p.MainWindowHandle -eq [IntPtr]::Zero) { return $null }
        return @{ Handle = $p.MainWindowHandle; Title = $p.MainWindowTitle; Pid = $p.Id }
    }
    SetValue    = {
        param($h, $s)
        $box = Find-Nt8PasswordBox $h
        if (-not $box) { return $false }
        $vp = $null
        if (-not $box.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { return $false }
        try { $vp.SetValue($s); return $true } catch { return $false }
    }
    Foreground  = {
        param($win)
        $h = $win.Handle
        if ([NT8Guard.LoginWin]::IsIconic($h)) { [NT8Guard.LoginWin]::ShowWindow($h, 9) | Out-Null }   # SW_RESTORE
        [NT8Guard.LoginWin]::SetForegroundWindow($h) | Out-Null
        Start-Sleep -Milliseconds 400
        [uint32]$fgPid = 0
        [NT8Guard.LoginWin]::GetWindowThreadProcessId([NT8Guard.LoginWin]::GetForegroundWindow(), [ref]$fgPid) | Out-Null
        if ($fgPid -ne [uint32]$win.Pid) { return $false }
        $box = Find-Nt8PasswordBox $h
        if ($box) { try { $box.SetFocus() } catch { } }
        try {
            $fe = [Windows.Automation.AutomationElement]::FocusedElement
            if ($fe.Current.ProcessId -ne $win.Pid) { return $false }
            if ($box -and -not $fe.Current.IsPassword) { return $false }
        } catch { return $false }
        return $true
    }
    Type        = { param($s) Send-LoginSecret $s }
    PressButton = {
        param($h)
        $b = Find-Nt8LoginButton $h
        if (-not $b) { return $false }
        $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        return $true
    }
    Enter       = { [Windows.Forms.SendKeys]::SendWait('{ENTER}') }
    Sleep       = { param($ms) Start-Sleep -Milliseconds $ms }
    Now         = { [DateTime]::UtcNow }
}

function Clear-Nt8Cache {
    # Opt-in (login.clearCacheBeforeLaunch): empty the folders NinjaTrader re-creates by itself. Never
    # db\day, db\minute or db\tick - that is its historical data, including data you imported.
    param([string]$Nt8Dir, [scriptblock]$Log)
    foreach ($rel in 'cache', 'db\cache', 'tmp') {
        $p = Join-Path $Nt8Dir $rel
        try { if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction Stop }; New-Item -ItemType Directory -Path $p -Force | Out-Null }
        catch { & $Log "clearing $rel failed (continuing): $($_.Exception.Message)" }
    }
}

if ($ImportOnly) { return }

# ------------------------------------------------------------------------------------------------
# Main
# ------------------------------------------------------------------------------------------------
if ($SetPassword) {
    $s = Read-Host 'NinjaTrader password' -AsSecureString
    Save-GuardPassword -Password $s -GuardHome $GuardHome
    'Saved, encrypted for this Windows user only.'
    exit 0
}
if ($Check) {
    if (Test-GuardPassword $GuardHome) { 'A password is saved and readable by this Windows user.'; exit 0 }
    'No readable password is saved. Save it in NT8 Guard: Tools > NinjaTrader login, or run: Guard-Login.ps1 -SetPassword'; exit 1
}

$cfg = Get-GuardConfig -GuardHome $GuardHome
$log = { param($m) Write-GuardLog -Component login -Message $m -GuardHome $GuardHome }
# The starter wrote the in-flight marker with THIS process's id before starting us; any other live
# login means a second one would type into the same box.
if (Test-LoginInFlight -Config @{ login = @{ helper = 'builtin' } } -GuardHome $GuardHome -ExceptPid $PID) {
    & $log 'another login is already in progress - leaving it to finish'; exit 0
}
if (-not (Test-GuardPassword $GuardHome)) {
    Publish-LoginResult -Result 'no-password' -Config $cfg -GuardHome $GuardHome
    exit 1
}

Set-LoginInflight -ProcessId $PID -GuardHome $GuardHome
$code = 1
try {
    $nt8 = Get-Nt8DataDir $cfg.nt8.dataDir
    if ($cfg.login.clearCacheBeforeLaunch -and -not (& $script:RealOps.IsRunning)) { Clear-Nt8Cache -Nt8Dir $nt8 -Log $log }
    Initialize-LoginNative
    $gh = $GuardHome
    $result = Invoke-GuardLogin -Ops $script:RealOps -Nt8Exe (Get-Nt8Exe $cfg.nt8.programDir) `
                                -GetSecret { Get-GuardPasswordPlain $gh } -Log $log
    Publish-LoginResult -Result $result -Config $cfg -GuardHome $GuardHome
    if ($result -eq 'ok' -or $result -eq 'already') { $code = 0 }
} catch {
    & $log "FATAL $($_.Exception.GetType().Name): $($_.Exception.Message)"
    Publish-LoginResult -Result 'crashed' -Detail $_.Exception.Message -Config $cfg -GuardHome $GuardHome
} finally {
    Remove-Item (Get-LoginInflightPath $GuardHome) -Force -ErrorAction SilentlyContinue
}
exit $code
