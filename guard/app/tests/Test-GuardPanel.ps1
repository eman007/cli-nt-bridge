# The control panel window, built from fixture files: every tab opens, shows what the files say, the
# buttons that change settings work, and each tab renders to a PNG (spec 10.3). Then the real script's
# -ScreenshotDir mode runs end to end in its own powershell.exe. Set NT8GUARD_SHOT_DIR to keep the PNGs.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$topic = 'guard-secret-topic-123'
$h = Use-TestHomes 'panel'
$now = [DateTime]::UtcNow

function Set-Beat([string]$Path, [int]$AgeSeconds, [string]$Text = 'x') {
    $d = Split-Path $Path -Parent
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Text)
    (Get-Item $Path).LastWriteTimeUtc = $now.AddSeconds(-$AgeSeconds)
}

# ---- a machine with something to show on every tab
$c = Get-GuardDefaults
$c.alerts.ntfyTopic = $topic
Save-GuardConfig $c
Set-Beat (Join-Path (Get-GuardPath state) 'relaunch.heartbeat') 20
Set-Beat (Join-Path (Get-GuardPath state) 'overwatch.heartbeat') 30
Set-Beat (Join-Path (Get-GuardPath secrets) 'nt8.cred') 86400
$hb = [ordered]@{ component = 'AutoReconnect'; version = '5.0.0'; rung = 0; autoRecover = $true; lastL1AgeSeconds = 1; watchdogStaleSeconds = 75
                  watchdogResolved = 'ES 12-26,NQ 12-26'; watchdogConfigured = 'ES ##-##,NQ ##-##'; events = $true; scheduledClosed = $false
                  nextClosure = 'Thanksgiving, Thu 26 Nov: closed all day'; feedProviders = 'Sim Feed'
                  selfChecks = [ordered]@{ feedProvider = 'ok'; rollover = 'nt8'; reEnable = 'ok'; calendar = 'rules' } }
Set-Beat (Join-Path $h.Nt8 'AutoReconnect.heartbeat.json') 4 ($hb | ConvertTo-Json -Depth 4)
$rows = @{}
foreach ($r in @(@{ n = 'Alpha'; s = 'live'; t = 'NQ 1 min' }, @{ n = 'Beta'; s = 'missing'; t = '' }, @{ n = 'Gamma'; s = 'removed'; t = '' })) {
    $rows["$($r.n)@NinjaTrader.NinjaScript.Strategies.DemoStrategy"] = @{
        strategies = @{ Id = 1; Name = $r.n; Classname = 'NinjaTrader.NinjaScript.Strategies.DemoStrategy' }; state = $r.s
        lastSeenUtc = $now.AddMinutes(-7).ToString('o'); labels = @{ accounts = @('Sim101'); instruments = @('NQ 12-26') }
        charts = @($(if ($r.t) { @{ workspace = 'Main'; tabName = $r.t } })) }
}
[IO.File]::WriteAllText((Join-Path (Get-GuardPath backups) 'rows.json'), (@{ version = 2; utc = $now.AddMinutes(-7).ToString('o'); rows = $rows } | ConvertTo-Json -Depth 8))
Write-GuardEvent -Component relaunch -Kind 'nt8.launch' -Severity info -Text 'Started NinjaTrader'
Write-GuardEvent -Component overwatch -Kind 'backups.missing' -Severity warn -Text 'Dropped across a restart: Beta'
Write-GuardEvent -Component overwatch -Kind 'nt8.fault' -Severity alert -Text "frozen; alert sent to https://ntfy.sh/$topic"

. (Join-Path $script:AppDir 'Guard-Panel.ps1') -ImportOnly -GuardHome $h.Home -Nt8Dir $h.Nt8
$GuardHome = $h.Home; $Nt8Dir = $h.Nt8
$procs = { , @([pscustomobject]@{ Id = 7; StartUtc = $now.AddHours(-3); Responding = $true; Title = 'Control Center' }) }
$sargs = @{ Processes = $procs; TaskState = { param($n) 'Ready' }; Nt8Version = [version]'8.1.8.3' }

function Get-VisualTexts($el) {
    $out = New-Object System.Collections.Generic.List[string]
    $walk = {
        param($e)
        if ($e -is [Windows.Controls.TextBlock]) { $out.Add([string]$e.Text) }
        if ($e -is [Windows.Controls.TextBox]) { $out.Add([string]$e.Text) }
        $n = 0; try { $n = [Windows.Media.VisualTreeHelper]::GetChildrenCount($e) } catch { }
        for ($i = 0; $i -lt $n; $i++) { & $walk ([Windows.Media.VisualTreeHelper]::GetChild($e, $i)) }
    }
    & $walk $el
    return $out
}
function Click([string]$Name) { (Ui $Name).RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Primitives.ButtonBase]::ClickEvent))) }

'--- the window builds from the files'
$w = Initialize-Panel -StatusArgs $sargs
Check 'window built' ($w -is [Windows.Window]) ''
# A clean Windows VM (2026-10-10) refused the whole window: its XAML reader called IsReadOnly on a
# TextBox an "unknown member". The XAML sets no IsReadOnly; the code sets it on the property itself.
Check 'the window XAML sets no IsReadOnly attribute (a clean Windows refused it)' ((Get-PanelXaml) -notmatch '<TextBox [^>]*IsReadOnly=') ''
Check 'the masked topic and webhook boxes are still read-only' ((Ui 'NtfyTopicMasked').IsReadOnly -and (Ui 'WebhookMasked').IsReadOnly) ''
$st = Get-GuardStatus -GuardHome $h.Home -Nt8Dir $h.Nt8 @sargs
Check 'the pill shows the overall state' ((Ui 'OverallText').Text -eq $st.Overall.Text) "$((Ui 'OverallText').Text) vs $($st.Overall.Text)"
$shown = @(@((Ui 'Cards').ItemsSource) | ForEach-Object { $_.Cards })
Check 'eight status cards, four to a row' ($shown.Count -eq 8 -and @((Ui 'Cards').ItemsSource).Count -eq 2) "$($shown.Count) in $(@((Ui 'Cards').ItemsSource).Count) rows"
Check 'events listed, newest first' (@((Ui 'Events').ItemsSource)[0].Text -match 'frozen') ''
Check 'an event holding the topic shows it masked' (@((Ui 'Events').ItemsSource)[0].Text -match 'ntfy\.sh/\*\*\*' -and @((Ui 'Events').ItemsSource)[0].Text -notmatch $topic) @((Ui 'Events').ItemsSource)[0].Text
$body = Res 'Body'
Check 'IBM Plex loaded from the fonts folder' ($body.Source -match '^file:///' -and @($body.GetTypefaces()).Count -gt 0) $body.Source

'--- protection'
Check 'the saved preset is selected' ((Ui 'PresetRecover').IsChecked -and -not (Ui 'PresetHandsoff').IsChecked) ''
Check 'Recover NT8: starts NinjaTrader, re-enable is alert only' ((Ui 'Tg_relaunch').IsChecked -and -not (Ui 'Tg_reenableStrategies').IsChecked -and (Ui 'TgText_reenableStrategies').Text -eq 'Alert only') (Ui 'TgText_reenableStrategies').Text
Click 'PresetHandsoff'
Check 'picking Hands-off turns re-enable on' ((Ui 'Tg_reenableStrategies').IsChecked -and (Ui 'PresetHandsoff').IsChecked) ''
(Ui 'Tg_crashDumps').IsChecked = $true; Click 'Tg_crashDumps'
Check 'crash reports are not part of a preset' ((Ui 'CustomNote').Visibility -eq 'Collapsed' -and (Ui 'TgText_crashDumps').Text -eq 'On') (Ui 'CustomNote').Visibility
(Ui 'Tg_relaunch').IsChecked = $false; Click 'Tg_relaunch'
Check 'a switch away from the preset says Custom' ((Ui 'CustomNote').Visibility -eq 'Visible' -and (Ui 'CustomNote').Text -match 'Hands-off') (Ui 'CustomNote').Text
Check 'nothing saved before Save' ((Get-GuardProtection (Get-GuardConfig)).preset -eq 'recover') ''
Click 'ProtSave'
$p = Get-GuardProtection (Get-GuardConfig)
Check 'Save writes exactly what is shown' ($p.preset -eq 'custom' -and $p.reenableStrategies -and -not $p.relaunch -and $p.crashDumps) ($p | ConvertTo-Json -Compress)
Check 'and says so' ((Ui 'ProtMsg').Text -match 'saved') (Ui 'ProtMsg').Text
Check 'the feed guard file follows (re-enable on)' ((Read-AllText (Join-Path $h.Nt8 'AutoReconnect.config.json')) -match '"reEnableAfterRecover": true') ''

'--- alerts'
Check 'the topic is hidden until asked' ((Ui 'NtfyTopicMasked').Visibility -eq 'Visible' -and (Ui 'NtfyTopicMasked').Text -notmatch $topic -and (Ui 'NtfyTopic').Visibility -eq 'Collapsed') (Ui 'NtfyTopicMasked').Text
Click 'TopicShow'
Check 'Show reveals it' ((Ui 'NtfyTopic').Visibility -eq 'Visible' -and (Ui 'NtfyTopic').Text -eq $topic) ''
Click 'TopicShow'
Check 'saved priorities selected' ((Ui 'Prio_info_3').IsChecked -and (Ui 'Prio_warn_4').IsChecked -and (Ui 'Prio_alert_5').IsChecked) ''
(Ui 'Prio_warn_5').IsChecked = $true
Click 'TopicNew'
$newTopic = (Ui 'NtfyTopic').Text
Check 'New topic makes a fresh private one' ($newTopic -match '^nt8guard-[a-z0-9]{22}$' -and $newTopic -ne $topic) $newTopic
Click 'AlertSave'
$cfg = Get-GuardConfig
Check 'Save writes topic and priorities' ($cfg.alerts.ntfyTopic -eq $newTopic -and $cfg.alerts.priorities.warn -eq 5) "$($cfg.alerts.priorities | ConvertTo-Json -Compress)"
Check 'the message never shows the topic' ((Ui 'AlertMsg').Text -notmatch $newTopic) (Ui 'AlertMsg').Text
$topic = $newTopic

'--- strategy backups'
Check 'three rows, missing first' (((@((Ui 'BackupRows').ItemsSource) | ForEach-Object Name) -join ',') -eq 'Beta,Alpha,Gamma') ((@((Ui 'BackupRows').ItemsSource) | ForEach-Object Name) -join ',')
Check 'the missing banner names it' ((Ui 'BackupBanner').Visibility -eq 'Visible' -and (Ui 'BackupBannerText').Text -match 'Beta') (Ui 'BackupBannerText').Text
Check 'summary counts the list' ((Ui 'BackupSummary').Text -match "^1 strategy on NinjaTrader's list") (Ui 'BackupSummary').Text

'--- watch list'
Check 'instruments shown one per line' ((Ui 'Instruments').Text -eq "ES ##-##`r`nNQ ##-##") (Ui 'Instruments').Text
Check 'what the feed guard resolved' ((Ui 'WatchResolved').Text -eq 'ES 12-26, NQ 12-26' -and (Ui 'WatchNext').Text -match 'Thanksgiving') (Ui 'WatchResolved').Text
(Ui 'Instruments').Text = "MNQ ##-##`r`n`r`nMES ##-##"
(Ui 'ExtraDates').Text = '2026-12-31 13:15'
(Ui 'StaleSeconds').Text = '90'
Click 'WatchSave'
$cfg = Get-GuardConfig
Check 'Save writes the list, the dates, the threshold' ((@($cfg.watch.instruments) -join ',') -eq 'MNQ ##-##,MES ##-##' -and @($cfg.watch.extraDates)[0] -eq '2026-12-31 13:15' -and $cfg.watch.staleSeconds -eq 90) ($cfg.watch | ConvertTo-Json -Compress)
(Ui 'ExtraDates').Text = '31/12/2026'
Click 'WatchSave'
Check 'a bad date is refused, in words' ((Ui 'WatchMsg').Text -match "Not saved: '31/12/2026'" -and @((Get-GuardConfig).watch.extraDates)[0] -eq '2026-12-31 13:15') (Ui 'WatchMsg').Text
(Ui 'StaleSeconds').Text = 'soon'
(Ui 'ExtraDates').Text = ''
Click 'WatchSave'
Check 'a non-number is refused' ((Ui 'WatchMsg').Text -match 'whole number') (Ui 'WatchMsg').Text

'--- pause'
Click 'Pause120'
Check 'Pause writes the pause file' ((Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)).Paused) ''
Click 'ToolResume'
Check 'Resume clears it' (-not (Get-GuardPanelPause -NowUtc ([DateTime]::UtcNow)).Paused) ''

'--- a hand-edited guard.json the tabs must still show'
$cfg = Get-GuardConfig
$cfg.alerts.priorities = @{ info = 9; warn = 4; alert = 5 }   # a number guard.json accepts, outside ntfy's 1-5
$cfg.protection = @{ preset = 'custom'; relaunch = $false }
[IO.File]::WriteAllText((Get-GuardConfigPath), ($cfg | ConvertTo-Json -Depth 8))
$w = Initialize-Panel -StatusArgs $sargs
Check 'a priority out of range shows the default' ((Ui 'Prio_info_3').IsChecked -and (Ui 'Prio_warn_4').IsChecked -and (Ui 'Prio_alert_5').IsChecked) ''
Check 'preset "custom" reads as Recover NT8 with changes' ((Ui 'PresetRecover').IsChecked -and (Ui 'CustomNote').Visibility -eq 'Visible' -and (Ui 'CustomNote').Text -match 'Recover NT8' -and -not (Ui 'Tg_relaunch').IsChecked) (Ui 'CustomNote').Text
Click 'ProtSave'
Check 'and saves as a real preset name' ((Get-GuardConfig).protection.preset -eq 'recover' -and (Get-GuardProtection (Get-GuardConfig)).preset -eq 'custom') ((Get-GuardConfig).protection | ConvertTo-Json -Compress)

'--- the webhook address is hidden like the topic'
$hook = 'https://hooks.example.com/notify?key=ZQXJHOOKKEY2'
$cfg = Get-GuardConfig; $cfg.alerts.webhookUrl = $hook; Save-GuardConfig $cfg
$w = Initialize-Panel -StatusArgs $sargs
Check 'hidden until asked, the host still shown' ((Ui 'Webhook').Visibility -eq 'Collapsed' -and (Ui 'WebhookMasked').Visibility -eq 'Visible' -and (Ui 'WebhookMasked').Text -match 'hooks\.example\.com' -and (Ui 'WebhookMasked').Text -notmatch 'ZQXJHOOKKEY2') (Ui 'WebhookMasked').Text
Click 'WebhookShow'
Check 'Show reveals it for editing' ((Ui 'Webhook').Visibility -eq 'Visible' -and (Ui 'Webhook').Text -eq $hook) ''
Click 'WebhookShow'

'--- every tab renders, and no secret is visible on any of them'
$shots = if ($env:NT8GUARD_SHOT_DIR) { $env:NT8GUARD_SHOT_DIR } else { New-TestDir shots }
$files = @(Save-PanelScreenshots -Dir $shots)
Check 'a PNG for each of the six tabs and the four setup pages' ($files.Count -eq 10 -and @($files | Where-Object { (Get-Item $_).Length -gt 15000 }).Count -eq 10) (($files | ForEach-Object { "$(Split-Path $_ -Leaf)=$((Get-Item $_).Length)" }) -join ' ')
Check 'the window is whole again after rendering' ($w.Content -eq (Ui 'Root')) ''
# The text a person can see: each tab laid out as in the screenshots, collapsed elements skipped.
$seen = @{}
foreach ($t in $script:TabNames.Keys) { $seen[$t] = @(Get-PanelVisibleText -Tab $t) }
Check 'the walk reaches into the tabs' ($seen.Alerts -contains 'PHONE (NTFY)' -and $seen.Watch -contains 'INSTRUMENTS TO WATCH') "$($seen.Alerts.Count) texts on Alerts"
$all = (@($seen.Values | ForEach-Object { $_ }) -join "`n")
Check 'the topic is on no tab' ($all -notmatch [regex]::Escape($topic)) ''
Check 'the webhook address is on no tab' ($all -notmatch 'ZQXJHOOKKEY2') ''
Click 'TopicShow'
Check 'and the walk would see the topic once shown' (@(Get-PanelVisibleText -Tab Alerts) -contains $topic) ''
Click 'TopicShow'

'--- a button that fails says so and leaves the window open'
$failing = New-Object Windows.Controls.Button
$failing.Add_Click({ throw 'boom from a test button' })
$frame = New-Object Windows.Threading.DispatcherFrame
[void]$w.Dispatcher.BeginInvoke([Action]{ $failing.RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Primitives.ButtonBase]::ClickEvent))) })
[void]$w.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::ApplicationIdle, [Action]{ $frame.Continue = $false })
$err = ''
try { [Windows.Threading.Dispatcher]::PushFrame($frame) } catch { $err = $_.Exception.Message }
Check 'the error does not escape the window''s message loop' (-not $err) $err
Check 'the footer says what went wrong' ((Ui 'FooterBar').Visibility -eq 'Visible' -and (Ui 'Footer').Text -match 'boom from a test button') (Ui 'Footer').Text
Check 'and it is logged' ((Read-AllText (Join-Path (Get-GuardPath logs) 'panel.log')) -match 'boom from a test button') ''

'--- no fonts folder: the system fonts stand in'
$bare = New-TestDir barepanel
Copy-Item (Join-Path $script:PanelDir '*.xaml') $bare
$ok = $true; try { [void][Windows.Markup.XamlReader]::Parse((Get-PanelXaml -PanelDir $bare)) } catch { $ok = $false; $why = $_.Exception.Message }
Check 'the window still loads' $ok $why

'--- an install path with an ampersand'
$amp = Join-Path (New-TestDir amp) 'Tom & Jerry'
New-Item -ItemType Directory -Path $amp -Force | Out-Null
Copy-Item (Join-Path $script:PanelDir '*') $amp -Recurse
$ok = $true; $why = ''
try { $ampWin = [Windows.Markup.XamlReader]::Parse((Get-PanelXaml -PanelDir $amp)) } catch { $ok = $false; $why = $_.Exception.Message }
Check 'the window loads' $ok $why
Check 'with the fonts from that folder' ($ok -and $ampWin.FindResource('Body').Source -match 'Tom') $(if ($ok) { $ampWin.FindResource('Body').Source } else { '' })

'--- the real script, screenshot mode, in its own process'
$dir = New-TestDir e2e
$out = & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $script:AppDir 'Guard-Panel.ps1') -GuardHome $h.Home -Nt8Dir $h.Nt8 -ScreenshotDir $dir 2>&1
Check 'exits 0' ($LASTEXITCODE -eq 0) ($out | Out-String)
Check 'writes every tab and setup page' (@(Get-ChildItem $dir -Filter *.png).Count -eq 10) (@(Get-ChildItem $dir).Name -join ',')

'--- a panel that cannot start says why, in the log'
$broken = New-TestDir brokenapp
Copy-Item (Join-Path $script:AppDir '*') $broken -Recurse -Exclude tests
Add-Content -Path (Join-Path $broken 'panel\MainWindow.xaml') -Value '<NotClosed>'
$bh = New-TestDir brokenhome
$e2 = New-TestDir e2e2
# Continue here: the child reports on stderr, which 'Stop' would turn into a terminating error in THIS script
$out = & { $ErrorActionPreference = 'Continue'; & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $broken 'Guard-Panel.ps1') -GuardHome $bh -Nt8Dir $h.Nt8 -ScreenshotDir $e2 2>&1 }
Check 'exits 1' ($LASTEXITCODE -eq 1) ($out | Out-String)
Check 'panel.log says it could not start, and why' ((Read-AllText (Join-Path $bh 'logs\panel.log')) -match 'could not start.*(XML|Xaml|xaml|element)') (Read-AllText (Join-Path $bh 'logs\panel.log'))

Complete-Test
