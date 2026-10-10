# The setup wizard in the panel's window (spec 5.1): seven steps with the tab strip hidden, Next saving each
# step's settings, step 3's Next waiting for both add-on lights, the password going straight from its box to
# the encrypted file, the last step installing Guard, and the wizard's pages opening one at a time from Tools.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes 'wizard'
$now = [DateTime]::UtcNow
Save-GuardConfig (Get-GuardDefaults)
. (Join-Path $script:AppDir 'Guard-Panel.ps1') -ImportOnly -GuardHome $h.Home -Nt8Dir $h.Nt8
$GuardHome = $h.Home; $Nt8Dir = $h.Nt8
$w = Initialize-Panel -StatusArgs @{ Processes = { , @() }; TaskState = { param($n) 'Ready' }; Nt8Version = [version]'8.1.8.3' }
function Click([string]$Name) { (Ui $Name).RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Primitives.ButtonBase]::ClickEvent))) }
function Selected { foreach ($k in $script:TabNames.Keys) { if ((Ui $script:TabNames[$k]).IsSelected) { return $k } } }
$script:SetupCheckArgs = @{ Nt8Version = [version]'8.1.8.3'; AutoLogon = { $true }; ScalePercent = { 100 }; OtherWatchdogs = { @() } }
$script:AddonArgs = @{ Processes = { , @([pscustomobject]@{ Id = 7; Title = 'Control Center' }) } }
function Start-PanelAction([string]$Label, [scriptblock]$Script, [object[]]$Arguments = @(), [scriptblock]$OnDone) { $res = & $Script @Arguments; & $OnDone $res '' }

'--- not set up yet'
Check 'the banner says so, on every tab' ((Ui 'SetupBanner').Visibility -eq 'Visible' -and (Ui 'SetupBannerText').Text -match 'step 1 of 7') (Ui 'SetupBannerText').Text

'--- step 1: check this PC'
Click 'SetupContinue'
Check 'the tab strip is hidden, the step bar shown' ((Ui 'Tabs').Tag -eq 'wizard' -and (Ui 'WizardBar').Visibility -eq 'Visible' -and (Ui 'SetupBanner').Visibility -eq 'Collapsed') ''
Check 'step 1 of 7: Check this PC' ((Ui 'WizardStep').Text -eq 'SETUP - STEP 1 OF 7' -and (Ui 'WizardTitle').Text -eq 'Check this PC' -and (Selected) -eq 'Check') "$((Ui 'WizardStep').Text) / $(Selected)"
Check 'six checks listed' (@((Ui 'CheckRows').ItemsSource).Count -eq 6) ''
Check 'Back is off on the first step' (-not (Ui 'WizardBack').IsEnabled) ''
Check 'seven dots, the first current' (((@((Ui 'WizardDots').ItemsSource) | ForEach-Object State) -join ',') -eq 'current,todo,todo,todo,todo,todo,todo') ((@((Ui 'WizardDots').ItemsSource) | ForEach-Object State) -join ',')

'--- step 2: NinjaTrader login'
Click 'WizardNext'
Check 'step 2, remembered in guard.json' ((Selected) -eq 'Login' -and (Get-GuardConfig).setup.step -eq 2) "$(Selected) $((Get-GuardConfig).setup.step)"
Check 'Guard''s own login chosen, no password yet' ((Ui 'LoginBuiltin').IsChecked -and (Ui 'LoginPasswordState').Text -match 'No password') (Ui 'LoginPasswordState').Text
Click 'LoginSavePassword'
Check 'an empty box saves nothing' (-not (Test-GuardPassword) -and (Ui 'LoginPasswordMsg').Text -match 'Type the password') (Ui 'LoginPasswordMsg').Text
$secret = 'Wz-not-real-7731'
(Ui 'LoginPassword').Password = $secret
Click 'LoginSavePassword'
Check 'saved, encrypted, and readable back by this user' ((Test-GuardPassword) -and (Get-GuardPasswordPlain) -eq $secret) ''
Check 'the box is emptied, the state says saved' ((Ui 'LoginPassword').Password -eq '' -and (Ui 'LoginPasswordState').Text -match 'A password is saved') (Ui 'LoginPasswordState').Text
$trail = (Get-ChildItem (Get-GuardPath events), (Get-GuardPath logs) -File -ErrorAction SilentlyContinue | ForEach-Object { Read-AllText $_.FullName }) -join "`n"
Check 'the password is in no event and no log' ($trail -notmatch [regex]::Escape($secret) -and $trail -match 'password saved') ''
(Ui 'LoginOwn').IsChecked = $true; Click 'LoginOwn'
(Ui 'LoginProgram').Text = (Join-Path $h.Home 'missing\NTLogin.exe')
Click 'LoginSave'
Check 'a login program that is not there is refused' ((Ui 'LoginMsg').Text -match 'Not saved' -and (Get-GuardConfig).login.helper -eq 'builtin') (Ui 'LoginMsg').Text
$exe = Join-Path (New-TestDir helper) 'NTLogin.exe'; [IO.File]::WriteAllText($exe, 'x')
(Ui 'LoginProgram').Text = $exe
Click 'LoginSave'
Check 'one that is: saved' ((Get-GuardConfig).login.helper -eq $exe) (Ui 'LoginMsg').Text
(Ui 'LoginBuiltin').IsChecked = $true; Click 'LoginBuiltin'; Click 'LoginSave'
Check 'and back to Guard''s own' ((Get-GuardConfig).login.helper -eq 'builtin') ''

'--- step 3: the add-ons - Next waits for both lights'
Click 'WizardNext'
Check 'step 3, Next held back' ((Selected) -eq 'Addons' -and -not (Ui 'WizardNext').IsEnabled -and (Ui 'WizardMsg').Text -match 'waits') (Ui 'WizardMsg').Text
Click 'WizardNext'
Check 'pressing it anyway does not move on' ((Selected) -eq 'Addons') ''
foreach ($p in @(@{ f = 'AutoReconnect.heartbeat.json'; t = '{"version":"5.0.0"}' }, @{ f = 'NT8Bridge\result\heartbeat.json'; t = '{}' })) {
    $f = Join-Path $h.Nt8 $p.f; New-Item -ItemType Directory -Path (Split-Path $f) -Force | Out-Null; [IO.File]::WriteAllText($f, $p.t)
}
[void](Update-PanelAddons)                     # what the 2-second refresh does on this page
Check 'both reporting: green, Next open' ((Ui 'WizardNext').IsEnabled -and (Ui 'AddonFeedDot').Fill -eq (Res 'Live') -and (Ui 'AddonLinkDot').Fill -eq (Res 'Live')) "$((Ui 'AddonFeedText').Text) / $((Ui 'AddonLinkText').Text)"

'--- steps 4-6: the ordinary tabs, saved by Next'
Click 'WizardNext'
Check 'step 4: the Watch list tab, its own Save bar hidden' ((Selected) -eq 'Watch' -and (Ui 'WatchBar').Visibility -eq 'Collapsed') ''
(Ui 'StaleSeconds').Text = 'soon'
Click 'WizardNext'
Check 'a value that cannot be saved stops Next, said beside it' ((Selected) -eq 'Watch' -and (Ui 'WizardMsg').Text -match 'whole number') (Ui 'WizardMsg').Text
(Ui 'StaleSeconds').Text = '90'
Click 'WizardNext'
Check 'fixed: saved, step 5' ((Selected) -eq 'Alerts' -and (Get-GuardConfig).watch.staleSeconds -eq 90) ''
Check 'phone alerts can be skipped' ((Ui 'WizardSkip').Visibility -eq 'Visible') ''
Click 'WizardSkip'
Check 'step 6: Protection' ((Selected) -eq 'Protection' -and (Ui 'WizardSkip').Visibility -eq 'Collapsed') ''
Click 'PresetHandsoff'
Click 'WizardNext'
Check 'its choice saved, step 7' ((Selected) -eq 'Done' -and (Get-GuardProtection (Get-GuardConfig)).preset -eq 'handsoff' -and (Ui 'WizardNext').Visibility -eq 'Collapsed') ''
Click 'WizardBack'
Check 'Back goes back' ((Selected) -eq 'Protection') ''
Click 'WizardNext'

'--- step 7: Finish setup installs Guard'
$script:reg = @()
$script:ActionSeams.finish = @{ OtherWatchdogs = { @() }; Register = { param($c) $script:reg += $c; [pscustomobject]@{ Ok = $true; Text = 'ok' } }; TestAlert = { [pscustomobject]@{ ok = $true; message = 'sent' } } }
Click 'DoneFinish'
Check 'the three tasks installed, through the action overlay' (($script:reg -join ',') -eq 'relaunch,overwatch,logarchive' -and (Ui 'ActionOverlay').Visibility -eq 'Visible') ($script:reg -join ',')
Check 'setup complete' ((Get-GuardSetupState).Completed -and (Ui 'ActionResult').Text -match 'Setup is finished') (Ui 'ActionResult').Text
Click 'ActionClose'
Click 'DoneStatus'
Check 'Open Status leaves the wizard: tab strip back, no banner' ((Selected) -eq 'Status' -and -not (Ui 'Tabs').Tag -and (Ui 'WizardBar').Visibility -eq 'Collapsed' -and (Ui 'WatchBar').Visibility -eq 'Visible' -and (Ui 'SetupBanner').Visibility -eq 'Collapsed') ''

'--- the pages one at a time, from Tools'
Select-PanelTab Tools
Click 'ToolLogin'
Check 'the login page alone: Back to Tools, no Next' ((Selected) -eq 'Login' -and (Ui 'WizardBack').Content -eq 'Back to Tools' -and (Ui 'WizardNext').Visibility -eq 'Collapsed' -and (Ui 'WizardStep').Text -eq 'SETUP') ''
Click 'WizardBack'
Check 'Back to Tools' ((Selected) -eq 'Tools' -and -not (Ui 'Tabs').Tag) ''
Click 'ToolSetup'
Check 'Run setup starts at step 1' ((Selected) -eq 'Check' -and (Ui 'WizardStep').Text -eq 'SETUP - STEP 1 OF 7') ''
Click 'WizardLater'
Check 'Finish later leaves it' ((Selected) -eq 'Status' -and -not (Ui 'Tabs').Tag) ''

'--- every wizard page renders, and the password is on none of them'
$shots = if ($env:NT8GUARD_SHOT_DIR) { $env:NT8GUARD_SHOT_DIR } else { New-TestDir shots }
(Ui 'LoginPassword').Password = $secret
$files = @(Save-PanelScreenshots -Dir $shots -Tabs Check, Login, Addons, Done)
Check 'four PNGs' ($files.Count -eq 4 -and @($files | Where-Object { (Get-Item $_).Length -gt 15000 }).Count -eq 4) (($files | ForEach-Object { "$(Split-Path $_ -Leaf)=$((Get-Item $_).Length)" }) -join ' ')
Check 'a typed password is not text anyone can read off the page' (@(Get-PanelVisibleText -Tab Login) -notcontains $secret) ''

Complete-Test
