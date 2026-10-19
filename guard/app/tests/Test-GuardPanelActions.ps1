# The panel's buttons that touch NinjaTrader: Put back now / Bring back on the Strategy backups tab, and
# Test login, Fire drill and Uninstall on Tools. Each asks first, then lists its steps in the action overlay
# as they happen and shows the result. The actions run in-process here with fakes ($script:ActionSeams),
# then once for real through the background runspace (a Test login that refuses before touching anything).
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes 'panelact'
$now = [DateTime]::UtcNow
Save-GuardConfig (Get-GuardDefaults)
$rows = @{}
foreach ($r in @(@{ n = 'Alpha'; s = 'live' }, @{ n = 'Beta'; s = 'missing' }, @{ n = 'Gamma'; s = 'removed' })) {
    $rows["$($r.n)@NinjaTrader.NinjaScript.Strategies.DemoStrategy"] = @{ strategies = @{ Id = 1; Name = $r.n; Classname = 'NinjaTrader.NinjaScript.Strategies.DemoStrategy' }
                                                                       state = $r.s; lastSeenUtc = $now.ToString('o'); charts = @() }
}
[IO.File]::WriteAllText((Join-Path (Get-GuardPath backups) 'rows.json'), (@{ version = 2; utc = $now.ToString('o'); rows = $rows } | ConvertTo-Json -Depth 8))

. (Join-Path $script:AppDir 'Guard-Panel.ps1') -ImportOnly -GuardHome $h.Home -Nt8Dir $h.Nt8
$GuardHome = $h.Home; $Nt8Dir = $h.Nt8
$sargs = @{ Processes = { , @() }; TaskState = { param($n) 'Ready' }; Nt8Version = [version]'8.1.8.3' }
$w = Initialize-Panel -StatusArgs $sargs
function Click([string]$Name) { (Ui $Name).RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Primitives.ButtonBase]::ClickEvent))) }
function Find-Named($Root, [string]$Name) {
    $out = New-Object System.Collections.Generic.List[object]
    $walk = { param($e)
        if ($e -is [Windows.FrameworkElement] -and $e.Name -eq $Name) { $out.Add($e) }
        $n = 0; try { $n = [Windows.Media.VisualTreeHelper]::GetChildrenCount($e) } catch { }
        for ($i = 0; $i -lt $n; $i++) { & $walk ([Windows.Media.VisualTreeHelper]::GetChild($e, $i)) } }
    & $walk $Root
    return $out.ToArray()
}

# The actions run in-process, with fakes; the real background runspace is used once at the end.
$realStart = ${function:Start-PanelAction}
function Start-PanelAction([string]$Label, [scriptblock]$Script, [object[]]$Arguments = @(), [scriptblock]$OnDone) {
    $res = & $Script @Arguments
    & $OnDone $res ''
}
$script:flatNow = [pscustomobject]@{ State = 'flat'; Safe = $true; Reasons = @(); Text = 'Flat: no position, no working order, no strategy running.' }
$script:pyArgs = @()
$script:ActionSeams.putback = @{
    Flat = { $script:flatNow }; IsRunning = { $false }; Kill = { }; Sleep = { param($ms) }
    Python = { param($s, $a, $t) $script:pyArgs = $a
               [pscustomobject]@{ ExitCode = 0; TimedOut = $false; Err = ''
                                  Out = '{"planned":[],"restored":[{"name":"Beta","id":812,"originalId":746}],"refused":[],"notes":[],"relinked":[],"relinkFailed":[],"backup":null}' } }
}

'--- the buttons on each backup row'
$found = @{}
Invoke-PanelOffscreen -Tabs @('Backups') -Action { param($t, $holder) foreach ($b in (Find-Named $holder 'PutBack')) { $found[[string]$b.Tag] = $b } }
Check 'missing: Put back now' ($found.Beta -and $found.Beta.Visibility -eq 'Visible' -and $found.Beta.Content -eq 'Put back now') "$($found.Beta.Visibility) $($found.Beta.Content)"
Check 'removed by you: Bring back' ($found.Gamma -and $found.Gamma.Visibility -eq 'Visible' -and $found.Gamma.Content -eq 'Bring back') "$($found.Gamma.Visibility) $($found.Gamma.Content)"
Check 'on the list: no button' ($found.Alpha -and $found.Alpha.Visibility -ne 'Visible') "$($found.Alpha.Visibility)"
Check 'the missing banner offers them all' ((Ui 'BackupBanner').Visibility -eq 'Visible' -and (Ui 'BackupPutBackAll').Visibility -eq 'Visible') ''
Check 'and no longer sends you to a command line' ((Ui 'BackupBannerText').Text -notmatch 'backup_restore') (Ui 'BackupBannerText').Text

'--- Put back now: asked first, then each step shown'
$found.Beta.RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Primitives.ButtonBase]::ClickEvent)))
Check 'asks first, naming it' ((Ui 'Overlay').Visibility -eq 'Visible' -and (Ui 'OverlayTitle').Text -eq 'Put back Beta now?' -and (Ui 'OverlayOk').Content -eq 'Put back now') (Ui 'OverlayTitle').Text
Check 'and says what will happen' ((Ui 'OverlayText').Text -match 'flat' -and (Ui 'OverlayText').Text -match 'abruptly' -and (Ui 'OverlayText').Text -match 'workspace' -and (Ui 'OverlayText').Text -match 'stay off') (Ui 'OverlayText').Text
Check 'nothing has run yet' ((Ui 'ActionOverlay').Visibility -eq 'Collapsed' -and $script:pyArgs.Count -eq 0) ''
Click 'OverlayOk'
Check 'the action overlay opens' ((Ui 'ActionOverlay').Visibility -eq 'Visible' -and (Ui 'ActionTitle').Text -eq 'Putting back Beta') (Ui 'ActionTitle').Text
$st = @((Ui 'ActionSteps').ItemsSource)
Check 'every step listed and done' ($st.Count -eq 5 -and (($st | ForEach-Object State) -join ',') -eq 'ok,ok,skipped,ok,ok') (($st | ForEach-Object { "$($_.Key)=$($_.State)" }) -join ',')
Check 'the backup tool was asked for Beta' (($script:pyArgs -join ' ') -match '--name Beta') ($script:pyArgs -join ' ')
Check 'the result shown, in green, and Close enabled' ((Ui 'ActionResult').Text -match 'Put back: Beta' -and (Ui 'ActionResult').Foreground -eq (Res 'Live') -and (Ui 'ActionClose').IsEnabled) (Ui 'ActionResult').Text
Check 'and it stays under the tab after closing' ((Ui 'BackupMsg').Text -match 'Put back: Beta') (Ui 'BackupMsg').Text
Click 'ActionClose'
Check 'Close hides the overlay' ((Ui 'ActionOverlay').Visibility -eq 'Collapsed') ''

'--- not flat: refused, shown in amber, nothing run'
$script:flatNow = [pscustomobject]@{ State = 'busy'; Safe = $false; Reasons = @('x'); Text = 'You are not flat: Sim101: Long 2 NQ 12-26.' }
$script:pyArgs = @()
$found.Beta.RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Primitives.ButtonBase]::ClickEvent)))
Click 'OverlayOk'
$st = @((Ui 'ActionSteps').ItemsSource)
Check 'paused first; the check failed; nothing closed or put back; the pause ended' ((($st | ForEach-Object State) -join ',') -eq 'ok,failed,pending,pending,ok' -and $st[1].Detail -match 'Long 2') (($st | ForEach-Object State) -join ',')
Check 'amber, with the reason' ((Ui 'ActionResult').Foreground -eq (Res 'Warn') -and (Ui 'ActionResult').Text -match 'not flat') (Ui 'ActionResult').Text
Check 'nothing put back' ($script:pyArgs.Count -eq 0) ''
Click 'ActionClose'
$script:flatNow = [pscustomobject]@{ State = 'flat'; Safe = $true; Reasons = @(); Text = 'Flat' }

'--- Bring back, and Put back all'
$found.Gamma.RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Primitives.ButtonBase]::ClickEvent)))
Check 'Bring back asks in its own words' ((Ui 'OverlayTitle').Text -eq 'Bring back Gamma?' -and (Ui 'OverlayOk').Content -eq 'Bring back') (Ui 'OverlayTitle').Text
Click 'OverlayOk'
Check 'and brings back that one' (($script:pyArgs -join ' ') -match '--name Gamma') ($script:pyArgs -join ' ')
Click 'ActionClose'
Click 'BackupPutBackAll'
Check 'Put back all asks first' ((Ui 'OverlayTitle').Text -eq 'Put back every missing strategy now?') (Ui 'OverlayTitle').Text
Click 'OverlayCancel'
$forget = $null
Invoke-PanelOffscreen -Tabs @('Backups') -Action { param($t, $holder) $script:forget = @(Find-Named $holder 'Forget' | Where-Object { $_.Tag -eq 'Beta' })[0] }
$script:forget.RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Primitives.ButtonBase]::ClickEvent)))
Check 'Forget names the strategy in its question' ((Ui 'OverlayTitle').Text -eq 'Forget Beta?') (Ui 'OverlayTitle').Text
Click 'OverlayCancel'

'--- Tools'
Click 'ToolTestLogin'
Check 'Test login asks first' ((Ui 'Overlay').Visibility -eq 'Visible' -and (Ui 'OverlayTitle').Text -eq 'Test the login?' -and (Ui 'OverlayText').Text -match 'flat') (Ui 'OverlayTitle').Text
Click 'OverlayCancel'
Click 'ToolDrill'
Check 'the fire drill asks first, saying why you must be flat' ((Ui 'OverlayTitle').Text -eq 'Run the fire drill?' -and (Ui 'OverlayText').Text -match 'switches the strategies on that connection off') (Ui 'OverlayText').Text
Click 'OverlayCancel'

'--- Stop and Start'
$script:ptasks = @{ 'NT8Guard-Relaunch' = 'Ready'; 'NT8Guard-Overwatch' = 'Ready'; 'NT8Guard-LogArchive' = 'Ready' }
$ptState = { param($n) $script:ptasks[$n] }
$ptSet = { param($n, $on) $script:ptasks[$n] = $(if ($on) { 'Ready' } else { 'Disabled' }) }
$noSend = { param($u, $hd, $b, $c) }
$script:ActionSeams.stop = @{ TaskState = $ptState; SetTaskEnabled = $ptSet; Sender = $noSend }
$script:ActionSeams.start = @{ TaskState = $ptState; SetTaskEnabled = $ptSet; RunTask = { param($n) }; WaitSeconds = 0; Sleep = { param($ms) }; Sender = $noSend }
$panelStatus = { Update-PanelStatus (Get-GuardStatus -GuardHome $GuardHome -Nt8Dir $Nt8Dir -Processes { , @() } -TaskState $ptState -Nt8Version ([version]'8.1.8.3')) }
Check 'Tools shows Stop Guard, not Start Guard' ((Ui 'ToolStop').Visibility -eq 'Visible' -and (Ui 'ToolStart').Visibility -eq 'Collapsed') ''
Click 'ToolStop'
Check 'Stop asks first, saying it never ends by itself' ((Ui 'Overlay').Visibility -eq 'Visible' -and (Ui 'OverlayTitle').Text -eq 'Stop NT8 Guard?' -and (Ui 'OverlayText').Text -match 'never ends by itself') (Ui 'OverlayText').Text
Click 'OverlayOk'
Check 'stopped: the tasks are off, the result says how to start it again' ($script:ptasks['NT8Guard-Overwatch'] -eq 'Disabled' -and (Ui 'ActionResult').Text -match 'Start Guard') (Ui 'ActionResult').Text
Check 'and so does the message under the buttons' ((Ui 'ToolPauseMsg').Text -match 'stopped') (Ui 'ToolPauseMsg').Text
(Ui 'ActionOverlay').Visibility = 'Collapsed'
& $panelStatus
Check 'Status header: STOPPED, and Start Guard where the pause buttons were' ((Ui 'OverallText').Text -match '^STOPPED since' -and (Ui 'StopActive').Visibility -eq 'Visible' -and (Ui 'PauseIdle').Visibility -eq 'Collapsed' -and (Ui 'StopText').Text -match '^Guard has been stopped since') "$((Ui 'OverallText').Text) / $((Ui 'StopText').Text)"
Check 'Tools: Start Guard instead of Stop Guard, and no pausing a stopped Guard' ((Ui 'ToolStart').Visibility -eq 'Visible' -and (Ui 'ToolStop').Visibility -eq 'Collapsed' -and -not (Ui 'ToolPause30').IsEnabled) ''
Click 'StartGuard'
Check 'Start asks first, saying a closed NinjaTrader gets started' ((Ui 'OverlayTitle').Text -eq 'Start NT8 Guard?' -and (Ui 'OverlayText').Text -match 'If NinjaTrader is closed') (Ui 'OverlayText').Text
Click 'OverlayOk'
Check 'started: the tasks are on, watching again' ($script:ptasks['NT8Guard-Overwatch'] -eq 'Ready' -and (Ui 'ActionResult').Text -match 'watching again') (Ui 'ActionResult').Text
(Ui 'ActionOverlay').Visibility = 'Collapsed'
& $panelStatus
Check 'back to the pause buttons and Stop Guard' ((Ui 'StopActive').Visibility -eq 'Collapsed' -and (Ui 'PauseIdle').Visibility -eq 'Visible' -and (Ui 'ToolStop').Visibility -eq 'Visible' -and (Ui 'ToolStart').Visibility -eq 'Collapsed' -and (Ui 'ToolPause30').IsEnabled) ''
# Stopped here, then the tasks switched on in Task Scheduler: Guard runs, Tools offers Start Guard to finish.
[IO.File]::WriteAllText((Get-GuardStopPath $GuardHome), '{ "stoppedUtc": "2026-10-01T00:00:00Z" }')
& $panelStatus
Check 'a stop undone outside the panel: Start Guard offered beside Stop Guard' ((Ui 'ToolStart').Visibility -eq 'Visible' -and (Ui 'ToolStop').Visibility -eq 'Visible' -and (Ui 'StopActive').Visibility -eq 'Collapsed') ''
Remove-Item (Get-GuardStopPath $GuardHome)

$menu = New-TestDir startmenu
$script:ActionSeams.uninstall = @{ RemoveTask = { param($n) $true }; StartMenuDir = $menu; PanelPid = 0 }
Click 'ToolUninstall'
Check 'Uninstall asks first' ((Ui 'OverlayTitle').Text -eq 'Uninstall NT8 Guard?') (Ui 'OverlayTitle').Text
Click 'OverlayOk'
Check 'uninstalled: the folder goes when the window closes' ((Ui 'ActionResult').Text -match 'uninstalled' -and $script:CleanupScript -and (Test-Path $script:CleanupScript)) (Ui 'ActionResult').Text
[IO.File]::Delete($script:CleanupScript); $script:CleanupScript = $null       # this test keeps its Guard home
(Ui 'ActionOverlay').Visibility = 'Collapsed'

'--- closing while an action runs is refused'
$script:Action = @{ Label = 'Putting back Beta' }
$w.Close()
Check 'the window stays, the footer says why' ((Ui 'Footer').Text -match "Wait for 'Putting back Beta'") (Ui 'Footer').Text
$script:Action = $null

'--- for real, through the background runspace: Test login with no password refuses before touching anything'
Set-Item function:Start-PanelAction $realStart
$script:Iss = New-PanelSessionState
Click 'ToolTestLogin'
Click 'OverlayOk'
$until = [DateTime]::UtcNow.AddSeconds(60)
while ($script:Action -and [DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 100; Complete-PanelAction; Update-PanelActionSteps }
Check 'it finished' (-not $script:Action) ''
Check 'refused, in words' ((Ui 'ActionResult').Text -match 'Save the password first') (Ui 'ActionResult').Text
Check 'and the Tools card says so too' ((Ui 'ToolLoginMsg').Text -match 'Save the password first') (Ui 'ToolLoginMsg').Text

Complete-Test
