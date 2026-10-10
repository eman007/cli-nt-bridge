# Probe-LoginWindow.ps1 - what does Windows UI Automation see in NinjaTrader's LOGIN window?
#
# Run it while NinjaTrader sits at its login ('Welcome') window. It only LOOKS: it never types, never
# clicks, never changes focus unless -TryFocus is given (then it focuses the password box, nothing more).
# The answer decides how Guard-Login.ps1 enters the password on this NinjaTrader version:
#   password box offers ValuePattern     -> the password is set in the box directly (no keyboard)
#   box found, no ValuePattern           -> keyboard, after verifying NinjaTrader is in front with focus
#   not found                            -> keyboard into the window's own focus, same verification
#   Log In button found                  -> press it via UI Automation; otherwise Enter (verified window)
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File Probe-LoginWindow.ps1 [-TryFocus] [-OutFile probe.json]
[CmdletBinding()] param([switch]$TryFocus, [string]$OutFile)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$p = Get-Process -Name NinjaTrader -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { 'NinjaTrader is not running. Start it (NinjaTrader.exe, not the login helper) and leave it at its login window.'; exit 1 }
$p.Refresh()
if ($p.MainWindowHandle -eq [IntPtr]::Zero) { 'NinjaTrader has no window yet - wait for the login window and run again.'; exit 1 }
$root = [Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)

$rows = foreach ($e in $all) {
    $c = $e.Current
    if ($c.ControlType -in [Windows.Automation.ControlType]::Edit, [Windows.Automation.ControlType]::Button,
                          [Windows.Automation.ControlType]::CheckBox, [Windows.Automation.ControlType]::Text) {
        [pscustomobject]@{ Type = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''; Name = $c.Name
                           AutomationId = $c.AutomationId; ClassName = $c.ClassName; IsPassword = $c.IsPassword
                           Enabled = $c.IsEnabled; Focusable = $c.IsKeyboardFocusable }
    }
}
$pwd = @($rows | Where-Object { $_.IsPassword })
$btn = @($rows | Where-Object { $_.Type -eq 'Button' -and $_.Name -match '^\s*(log\s*in|sign\s*in)\s*$' })
$version = ''; try { $version = $p.MainModule.FileVersionInfo.FileVersion } catch { }

"NinjaTrader $version  window title: '$($p.MainWindowTitle)'  UI Automation elements: $($all.Count)"
''
$rows | Format-Table -AutoSize | Out-String -Width 200
"password box (IsPassword): $($pwd.Count) found"
"Log In button by name     : $($btn.Count) found"
# Guard-Login's first choice sets the password through the box's ValuePattern (no keyboard). Only ASK
# whether the pattern is offered; nothing is set.
$valueOk = $false
if ($pwd.Count) {
    $pc = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::IsPasswordProperty, $true)
    $vp = $null
    try { $valueOk = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $pc).TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$vp) } catch { }
}
"password box ValuePattern: $(if ($valueOk) { 'offered - the password can be set with no keyboard' } else { 'not offered - the keyboard path (verified foreground) is used' })"
$focusResult = 'not tried'
if ($TryFocus -and $pwd.Count) {
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::IsPasswordProperty, $true)
    try { $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $c).SetFocus(); $focusResult = 'focused OK' }
    catch { $focusResult = "SetFocus failed: $($_.Exception.Message)" }
    "focus the password box   : $focusResult"
}
''
$verdict = $(if ($pwd.Count -ge 1) { 'UIA' } else { 'FALLBACK' })
"VERDICT: $verdict - " + $(if ($verdict -eq 'UIA') { 'Guard-Login focuses the password box through UI Automation.' } else { 'UI Automation cannot see the password box; Guard-Login uses the window''s own focus.' })
if ($OutFile) {
    [ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); ntVersion = $version; title = $p.MainWindowTitle; verdict = $verdict
                passwordBoxes = $pwd.Count; loginButtons = $btn.Count; valuePattern = $valueOk; focus = $focusResult; elements = @($rows) } |
        ConvertTo-Json -Depth 4 | Out-File -FilePath $OutFile -Encoding utf8
    "written: $OutFile"
}
