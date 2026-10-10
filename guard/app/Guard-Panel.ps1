# Guard-Panel.ps1 - NT8 Guard's control panel (WPF).
#
#   wscript.exe Guard-Run.vbs Guard-Panel.ps1           no console window (the Start-menu shortcut)
#   powershell -STA -File Guard-Panel.ps1 [-Tab Alerts]
#   powershell -STA -File Guard-Panel.ps1 -Wizard              the setup wizard, at the step you stopped at
#   powershell -STA -File Guard-Panel.ps1 -ScreenshotDir DIR    render every tab and wizard page to DIR\<Tab>.png, no window
#
# The panel only READS what the watchdogs and add-ons write (every 2 seconds, off the window's thread)
# and writes settings, the pause and the backups list on request. It does no watchdog work: closing it
# changes nothing. Long actions run in a background runspace; the window stays responsive.
#
# -ImportOnly defines the functions and returns (the tests build the window from them).
[CmdletBinding()]
param([string]$GuardHome, [string]$Nt8Dir,
      [ValidateSet('Status', 'Protection', 'Alerts', 'Backups', 'Watch', 'Tools')][string]$Tab = 'Status',
      [string]$ScreenshotDir, [switch]$Wizard, [switch]$ImportOnly)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
$script:Modules = foreach ($m in 'Guard.Paths', 'Guard.Log', 'Guard.Config', 'Guard.Events', 'Guard.Alerts', 'Guard.Pause',
                                 'Guard.Start', 'Guard.Overwatch', 'Guard.FeedGuard', 'Guard.Status', 'Guard.Panel',
                                 'Guard.Nt8Link', 'Guard.Password', 'Guard.Actions', 'Guard.Setup') {
    $p = Join-Path $PSScriptRoot "lib\$m.psm1"
    Import-Module $p -DisableNameChecking
    $p
}
$script:PanelDir = Join-Path $PSScriptRoot 'panel'
$script:TabNames = [ordered]@{ Status = 'TabStatus'; Protection = 'TabProtection'; Alerts = 'TabAlerts'; Backups = 'TabBackups'; Watch = 'TabWatch'; Tools = 'TabTools'
                               Check = 'TabCheck'; Login = 'TabLogin'; Addons = 'TabAddons'; Done = 'TabDone' }   # the last four: the setup wizard's own pages
# The setup wizard (spec 5.1): its own pages for steps 1, 2, 3 and 7; the ordinary tabs for 4, 5 and 6.
$script:WizardSteps = @(
    @{ Tab = 'Check'; Title = 'Check this PC' }, @{ Tab = 'Login'; Title = 'NinjaTrader login' }, @{ Tab = 'Addons'; Title = 'NinjaTrader add-ons' },
    @{ Tab = 'Watch'; Title = 'Watch list' }, @{ Tab = 'Alerts'; Title = 'Phone alerts' }, @{ Tab = 'Protection'; Title = 'Protection' },
    @{ Tab = 'Done'; Title = 'Start Guard' })

# Protection switches, in the order of spec section 4. 'locked' = always on, shown but not a switch.
$script:ToggleRows = @(
    @{ Key = 'relaunch'; Title = 'Start NinjaTrader when it closes or crashes'
       Desc = 'Within a minute, through the login. Never while the screen is locked, and it backs off if NinjaTrader keeps failing to stay up.' }
    @{ Key = 'restartWhenFrozen'; Title = 'Restart NinjaTrader when it freezes'
       Desc = 'When it stops responding, or its add-ons stop reporting, for several checks in a row.' }
    @{ Key = 'reconnect'; Title = 'Reconnect dropped connections'
       Desc = 'Only connections that were connected before. One you disconnect yourself stays off.' }
    @{ Key = 'recoverSilentFeed'; Title = 'Recover a silent feed'
       Desc = 'When the watch list goes quiet while the market is open: re-subscribe, then reconnect the connection serving it.' }
    @{ Key = 'locked'; Title = 'Never disconnect an account holding a position'
       Desc = 'A recovery that would disconnect it waits and alerts you instead.' }
    @{ Key = 'restoreStrategies'; Title = 'Put back strategies NinjaTrader dropped'
       Desc = 'NinjaTrader drops switched-off strategies from its list when it closes. Guard puts them back from the backups before it starts NinjaTrader.' }
    @{ Key = 'reattachCharts'; Title = 'Put them back on their charts'
       Desc = 'Each one goes back on the chart tab it sat on.' }
    @{ Key = 'reenableStrategies'; Title = 'Switch strategies back on'
       Desc = 'After a restart or a feed recovery, the ones that were running before.' }
    @{ Key = 'flattenUnprotected'; Title = 'Close an unprotected position first'
       Desc = 'Before switching strategies back on, close a position on that account that has no stop.' }
    @{ Key = 'crashDumps'; Title = 'Save a crash report when NinjaTrader freezes'
       Desc = 'A memory dump for NinjaTrader support, 3 to 6 GB each; the last 2 are kept. Off by default.' }
)
$script:PriorityRows = @(
    @{ Key = 'info'; Title = 'Information'; Desc = 'a restart worked, a pause ended' }
    @{ Key = 'warn'; Title = 'Warning'; Desc = 'something needs a look' }
    @{ Key = 'alert'; Title = 'Alert'; Desc = 'act now: NinjaTrader down, a position without a stop' }
)

function ConvertTo-XamlText([string]$s) { return [Security.SecurityElement]::Escape($s) }

function Get-ProtectionRowsXaml {
    $out = New-Object Text.StringBuilder
    for ($i = 0; $i -lt $script:ToggleRows.Count; $i++) {
        $r = $script:ToggleRows[$i]
        $sep = $(if ($i -lt $script:ToggleRows.Count - 1) { 1 } else { 0 })
        $right = $(if ($r.Key -eq 'locked') {
            '<TextBlock Text="Always on" Foreground="{StaticResource Live}" FontSize="12.5" VerticalAlignment="Center"/>'
        } else {
            "<TextBlock x:Name=`"TgText_$($r.Key)`" Width=`"80`" TextAlignment=`"Right`" Margin=`"0,0,12,0`" VerticalAlignment=`"Center`" Foreground=`"{StaticResource Muted}`" FontSize=`"12.5`"/>" +
            "<CheckBox x:Name=`"Tg_$($r.Key)`" Style=`"{StaticResource Switch}`"/>"
        })
        [void]$out.Append(@"
<Border BorderBrush="{StaticResource Line}" BorderThickness="0,0,0,$sep" Padding="18,13">
  <DockPanel>
    <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Center">$right</StackPanel>
    <StackPanel Margin="0,0,16,0">
      <TextBlock Text="$(ConvertTo-XamlText $r.Title)" Foreground="{StaticResource FgStrong}" FontSize="14" FontWeight="Medium"/>
      <TextBlock Text="$(ConvertTo-XamlText $r.Desc)" Style="{StaticResource Help}" Margin="0,3,0,0"/>
    </StackPanel>
  </DockPanel>
</Border>
"@)
    }
    return $out.ToString()
}

function Get-PriorityRowsXaml {
    $out = New-Object Text.StringBuilder
    foreach ($r in $script:PriorityRows) {
        $seg = (1..5 | ForEach-Object { "<RadioButton x:Name=`"Prio_$($r.Key)_$_`" GroupName=`"Prio_$($r.Key)`" Content=`"$_`" Style=`"{StaticResource Seg}`"/>" }) -join ''
        [void]$out.Append(@"
<DockPanel Margin="0,4">
  <TextBlock Text="$(ConvertTo-XamlText $r.Title)" Width="120" VerticalAlignment="Center" Foreground="{StaticResource FgStrong}"/>
  <StackPanel Orientation="Horizontal">$seg</StackPanel>
  <TextBlock Text="$(ConvertTo-XamlText $r.Desc)" Style="{StaticResource Help}" VerticalAlignment="Center" Margin="10,0,0,0"/>
</DockPanel>
"@)
    }
    return $out.ToString()
}

function Get-PanelXaml {
    # MainWindow.xaml with the theme pasted in, the fonts folder's address filled in, and the generated rows.
    [CmdletBinding()] param([string]$PanelDir = $script:PanelDir)
    $theme = [IO.File]::ReadAllText((Join-Path $PanelDir 'Theme.xaml'))
    $inner = [regex]::Match($theme, '(?s)<ResourceDictionary[^>]*>(.*)</ResourceDictionary>').Groups[1].Value
    $fonts = Join-Path $PanelDir 'fonts'
    if (Test-Path (Join-Path $fonts '*.ttf')) {
        # escaped: the address goes into XML text, and a folder may be named 'Tom & Jerry'
        $inner = $inner.Replace('{FONTBASE}', [Security.SecurityElement]::Escape(([Uri]((Resolve-Path $fonts).Path.TrimEnd('\') + '\')).AbsoluteUri))
    } else {
        $inner = [regex]::Replace($inner, '\{FONTBASE\}#[^,<]+,\s*', '')    # no fonts folder: Segoe UI / Consolas
    }
    $win = [IO.File]::ReadAllText((Join-Path $PanelDir 'MainWindow.xaml'))
    return $win.Replace('<!--THEME-->', $inner).Replace('<!--PROTECTION_ROWS-->', (Get-ProtectionRowsXaml)).Replace('<!--PRIORITY_ROWS-->', (Get-PriorityRowsXaml))
}

function New-PanelWindow { return [Windows.Markup.XamlReader]::Parse((Get-PanelXaml)) }

# Not C / R: 'r' is PowerShell's alias for Invoke-History, and an alias wins over a function.
function Ui([string]$Name) { return $script:Win.FindName($Name) }
function Res([string]$Key) { return $script:Win.FindResource($Key) }

function Find-PanelChild($Element, [type]$Type) {
    $n = [Windows.Media.VisualTreeHelper]::GetChildrenCount($Element)
    for ($i = 0; $i -lt $n; $i++) {
        $c = [Windows.Media.VisualTreeHelper]::GetChild($Element, $i)
        if ($c -is $Type) { return $c }
        $hit = Find-PanelChild $c $Type
        if ($hit) { return $hit }
    }
    return $null
}

function Set-PanelFooter([string]$Text) {
    (Ui 'Footer').Text = $Text
    (Ui 'FooterBar').Visibility = $(if ($Text) { 'Visible' } else { 'Collapsed' })
}

function Set-PanelMessage([string]$Name, [string]$Text, [ValidateSet('ok', 'bad', 'info')][string]$Kind = 'info') {
    $t = Ui $Name
    $t.Text = $Text
    $t.Foreground = Res $(switch ($Kind) { 'ok' { 'Live' } 'bad' { 'Danger' } default { 'Fg' } })
}

# ------------------------------------------------------------------ reading: status, backups, watch list
function Update-PanelStatus($Status) {
    $tone = @{ ok = 'Live'; warn = 'Warn'; bad = 'Danger'; paused = 'Accent' }[[string]$Status.Overall.State]
    (Ui 'OverallText').Text = $Status.Overall.Text
    (Ui 'OverallText').Foreground = Res $tone
    (Ui 'OverallDot').Fill = Res $tone
    (Ui 'OverallPill').BorderBrush = Res $tone
    (Ui 'OverallPill').Background = Res "${tone}Soft"
    $b = @($Status.Banner)
    (Ui 'Banner').Visibility = $(if ($b.Count) { 'Visible' } else { 'Collapsed' })
    (Ui 'BannerItems').ItemsSource = $b

    $script:LastCards = @($Status.Cards)
    Show-PanelCards
    $esig = (@($Status.Events | ForEach-Object { "$($_.Utc)|$($_.Text)" })) -join "`n"
    if ($esig -ne $script:EventSig) { $script:EventSig = $esig; (Ui 'Events').ItemsSource = @($Status.Events) }
    (Ui 'EventsEmpty').Visibility = $(if (@($Status.Events).Count) { 'Collapsed' } else { 'Visible' })

    $paused = [bool]$Status.Pause.Paused
    (Ui 'PauseIdle').Visibility = $(if ($paused) { 'Collapsed' } else { 'Visible' })
    (Ui 'PauseActive').Visibility = $(if ($paused) { 'Visible' } else { 'Collapsed' })
    if ($paused) {
        $until = Format-GuardTime -Utc $Status.Pause.UntilUtc -NowUtc $Status.NowUtc
        (Ui 'PauseText').Text = "Paused until $until" + $(if ($Status.Pause.Reason) { " - $($Status.Pause.Reason)" } else { '' })
    }
    (Ui 'ToolResume').IsEnabled = $paused
    (Ui 'Updated').Text = 'Updated ' + [TimeZoneInfo]::ConvertTimeFromUtc($Status.NowUtc, [TimeZoneInfo]::Local).ToString('HH:mm:ss')
}

function Update-PanelBackups($List, $Status) {
    $rows = @($List.Rows)
    (Ui 'BackupRows').ItemsSource = $rows
    (Ui 'BackupEmpty').Visibility = $(if ($rows.Count) { 'Collapsed' } else { 'Visible' })
    if ($List.Error -and $List.Error -ne 'none') {
        (Ui 'BackupSummary').Text = 'The backup list could not be read'
        (Ui 'BackupEmpty').Text = $List.Error
    } else {
        $live = @($rows | Where-Object State -eq 'live').Count
        $when = $(if ($List.SavedUtc) { ' - last saved ' + (Format-GuardTime -Utc $List.SavedUtc -NowUtc ([DateTime]::UtcNow)) } else { '' })
        (Ui 'BackupSummary').Text = "$(Format-GuardCount $live strategy strategies) on NinjaTrader's list$when"
    }
    $miss = @($rows | Where-Object State -eq 'missing')
    (Ui 'BackupBanner').Visibility = $(if ($miss.Count) { 'Visible' } else { 'Collapsed' })
    if ($miss.Count) {
        (Ui 'BackupBannerText').Text = "$($miss.Count) missing from NinjaTrader's list: $(@($miss.Name) -join ', '). " + (Get-BackupAdvice -Config $script:Config -Kind missing)
    }
}

function Update-PanelWatchInfo($Info) {
    (Ui 'WatchResolved').Text = $(if (@($Info.Resolved).Count) { @($Info.Resolved) -join ', ' } else { '-' })
    (Ui 'WatchProviders').Text = $(if ($Info.Providers) { $Info.Providers } else { '-' })
    (Ui 'WatchNext').Text = $(if ($Info.NextClosure) { $Info.NextClosure } else { '-' })
    (Ui 'WatchNote').Text = $Info.Note
}

function Show-PanelCards {
    # The cards cut into rows of $script:CardCols, so each row is only as tall as its own tallest card.
    $cols = [int]$script:CardCols
    if ($cols -lt 1) { $cols = 4 }
    $cards = @($script:LastCards)
    $sig = "$cols`n" + ((@($cards | ForEach-Object { "$($_.Key)|$($_.State)|$($_.Headline)|$($_.Detail)|$($_.Remedy)" })) -join "`n")
    if ($sig -eq $script:CardSig) { return }
    $script:CardSig = $sig
    $rows = for ($i = 0; $i -lt $cards.Count; $i += $cols) {
        [pscustomobject]@{ Cols = $cols; Cards = @($cards[$i..([math]::Min($i + $cols, $cards.Count) - 1)]) }
    }
    (Ui 'Cards').ItemsSource = @($rows)
}

function Update-PanelColumns {
    $w = (Ui 'Root').ActualWidth
    if ($w -le 0) { return }
    $cols = $(if ($w -ge 1060) { 4 } else { 2 })
    if ($cols -ne $script:CardCols) { $script:CardCols = $cols; Show-PanelCards }
}

# ------------------------------------------------------------------ settings shown in the tabs
function Show-PanelProtection([hashtable]$Protection) {
    $script:ProtEdit = @{}
    foreach ($k in $Protection.Keys) { $script:ProtEdit[$k] = $Protection[$k] }
    # 'custom' (or anything else) as the stored preset: switches on top of Recover NT8, as Get-GuardProtection reads it
    if (@('watch', 'recover', 'handsoff') -notcontains [string]$script:ProtEdit.preset) { $script:ProtEdit.preset = 'recover' }
    Update-PanelProtectionView
}

function Update-PanelProtectionView {
    $eff = Get-GuardProtection @{ protection = $script:ProtEdit }
    $named = [string]$script:ProtEdit.preset
    (Ui 'PresetWatch').IsChecked = ($named -eq 'watch')
    (Ui 'PresetRecover').IsChecked = ($named -eq 'recover')
    (Ui 'PresetHandsoff').IsChecked = ($named -eq 'handsoff')
    $names = @{ watch = 'Watch only'; recover = 'Recover NT8'; handsoff = 'Hands-off' }
    (Ui 'CustomNote').Visibility = $(if ($eff.preset -eq 'custom') { 'Visible' } else { 'Collapsed' })
    (Ui 'CustomNote').Text = "Custom: some switches below differ from $($names[$named]). Choosing a preset resets them."
    foreach ($r in $script:ToggleRows) {
        if ($r.Key -eq 'locked') { continue }
        $on = [bool]$eff[$r.Key]
        (Ui "Tg_$($r.Key)").IsChecked = $on
        (Ui "TgText_$($r.Key)").Text = $(if ($on) { 'On' } elseif ($r.Key -eq 'crashDumps') { 'Off' } else { 'Alert only' })
        (Ui "TgText_$($r.Key)").Foreground = Res $(if ($on) { 'Live' } else { 'Muted' })
    }
}

function Show-PanelAlerts([hashtable]$Alerts) {
    (Ui 'NtfyServer').Text = [string]$Alerts.ntfyServer
    (Ui 'NtfyTopic').Text = [string]$Alerts.ntfyTopic
    (Ui 'Webhook').Text = [string]$Alerts.webhookUrl
    foreach ($k in 'info', 'warn', 'alert') {
        $v = [int]$Alerts.priorities[$k]
        if ($v -lt 1 -or $v -gt 5) { $v = @{ info = 3; warn = 4; alert = 5 }[$k] }
        (Ui "Prio_${k}_$v").IsChecked = $true
    }
    Set-PanelTopicVisible $false
    Set-PanelWebhookVisible $false
    Hide-PanelQr
}

function Set-PanelTopicVisible([bool]$Visible) {
    $t = [string](Ui 'NtfyTopic').Text
    (Ui 'NtfyTopicMasked').Text = $(if (-not $t) { '(none yet - press New topic)' }
                                   elseif ($t -like 'nt8guard-*') { 'nt8guard-' + ([string][char]0x2022 * 10) + '  (hidden)' }
                                   else { $t.Substring(0, [math]::Min(3, $t.Length)) + ([string][char]0x2022 * 10) + '  (hidden)' })
    (Ui 'NtfyTopic').Visibility = $(if ($Visible) { 'Visible' } else { 'Collapsed' })
    (Ui 'NtfyTopicMasked').Visibility = $(if ($Visible) { 'Collapsed' } else { 'Visible' })
    (Ui 'TopicShow').Content = $(if ($Visible) { 'Hide' } else { 'Show' })
}

function Set-PanelWebhookVisible([bool]$Visible) {
    # A webhook address carries its key in the path or query (Slack, Discord, Google Chat): show only the
    # scheme and host until asked. Nothing to hide when it is empty, so the box is open for typing.
    $t = ([string](Ui 'Webhook').Text).Trim()
    if (-not $t) { $Visible = $true }
    $u = $null
    $shown = $(if ([Uri]::TryCreate($t, 'Absolute', [ref]$u)) { "$($u.Scheme)://$($u.Host)/" } else { '' })
    (Ui 'WebhookMasked').Text = $shown + ([string][char]0x2022 * 10) + '  (hidden)'
    (Ui 'Webhook').Visibility = $(if ($Visible) { 'Visible' } else { 'Collapsed' })
    (Ui 'WebhookMasked').Visibility = $(if ($Visible) { 'Collapsed' } else { 'Visible' })
    (Ui 'WebhookShow').Content = $(if ($Visible) { 'Hide' } else { 'Show' })
    (Ui 'WebhookShow').IsEnabled = [bool]$t
}

function Hide-PanelQr {
    (Ui 'QrImage').Source = $null
    (Ui 'QrTile').Visibility = 'Collapsed'
    (Ui 'QrHint').Visibility = 'Visible'
    (Ui 'QrShow').Content = 'Show code'
}

function Show-PanelWatch([hashtable]$Watch) {
    (Ui 'Instruments').Text = (@($Watch.instruments) -join "`r`n")
    (Ui 'StaleSeconds').Text = [string]$Watch.staleSeconds
    (Ui 'ExtraDates').Text = (@($Watch.extraDates) -join "`r`n")
}

function Show-PanelSettings {
    $script:Config = Get-GuardConfig $GuardHome
    Show-PanelProtection $script:Config.protection
    Show-PanelAlerts $script:Config.alerts
    Show-PanelWatch $script:Config.watch
}

# Each tab saves only its own section, laid on a FRESH copy of guard.json: unsaved edits on another tab
# are neither saved nor lost by it.
function Read-PanelAlerts([hashtable]$Config) {
    $Config.alerts.ntfyServer = ([string](Ui 'NtfyServer').Text).Trim()
    $Config.alerts.ntfyTopic = ([string](Ui 'NtfyTopic').Text).Trim()
    $Config.alerts.webhookUrl = ([string](Ui 'Webhook').Text).Trim()
    $pr = @{}
    foreach ($k in 'info', 'warn', 'alert') { $pr[$k] = @(1..5 | Where-Object { (Ui "Prio_${k}_$_").IsChecked })[0] }
    $Config.alerts.priorities = $pr
    return $Config
}

function Save-PanelSection([ValidateSet('Protection', 'Alerts', 'Watch list')][string]$Section) {
    # $true when saved. In the setup wizard the tab's own Save bar is hidden (Next saves), so the message
    # is shown beside Next as well.
    $cfg = Get-GuardConfig $GuardHome
    $msgName = @{ 'Protection' = 'ProtMsg'; 'Alerts' = 'AlertMsg'; 'Watch list' = 'WatchMsg' }[$Section]
    $say = { param($t, $k) Set-PanelMessage $msgName $t $k; if ($script:WizardMode -eq 'wizard') { Set-PanelMessage 'WizardMsg' $t $k } }
    switch ($Section) {
        'Protection' { $cfg.protection = @{}; foreach ($k in $script:ProtEdit.Keys) { $cfg.protection[$k] = $script:ProtEdit[$k] } }
        'Alerts' { $cfg = Read-PanelAlerts $cfg }
        'Watch list' {
            $inst = @(ConvertFrom-GuardListText (Ui 'Instruments').Text)
            $dates = @(ConvertFrom-GuardListText (Ui 'ExtraDates').Text)
            $stale = 0
            if (-not [int]::TryParse(([string](Ui 'StaleSeconds').Text).Trim(), [ref]$stale)) {
                & $say 'Not saved: "Silent after" must be a whole number of seconds.' bad; return $false
            }
            $p = @(Test-GuardWatchEntries -Instruments $inst -ExtraDates $dates)
            if ($p.Count) { & $say ('Not saved: ' + ($p -join '; ')) bad; return $false }
            $cfg.watch.instruments = $inst; $cfg.watch.extraDates = $dates; $cfg.watch.staleSeconds = $stale
        }
    }
    $r = Save-GuardPanelSettings -Config $cfg -Section $Section -GuardHome $GuardHome -Nt8Dir $Nt8Dir
    & $say $r.Message $(if ($r.Ok) { 'ok' } else { 'bad' })
    if ($r.Ok) { $script:Config = Get-GuardConfig $GuardHome; $script:NextRefresh = [DateTime]::MinValue }
    return [bool]$r.Ok
}

# ------------------------------------------------------------------ background work
function New-PanelSessionState {
    $iss = [Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    $iss.ExecutionPolicy = [Microsoft.PowerShell.ExecutionPolicy]::Bypass
    $iss.ImportPSModule([string[]]$script:Modules)
    return $iss
}

function Start-PanelAction([string]$Label, [scriptblock]$Script, [object[]]$Arguments = @(), [scriptblock]$OnDone) {
    # One action at a time, in its own runspace; Step-PanelTimer hands the result to $OnDone on this thread.
    if ($script:Action) { Set-PanelFooter "Still busy: $($script:Action.Label). Try again when it finishes."; return }
    $rs = [runspacefactory]::CreateRunspace($script:Iss)
    $rs.Open()
    $ps = [powershell]::Create()
    $ps.Runspace = $rs
    [void]$ps.AddScript($Script)
    foreach ($a in $Arguments) { [void]$ps.AddArgument($a) }
    $script:Action = @{ Label = $Label; Ps = $ps; Rs = $rs; Async = $ps.BeginInvoke(); OnDone = $OnDone }
    Set-PanelFooter "$Label ..."
}

function Complete-PanelAction {
    $a = $script:Action
    if (-not $a -or -not $a.Async.IsCompleted) { return }
    $script:Action = $null
    $result = $null; $err = ''
    try {
        $result = @($a.Ps.EndInvoke($a.Async))
        if ($a.Ps.Streams.Error.Count) { $err = $a.Ps.Streams.Error[0].ToString() }
    } catch { $err = $_.Exception.Message }
    try { $a.Ps.Dispose(); $a.Rs.Dispose() } catch { }
    Set-PanelFooter ''
    try { & $a.OnDone $result $err } catch { Set-PanelFooter "Could not show the result: $($_.Exception.Message)" }
    $script:NextRefresh = [DateTime]::MinValue
}

function Step-PanelRefresh {
    # Every 2 s: read the status in the background runspace, then show it here.
    $j = $script:Refresh
    if ($j) {
        if (-not $j.Async.IsCompleted) { return }
        $script:Refresh = $null
        try {
            $r = @($j.Ps.EndInvoke($j.Async))[0]
            if ($r) {
                Update-PanelStatus $r.Status; Update-PanelBackups $r.Backups $r.Status; Update-PanelWatchInfo $r.Watch
                Update-PanelSetupBanner $r.Setup
                if ($script:WizardMode -and (Ui 'TabAddons').IsSelected) { [void](Update-PanelAddons) }   # the lights follow the add-ons' first report
            }
            elseif ($j.Ps.Streams.Error.Count) { Set-PanelFooter "Could not read the status: $($j.Ps.Streams.Error[0])" }
        } catch { Set-PanelFooter "Could not read the status: $($_.Exception.Message)" }
        finally { $j.Ps.Dispose() }
        $script:NextRefresh = [DateTime]::UtcNow.AddSeconds(2)
        return
    }
    if ([DateTime]::UtcNow -lt $script:NextRefresh) { return }
    $ps = [powershell]::Create()
    $ps.Runspace = $script:RefreshRs
    [void]$ps.AddScript({
        param($gh, $nd)
        $cfg = Get-GuardConfig $gh
        [pscustomobject]@{ Status = (Get-GuardStatus -GuardHome $gh -Nt8Dir $nd -Config $cfg)
                           Backups = (Get-GuardBackupList -GuardHome $gh)
                           Watch = (Get-GuardWatchInfo -GuardHome $gh -Nt8Dir $nd -Config $cfg)
                           Setup = (Get-GuardSetupState -GuardHome $gh) }
    }).AddArgument($GuardHome).AddArgument($Nt8Dir)
    $script:Refresh = @{ Ps = $ps; Async = $ps.BeginInvoke() }
}

function Step-PanelTimer { Complete-PanelAction; Update-PanelActionSteps; Step-PanelRefresh }

# ------------------------------------------------------------------ the buttons that touch NinjaTrader
# Put back now / Bring back, Test login, Fire drill, Uninstall (Guard.Actions). Each runs in the background
# runspace while the action overlay lists its steps as they happen, then shows the result. The steps come
# back through a synchronized hashtable the action writes and the timer reads - never a scriptblock handed
# across runspaces. Tests put fakes in $script:ActionSeams.<action> and run the action in-process.
$script:ActionSeams = @{}
$script:ActionFns = @{ putback = 'Invoke-GuardPutBack'; testlogin = 'Invoke-GuardTestLogin'; drill = 'Invoke-GuardFireDrill'; uninstall = 'Invoke-GuardUninstall'
                       finish = 'Invoke-GuardSetupFinish' }

function Start-PanelRiskyAction([string]$Action, [string]$Title, [hashtable]$Params, [string]$MsgName) {
    if ($script:Action) { Set-PanelFooter "Still busy: $($script:Action.Label). Try again when it finishes."; return }
    $script:ActionProgress = [hashtable]::Synchronized(@{})
    $script:ActionView = @(Get-GuardActionSteps $Action | ForEach-Object { [pscustomobject]@{ Key = $_.Key; Text = $_.Text; State = 'pending'; Detail = '' } })
    $script:ActionSig = $null
    $script:ActionMsg = $MsgName
    (Ui 'ActionTitle').Text = $Title
    (Ui 'ActionResult').Text = ''
    (Ui 'ActionClose').IsEnabled = $false
    (Ui 'ActionSteps').ItemsSource = $script:ActionView
    (Ui 'ActionOverlay').Visibility = 'Visible'
    $seams = $(if ($script:ActionSeams.ContainsKey($Action)) { $script:ActionSeams[$Action] } else { @{} })
    Start-PanelAction $Title {
        param($fn, $p, $prog, $seams)
        $report = { param($k, $s, $t) $prog[$k] = @{ State = $s; Detail = [string]$t } }
        & $fn @p -Progress $report @seams
    } @($script:ActionFns[$Action], $Params, $script:ActionProgress, $seams) { param($res, $err) Show-PanelActionResult $res $err }
}

function Update-PanelActionSteps {
    # The overlay's step list from what the action reported so far (read under the hashtable's own lock).
    $prog = $script:ActionProgress
    if (-not $script:ActionView -or -not $prog) { return }
    $snap = @{}
    [Threading.Monitor]::Enter($prog.SyncRoot)
    try { foreach ($k in @($prog.Keys)) { $snap[$k] = $prog[$k] } } finally { [Threading.Monitor]::Exit($prog.SyncRoot) }
    $sig = (@($snap.Keys | Sort-Object | ForEach-Object { "$_=$($snap[$_].State)|$($snap[$_].Detail)" })) -join ';'
    if ($sig -eq $script:ActionSig) { return }
    $script:ActionSig = $sig
    $script:ActionView = @($script:ActionView | ForEach-Object {
        $p = $snap[$_.Key]
        [pscustomobject]@{ Key = $_.Key; Text = $_.Text; State = $(if ($p) { $p.State } else { 'pending' }); Detail = $(if ($p) { $p.Detail } else { '' }) } })
    (Ui 'ActionSteps').ItemsSource = $script:ActionView
}

function Show-PanelActionResult($Res, [string]$Err) {
    Update-PanelActionSteps
    $r = @(@($Res) | Where-Object { $_ -and $_.PSObject.Properties['Message'] }) | Select-Object -Last 1
    if ($Err -or -not $r) { $text = "It stopped with an error: $(if ($Err) { $Err } else { 'no result' })"; $ok = $false; $tone = 'Danger' }
    else { $text = [string]$r.Message; $ok = [bool]$r.Ok; $tone = $(if ($ok) { 'Live' } elseif ($r.Result -eq 'refused') { 'Warn' } else { 'Danger' }) }
    try { $text = Hide-GuardSecret -Text $text -Config $script:Config } catch { }
    (Ui 'ActionResult').Text = $text
    (Ui 'ActionResult').Foreground = Res $tone
    (Ui 'ActionClose').IsEnabled = $true
    if ($script:ActionMsg) { Set-PanelMessage $script:ActionMsg $text $(if ($ok) { 'ok' } else { 'bad' }) }
    if ($r -and $r.PSObject.Properties['CleanupScript'] -and $r.CleanupScript) { $script:CleanupScript = [string]$r.CleanupScript }
}

function Confirm-PanelTestLogin([string]$MsgName) {
    $script:ConfirmMsg = $MsgName        # read by the OK handler (not a closure: see Forget below)
    Show-PanelConfirm 'Test the login?' ("Guard starts NinjaTrader and logs in the way it does after a crash, and shows what happened. If NinjaTrader is running, Guard first checks with the NT8 link that you are flat, then closes it abruptly " +
                                         "(a normal close drops switched-off strategies). Strategies you switched off to be flat stay off. Guard is paused while it runs.") 'Test login' {
        Start-PanelRiskyAction testlogin 'Testing the login' @{ Config = (Get-GuardConfig $GuardHome); GuardHome = $GuardHome; Nt8Dir = $Nt8Dir } $script:ConfirmMsg
    }
}

function Confirm-PanelDrill([string]$MsgName) {
    $script:ConfirmMsg = $MsgName
    $stale = [int](Get-GuardConfig $GuardHome).watch.staleSeconds
    Show-PanelConfirm 'Run the fire drill?' ("For about $([math]::Ceiling(($stale + 60) / 60) + 1) minutes the feed guard ignores the market data, as if the feed died. It should notice, re-subscribe, then disconnect and reconnect the connection serving your watch list - " +
                                             "NinjaTrader switches the strategies on that connection off, so Guard first checks with the NT8 link that you are flat. Strategies you switched off to be flat stay off. Your phone gets the alerts a real outage sends.") 'Run the drill' {
        Start-PanelRiskyAction drill 'Fire drill' @{ Config = (Get-GuardConfig $GuardHome); GuardHome = $GuardHome; Nt8Dir = $Nt8Dir } $script:ConfirmMsg
    }
}

function Confirm-PanelPutBack([string[]]$Names, [bool]$Bring) {
    $script:PutBackNames = @($Names)     # read by the OK handler (not a closure: see Forget below)
    $what = $(if (@($Names).Count) { @($Names) -join ', ' } else { 'every missing strategy' })
    $title = $(if ($Bring) { "Bring back ${what}?" } else { "Put back $what now?" })    # ${}: '?' is legal in a variable name
    $text = "Guard pauses itself and checks with the NT8 link that you are flat (no position, no working order, no strategy running), and stops if it cannot tell. " +
            "Then it closes NinjaTrader abruptly - on purpose: a normal close drops switched-off strategies from its list - " +
            "puts $what back from the backup and onto its chart, and lets NinjaTrader start again. " +
            "Strategies you switched off to be flat stay off: switch them on again yourself. " +
            "If you changed charts since NinjaTrader started, save the workspace in NinjaTrader first."
    Show-PanelConfirm $title $text $(if ($Bring) { 'Bring back' } else { 'Put back now' }) {
        Start-PanelRiskyAction putback $(if (@($script:PutBackNames).Count) { "Putting back $(@($script:PutBackNames) -join ', ')" } else { 'Putting back the missing strategies' }) `
            @{ Names = @($script:PutBackNames); Config = (Get-GuardConfig $GuardHome); GuardHome = $GuardHome; Nt8Dir = $Nt8Dir } 'BackupMsg'
    }
}

# ------------------------------------------------------------------ the setup wizard (spec 5.1)
# Seven steps; Next saves the step's settings (the tab's own Save bar is hidden). The tab strip is hidden
# while it runs. The wizard's own pages also open one at a time from Tools ('page' mode: Back to Tools).
# Tests put fakes for the checks in $script:SetupCheckArgs / $script:AddonArgs (see Guard.Setup).
$script:SetupCheckArgs = @{}
$script:AddonArgs = @{}

function Set-PanelWizardChrome([bool]$On) {
    (Ui 'Tabs').Tag = $(if ($On) { 'wizard' } else { $null })
    foreach ($n in 'WizardBar', 'WizardNav') { (Ui $n).Visibility = $(if ($On) { 'Visible' } else { 'Collapsed' }) }
    foreach ($n in 'ProtBar', 'AlertBar', 'WatchBar') { (Ui $n).Visibility = $(if ($On -and $script:WizardMode -eq 'wizard') { 'Collapsed' } else { 'Visible' }) }
    (Ui 'SetupBanner').Visibility = 'Collapsed'
}

function Enter-PanelWizard([int]$Step = 1) {
    $script:WizardMode = 'wizard'
    $script:WizardStep = [math]::Max(1, [math]::Min(7, $Step))
    Show-PanelWizardStep
}

function Show-PanelWizardStep {
    $i = $script:WizardStep
    $s = $script:WizardSteps[$i - 1]
    Set-PanelWizardChrome $true
    (Ui 'WizardStep').Text = "SETUP - STEP $i OF 7"
    (Ui 'WizardTitle').Text = $s.Title
    (Ui 'WizardDots').ItemsSource = @(for ($k = 1; $k -le 7; $k++) {
        [pscustomobject]@{ Title = $script:WizardSteps[$k - 1].Title; State = $(if ($k -lt $i) { 'done' } elseif ($k -eq $i) { 'current' } else { 'todo' }) } })
    (Ui 'WizardBack').Content = 'Back'
    (Ui 'WizardBack').IsEnabled = ($i -gt 1)
    (Ui 'WizardLater').Visibility = 'Visible'
    (Ui 'WizardSkip').Visibility = $(if ($s.Tab -eq 'Alerts') { 'Visible' } else { 'Collapsed' })
    (Ui 'WizardNext').Visibility = $(if ($s.Tab -eq 'Done') { 'Collapsed' } else { 'Visible' })
    (Ui 'WizardNext').IsEnabled = $true
    Set-PanelMessage 'WizardMsg' ''
    Select-PanelTab $s.Tab
    Update-PanelWizardPage $s.Tab
    [void](Set-GuardSetupStep -Step $i -GuardHome $GuardHome)
}

function Show-PanelPage([ValidateSet('Check', 'Login', 'Addons')][string]$Tab) {
    # One of the wizard's pages on its own, from Tools.
    $script:WizardMode = 'page'
    Set-PanelWizardChrome $true
    (Ui 'WizardStep').Text = 'SETUP'
    (Ui 'WizardTitle').Text = @($script:WizardSteps | Where-Object { $_.Tab -eq $Tab })[0].Title
    (Ui 'WizardDots').ItemsSource = $null
    (Ui 'WizardBack').Content = 'Back to Tools'
    (Ui 'WizardBack').IsEnabled = $true
    foreach ($n in 'WizardLater', 'WizardSkip', 'WizardNext') { (Ui $n).Visibility = 'Collapsed' }
    Set-PanelMessage 'WizardMsg' ''
    Select-PanelTab $Tab
    Update-PanelWizardPage $Tab
}

function Exit-PanelWizard([string]$Tab = 'Status') {
    $script:WizardMode = $null
    Set-PanelWizardChrome $false
    Select-PanelTab $Tab
    $script:NextRefresh = [DateTime]::MinValue
    Update-PanelSetupBanner (Get-GuardSetupState -GuardHome $GuardHome)
}

function Update-PanelWizardPage([string]$Tab) {
    switch ($Tab) {
        'Check'  { $sa = @{ Config = (Get-GuardConfig $GuardHome); Nt8Dir = $Nt8Dir }; foreach ($k in $script:SetupCheckArgs.Keys) { $sa[$k] = $script:SetupCheckArgs[$k] }
                   (Ui 'CheckRows').ItemsSource = @(Get-GuardSetupCheck @sa) }
        'Login'  { Show-PanelLogin }
        'Addons' { [void](Update-PanelAddons) }
    }
}

function Show-PanelLogin {
    $h = [string](Get-GuardConfig $GuardHome).login.helper
    $own = ($h -and $h -ne 'builtin')
    (Ui 'LoginBuiltin').IsChecked = -not $own
    (Ui 'LoginOwn').IsChecked = $own
    (Ui 'LoginProgram').Text = $(if ($own) { $h } else { '' })
    (Ui 'LoginOwnRow').IsEnabled = $own
    Update-PanelPasswordState
}

function Update-PanelPasswordState {
    $at = Get-GuardPasswordSavedUtc -GuardHome $GuardHome
    if ((Test-GuardPassword -GuardHome $GuardHome) -and $at) {
        Set-PanelMessage 'LoginPasswordState' "A password is saved (saved $(Format-GuardTime -Utc $at -NowUtc ([DateTime]::UtcNow)))." ok
    } else { Set-PanelMessage 'LoginPasswordState' 'No password saved yet.' bad }
}

function Save-PanelLogin {
    $cfg = Get-GuardConfig $GuardHome
    if ((Ui 'LoginOwn').IsChecked) {
        $p = ([string](Ui 'LoginProgram').Text).Trim().Trim('"')
        if (-not $p) { Set-PanelMessage 'LoginMsg' 'Not saved: choose your login program first.' bad; return $false }
        $cfg.login.helper = $p
    } else { $cfg.login.helper = 'builtin' }
    $r = Save-GuardPanelSettings -Config $cfg -Section 'Login' -GuardHome $GuardHome -Nt8Dir $Nt8Dir
    Set-PanelMessage 'LoginMsg' $r.Message $(if ($r.Ok) { 'ok' } else { 'bad' })
    if ($r.Ok) { $script:Config = Get-GuardConfig $GuardHome }
    return [bool]$r.Ok
}

function Save-PanelPassword {
    # Straight from the box to the encrypted file: never a string in a variable, a log or an event.
    $box = Ui 'LoginPassword'
    if ($box.SecurePassword.Length -eq 0) { Set-PanelMessage 'LoginPasswordMsg' 'Type the password first.' bad; return }
    Save-GuardPassword -Password $box.SecurePassword.Copy() -GuardHome $GuardHome
    $box.Clear()
    Write-GuardEvent -Component panel -Kind 'password.saved' -Severity info -Text 'NinjaTrader password saved' -GuardHome $GuardHome
    Set-PanelMessage 'LoginPasswordMsg' 'Saved, encrypted for this Windows user. A refused-password hold, if there was one, lifts at the next start.' ok
    Update-PanelPasswordState
}

function Update-PanelAddons {
    $z = Get-GuardAddOnsZip
    (Ui 'AddonsZip').Text = $(if ($z) { $z } else { 'NT8Guard-AddOns.zip is not here: build it with guard\build\Build-AddOnsZip.ps1, or install Guard again.' })
    (Ui 'AddonsShowZip').IsEnabled = [bool]$z
    (Ui 'AddonsCopyPath').IsEnabled = [bool]$z
    $la = @{ Config = (Get-GuardConfig $GuardHome); Nt8Dir = $Nt8Dir }; foreach ($k in $script:AddonArgs.Keys) { $la[$k] = $script:AddonArgs[$k] }
    $l = Get-GuardAddonLights @la
    foreach ($p in @(@{ Dot = 'AddonFeedDot'; Text = 'AddonFeedText'; L = $l.FeedGuard }, @{ Dot = 'AddonLinkDot'; Text = 'AddonLinkText'; L = $l.Link })) {
        (Ui $p.Dot).Fill = Res $(switch ($p.L.State) { 'ok' { 'Live' } 'old' { 'Warn' } default { 'Line2' } })
        (Ui $p.Text).Text = $p.L.Text
    }
    (Ui 'AddonsNote').Text = $l.Note
    if ($script:WizardMode -eq 'wizard' -and $script:WizardSteps[$script:WizardStep - 1].Tab -eq 'Addons') {
        (Ui 'WizardNext').IsEnabled = $l.Ready
        Set-PanelMessage 'WizardMsg' $(if ($l.Ready) { '' } else { 'Next waits for both lights.' })
    }
    return $l.Ready
}

function Invoke-PanelWizardNext {
    $tab = $script:WizardSteps[$script:WizardStep - 1].Tab
    $ok = switch ($tab) {
        'Watch'      { Save-PanelSection 'Watch list' }
        'Alerts'     { Save-PanelSection 'Alerts' }
        'Protection' { Save-PanelSection 'Protection' }
        'Addons'     { Update-PanelAddons }
        default      { $true }
    }
    if (-not $ok) { return }
    if ($script:WizardStep -lt 7) { $script:WizardStep++; Show-PanelWizardStep }
}

function Update-PanelSetupBanner($State) {
    $show = (-not $State.Completed) -and -not $script:WizardMode
    (Ui 'SetupBanner').Visibility = $(if ($show) { 'Visible' } else { 'Collapsed' })
    if ($show) { (Ui 'SetupBannerText').Text = "Setup is not finished (step $($State.Step) of 7), so Guard may not be watching NinjaTrader yet." }
}

# ------------------------------------------------------------------ the question overlay
function Show-PanelConfirm([string]$Title, [string]$Text, [string]$OkText, [scriptblock]$OnOk) {
    (Ui 'OverlayTitle').Text = $Title
    (Ui 'OverlayText').Text = $Text
    (Ui 'OverlayOk').Content = $OkText
    $script:OverlayOk = $OnOk
    (Ui 'Overlay').Visibility = 'Visible'
}

# ------------------------------------------------------------------ buttons
function Open-PanelFolder([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { Set-PanelFooter "Not found: $Path"; return }
    Start-Process explorer.exe -ArgumentList ('"' + $Path + '"')
}

function Invoke-PanelPause([int]$Minutes) {
    if ($Minutes -gt 0) { Set-GuardPause -Minutes $Minutes -Reason 'from the control panel' -GuardHome $GuardHome | Out-Null }
    else { Clear-GuardPause -GuardHome $GuardHome }
    $script:NextRefresh = [DateTime]::MinValue
    Set-PanelMessage 'ToolPauseMsg' $(if ($Minutes -gt 0) { "Paused for $Minutes min. Both watchdogs only watch until then." } else { 'Guard is watching again.' }) ok
}

function Register-PanelHandlers {
    # Click handlers only: Click fires for the person's click, never when the code sets IsChecked.
    $w = $script:Win
    # A handler that throws ends the window's message loop: ShowDialog throws and - started by wscript,
    # with no console - the panel simply vanishes. Every failure is shown in the footer and logged instead.
    # Once per process: the dispatcher belongs to the thread, not to one window.
    if (-not $script:DispatcherHooked) {
        $script:DispatcherHooked = $true
        $w.Dispatcher.Add_UnhandledException({
            param($s, $e)
            $x = $e.Exception
            while ($x.InnerException) { $x = $x.InnerException }
            $msg = [string]$x.Message
            try { $msg = Hide-GuardSecret -Text $msg -Config $script:Config } catch { }
            try { Write-GuardLog -Component panel -Message "a button failed: $msg" -GuardHome $GuardHome } catch { }
            try { Set-PanelFooter "That did not work: $msg" } catch { }
            $e.Handled = $true
        })
    }
    foreach ($p in @(@{ n = 'Pause30'; m = 30 }, @{ n = 'Pause120'; m = 120 }, @{ n = 'Pause480'; m = 480 },
                     @{ n = 'ToolPause30'; m = 30 }, @{ n = 'ToolPause120'; m = 120 }, @{ n = 'ToolPause480'; m = 480 })) {
        (Ui $p.n).Tag = $p.m
        (Ui $p.n).Add_Click({ param($s, $e) Invoke-PanelPause ([int]$s.Tag) })
    }
    (Ui 'Resume').Add_Click({ Invoke-PanelPause 0 })
    (Ui 'ToolResume').Add_Click({ Invoke-PanelPause 0 })
    (Ui 'OpenLogs').Add_Click({ Open-PanelFolder (Get-GuardPath logs $GuardHome) })
    (Ui 'FolderLogs').Add_Click({ Open-PanelFolder (Get-GuardPath logs $GuardHome) })
    (Ui 'FolderGuard').Add_Click({ Open-PanelFolder (Get-GuardHome $GuardHome) })
    (Ui 'FolderNt8').Add_Click({ Open-PanelFolder $Nt8Dir })
    (Ui 'BackupFolder').Add_Click({ Open-PanelFolder (Join-Path $Nt8Dir 'templates\Strategy') })

    # Protection
    foreach ($p in @(@{ n = 'PresetWatch'; v = 'watch' }, @{ n = 'PresetRecover'; v = 'recover' }, @{ n = 'PresetHandsoff'; v = 'handsoff' })) {
        (Ui $p.n).Tag = $p.v
        (Ui $p.n).Add_Click({ param($s, $e) Set-GuardPreset -Config @{ protection = $script:ProtEdit } -Preset ([string]$s.Tag) | Out-Null; Update-PanelProtectionView; Set-PanelMessage 'ProtMsg' 'Not saved yet.' })
    }
    foreach ($r in $script:ToggleRows) {
        if ($r.Key -eq 'locked') { continue }
        (Ui "Tg_$($r.Key)").Tag = $r.Key
        (Ui "Tg_$($r.Key)").Add_Click({ param($s, $e) Set-GuardToggle -Config @{ protection = $script:ProtEdit } -Name ([string]$s.Tag) -Value ([bool]$s.IsChecked) | Out-Null; Update-PanelProtectionView; Set-PanelMessage 'ProtMsg' 'Not saved yet.' })
    }
    (Ui 'ProtSave').Add_Click({ Save-PanelSection 'Protection' })
    (Ui 'ProtRevert').Add_Click({ Show-PanelProtection (Get-GuardConfig $GuardHome).protection; Set-PanelMessage 'ProtMsg' 'Back to the saved settings.' })

    # Alerts
    (Ui 'AlertSave').Add_Click({ Save-PanelSection 'Alerts' })
    (Ui 'AlertRevert').Add_Click({ Show-PanelAlerts (Get-GuardConfig $GuardHome).alerts; Set-PanelMessage 'AlertMsg' 'Back to the saved settings.' })
    (Ui 'TopicShow').Add_Click({ Set-PanelTopicVisible ((Ui 'NtfyTopic').Visibility -ne 'Visible') })
    (Ui 'WebhookShow').Add_Click({ Set-PanelWebhookVisible ((Ui 'Webhook').Visibility -ne 'Visible') })
    (Ui 'TopicNew').Add_Click({
        (Ui 'NtfyTopic').Text = New-GuardNtfyTopic
        Set-PanelTopicVisible ((Ui 'NtfyTopic').Visibility -eq 'Visible'); Hide-PanelQr
        Set-PanelMessage 'AlertMsg' 'New private topic made. Save, then subscribe to it on your phone - the old topic stops getting alerts.'
    })
    (Ui 'TopicCopy').Add_Click({
        $cfg = Read-PanelAlerts (Get-GuardConfig $GuardHome)
        if (-not $cfg.alerts.ntfyTopic) { Set-PanelMessage 'AlertMsg' 'No topic yet: press New topic first.' bad; return }
        [Windows.Clipboard]::SetText((Get-GuardNtfyAddress $cfg))
        Set-PanelMessage 'AlertMsg' 'Address copied. Paste it only into the ntfy app on your phone.'
    })
    (Ui 'QrShow').Add_Click({
        if ((Ui 'QrTile').Visibility -eq 'Visible') { Hide-PanelQr; return }
        $cfg = Read-PanelAlerts (Get-GuardConfig $GuardHome)
        if (-not $cfg.alerts.ntfyTopic) { Set-PanelMessage 'AlertMsg' 'No topic yet: press New topic first.' bad; return }
        Start-PanelAction 'Drawing the QR code' { param($c) Get-GuardQrPng -Text (Get-GuardNtfyAddress $c) -Config $c } @($cfg) {
            param($res, $err)
            if ($err -or -not $res) { Set-PanelMessage 'AlertMsg' $(if ($err) { $err } else { 'The QR code could not be drawn.' }) bad; return }
            $bytes = [byte[]]$res
            $bmp = New-Object Windows.Media.Imaging.BitmapImage
            $bmp.BeginInit(); $bmp.CacheOption = 'OnLoad'; $bmp.StreamSource = New-Object IO.MemoryStream(, $bytes); $bmp.EndInit(); $bmp.Freeze()
            (Ui 'QrImage').Source = $bmp
            (Ui 'QrTile').Visibility = 'Visible'; (Ui 'QrHint').Visibility = 'Collapsed'; (Ui 'QrShow').Content = 'Hide code'
        }
    })
    (Ui 'AlertTest').Add_Click({
        $cfg = Read-PanelAlerts (Get-GuardConfig $GuardHome)
        Start-PanelAction 'Sending a test alert' { param($c, $gh) Test-GuardAlert -Config $c -GuardHome $gh } @($cfg, $GuardHome) {
            param($res, $err)
            $r = @($res)[0]
            if ($err -or -not $r) { Set-PanelMessage 'AlertMsg' "The test could not be sent: $err" bad }
            else { Set-PanelMessage 'AlertMsg' ("Test: " + $r.message) $(if ($r.ok) { 'ok' } else { 'bad' }) }
        }
    })

    # Watch list
    (Ui 'WatchSave').Add_Click({ Save-PanelSection 'Watch list' })
    (Ui 'WatchRevert').Add_Click({ Show-PanelWatch (Get-GuardConfig $GuardHome).watch; Set-PanelMessage 'WatchMsg' 'Back to the saved settings.' })

    # Strategy backups
    (Ui 'BackupSave').Add_Click({
        Start-PanelAction 'Saving strategy backups' { param($gh, $nd) Invoke-BackupSave -Config (Get-GuardConfig $gh) -GuardHome $gh -Nt8Dir $nd -Component panel } @($GuardHome, $Nt8Dir) {
            param($res, $err)
            $r = @($res)[0]
            if ($err -or -not $r) { Set-PanelMessage 'BackupMsg' "Not saved: $err" bad }
            elseif ($r.Result -eq 'saved') { Set-PanelMessage 'BackupMsg' ($r.Summary -replace '^strategy backups: ', 'Saved: ') ok }
            elseif ($r.Result -eq 'busy') { Set-PanelMessage 'BackupMsg' 'NinjaTrader is being started right now; try again in a minute.' bad }
            else { Set-PanelMessage 'BackupMsg' $r.Summary bad }
        }
    })
    (Ui 'BackupPutBackAll').Add_Click({ Confirm-PanelPutBack @() $false })
    (Ui 'BackupRows').AddHandler([Windows.Controls.Primitives.ButtonBase]::ClickEvent, [Windows.RoutedEventHandler]{
        param($s, $e)
        $name = [string]$e.OriginalSource.Tag
        if (-not $name) { return }
        if ($e.OriginalSource.Name -eq 'PutBack') { Confirm-PanelPutBack @($name) ([string]$e.OriginalSource.Content -eq 'Bring back'); return }
        $script:ForgetName = $name     # not a closure: a GetNewClosure() block cannot see this script's functions
        Show-PanelConfirm "Forget ${name}?" "Guard drops its backup of $name and will not put it back. The template file stays in NinjaTrader's templates folder, so you can still load it by hand." 'Forget' {
            Start-PanelAction "Forgetting $script:ForgetName" { param($n, $gh, $nd) Invoke-GuardBackupForget -Name $n -Config (Get-GuardConfig $gh) -GuardHome $gh -Nt8Dir $nd } @($script:ForgetName, $GuardHome, $Nt8Dir) {
                param($res, $err)
                $r = @($res)[0]
                if ($err -or -not $r) { Set-PanelMessage 'BackupMsg' "Not forgotten: $err" bad } else { Set-PanelMessage 'BackupMsg' $r.Message $(if ($r.Ok) { 'ok' } else { 'bad' }) }
            }
        }
    })

    # Tools
    (Ui 'ToolTest').Add_Click({
        Start-PanelAction 'Sending a test alert' { param($gh) Test-GuardAlert -Config (Get-GuardConfig $gh) -GuardHome $gh } @($GuardHome) {
            param($res, $err)
            $r = @($res)[0]
            if ($err -or -not $r) { Set-PanelMessage 'ToolTestMsg' "The test could not be sent: $err" bad }
            else { Set-PanelMessage 'ToolTestMsg' $r.message $(if ($r.ok) { 'ok' } else { 'bad' }) }
        }
    })
    (Ui 'ToolDiag').Add_Click({
        $out = Join-Path ([Environment]::GetFolderPath('Desktop')) ("NT8Guard-diagnostics-{0}.zip" -f (Get-Date -Format 'yyyyMMdd-HHmm'))
        Start-PanelAction 'Creating the diagnostics zip' { param($o, $gh, $nd) New-GuardDiagnosticsZip -OutPath $o -GuardHome $gh -Nt8Dir $nd } @($out, $GuardHome, $Nt8Dir) {
            param($res, $err)
            $r = @($res)[0]
            if ($err -or -not $r) { Set-PanelMessage 'ToolDiagMsg' "Not created: $err" bad; return }
            Set-PanelMessage 'ToolDiagMsg' "Saved $(Split-Path $r.Path -Leaf) on your Desktop. $($r.Note)" ok
            Start-Process explorer.exe -ArgumentList ('/select,"' + $r.Path + '"')
        }
    })

    # Tools: the buttons that touch NinjaTrader, each asked first
    (Ui 'ToolTestLogin').Add_Click({ Confirm-PanelTestLogin 'ToolLoginMsg' })
    (Ui 'ToolDrill').Add_Click({ Confirm-PanelDrill 'ToolDrillMsg' })
    (Ui 'ToolUninstall').Add_Click({
        Show-PanelConfirm 'Uninstall NT8 Guard?' ("Its scheduled tasks and Start-menu shortcut are removed now, and its folder - settings, saved password, logs and the backup list - when this window closes. " +
                                                  "NinjaTrader keeps running, unwatched. Its add-ons and your strategy backup templates stay in NinjaTrader; the result says how to remove the add-ons.") 'Uninstall' {
            Start-PanelRiskyAction uninstall 'Uninstalling NT8 Guard' @{ GuardHome = $GuardHome } 'ToolUninstallMsg'
        }
    })

    # the setup wizard, and its pages opened from Tools
    (Ui 'WizardNext').Add_Click({ Invoke-PanelWizardNext })
    (Ui 'WizardBack').Add_Click({ if ($script:WizardMode -eq 'page') { Exit-PanelWizard 'Tools' } elseif ($script:WizardStep -gt 1) { $script:WizardStep--; Show-PanelWizardStep } })
    (Ui 'WizardSkip').Add_Click({ if ($script:WizardStep -lt 7) { $script:WizardStep++; Show-PanelWizardStep } })
    (Ui 'WizardLater').Add_Click({ Exit-PanelWizard 'Status' })
    (Ui 'SetupContinue').Add_Click({ Enter-PanelWizard (Get-GuardSetupState -GuardHome $GuardHome).Step })
    (Ui 'ToolSetup').Add_Click({ Enter-PanelWizard 1 })
    (Ui 'ToolCheck').Add_Click({ Show-PanelPage Check })
    (Ui 'ToolLogin').Add_Click({ Show-PanelPage Login })
    (Ui 'ToolAddons').Add_Click({ Show-PanelPage Addons })
    (Ui 'CheckAgain').Add_Click({ Update-PanelWizardPage Check })
    (Ui 'LoginBuiltin').Add_Click({ (Ui 'LoginOwnRow').IsEnabled = $false })
    (Ui 'LoginOwn').Add_Click({ (Ui 'LoginOwnRow').IsEnabled = $true })
    (Ui 'LoginBrowse').Add_Click({
        $d = New-Object Microsoft.Win32.OpenFileDialog
        $d.Filter = 'Programs (*.exe)|*.exe'; $d.Title = 'Your login program'
        if ($d.ShowDialog($script:Win)) { (Ui 'LoginProgram').Text = $d.FileName; (Ui 'LoginOwn').IsChecked = $true; (Ui 'LoginOwnRow').IsEnabled = $true }
    })
    (Ui 'LoginSave').Add_Click({ [void](Save-PanelLogin) })
    (Ui 'LoginSavePassword').Add_Click({ Save-PanelPassword })
    (Ui 'LoginTest').Add_Click({ Confirm-PanelTestLogin 'LoginTestMsg' })
    (Ui 'AddonsShowZip').Add_Click({ $z = Get-GuardAddOnsZip; if ($z) { Start-Process explorer.exe -ArgumentList ('/select,"' + $z + '"') } })
    (Ui 'AddonsCopyPath').Add_Click({ $z = Get-GuardAddOnsZip; if ($z) { [Windows.Clipboard]::SetText($z); Set-PanelMessage 'WizardMsg' 'Path copied: paste it into NinjaTrader''s import window.' ok } })
    (Ui 'DoneFinish').Add_Click({ Start-PanelRiskyAction finish 'Finishing setup' @{ Config = (Get-GuardConfig $GuardHome); GuardHome = $GuardHome } 'DoneMsg' })
    (Ui 'DoneDrill').Add_Click({ Confirm-PanelDrill 'DoneMsg' })
    (Ui 'DoneStatus').Add_Click({ Exit-PanelWizard 'Status' })

    # the overlays
    (Ui 'OverlayCancel').Add_Click({ (Ui 'Overlay').Visibility = 'Collapsed' })
    (Ui 'OverlayOk').Add_Click({ (Ui 'Overlay').Visibility = 'Collapsed'; if ($script:OverlayOk) { & $script:OverlayOk } })
    (Ui 'ActionClose').Add_Click({
        (Ui 'ActionOverlay').Visibility = 'Collapsed'
        $script:ActionView = $null; $script:ActionProgress = $null
        if ($script:CleanupScript) { $script:Win.Close() }     # uninstalled: the folder goes once the window is closed
    })
    # Closing mid-action would stop it half done (NinjaTrader closed, Guard paused): wait for it.
    $w.Add_Closing({ param($s, $e) if ($script:Action) { $e.Cancel = $true; Set-PanelFooter "Wait for '$($script:Action.Label)' to finish - closing now would stop it half done." } })
    $w.Add_Closed({ if ($script:CleanupScript) { try { Start-GuardCleanup -Script $script:CleanupScript } catch { } } })

    (Ui 'Root').Add_SizeChanged({ Update-PanelColumns })
}

# ------------------------------------------------------------------ build, screenshot, run
function Initialize-Panel {
    # The window with every tab filled from the files as they are now. Shared by the real run, the
    # screenshot mode and the tests.
    [CmdletBinding()] param([hashtable]$StatusArgs = @{})
    $script:Win = New-PanelWindow
    $script:CardSig = $null; $script:EventSig = $null; $script:CardCols = 4
    $script:ActionView = $null; $script:ActionProgress = $null; $script:CleanupScript = $null; $script:WizardMode = $null
    Show-PanelSettings
    $sa = @{ GuardHome = $GuardHome; Nt8Dir = $Nt8Dir; Config = $script:Config }
    foreach ($k in $StatusArgs.Keys) { $sa[$k] = $StatusArgs[$k] }
    $st = Get-GuardStatus @sa
    Update-PanelStatus $st
    Update-PanelBackups (Get-GuardBackupList -GuardHome $GuardHome) $st
    Update-PanelWatchInfo (Get-GuardWatchInfo -GuardHome $GuardHome -Nt8Dir $Nt8Dir -Config $script:Config)
    Update-PanelSetupBanner (Get-GuardSetupState -GuardHome $GuardHome)
    Register-PanelHandlers
    return $script:Win
}

function Select-PanelTab([string]$Name) { (Ui $script:TabNames[$Name]).IsSelected = $true }

function Invoke-PanelOffscreen {
    # Lay out tabs without showing a window: the root Grid is lifted out of the Window into a Border that
    # carries the Window's resources, measured at the window's size, handed to $Action, then put back.
    # (Content of a tab that is not selected - or of a window never shown - is not laid out at all.)
    param([string[]]$Tabs, [scriptblock]$Action)
    $win = $script:Win
    $root = $win.Content
    $win.Content = $null
    $holder = New-Object Windows.Controls.Border
    $holder.Resources = $win.Resources
    $holder.Child = $root
    $size = New-Object Windows.Size($win.Width, $win.Height)
    try {
        foreach ($t in $Tabs) {
            Select-PanelTab $t
            $holder.Measure($size); $holder.Arrange((New-Object Windows.Rect($size))); $holder.UpdateLayout()
            Update-PanelColumns; $holder.UpdateLayout()
            & $Action $t $holder
        }
    } finally {
        $holder.Child = $null
        $win.Content = $root
    }
}

function Save-PanelScreenshots {
    # Render every tab to <Dir>\<Tab>.png without showing a window.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Dir, [string[]]$Tabs = @($script:TabNames.Keys))
    if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Path $Dir -Force | Out-Null }
    $files = New-Object System.Collections.Generic.List[string]
    Invoke-PanelOffscreen -Tabs $Tabs -Action {
        param($tab, $holder)
        $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap([int]$script:Win.Width, [int]$script:Win.Height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $rtb.Render($holder)
        $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
        $f = Join-Path $Dir "$tab.png"
        $fs = [IO.File]::Create($f)
        try { $enc.Save($fs) } finally { $fs.Dispose() }
        $files.Add($f)
    }
    return $files.ToArray()
}

function Get-PanelVisibleText {
    # The text a person can see on a tab: laid out as for a screenshot, collapsed and hidden elements
    # skipped (a collapsed TextBox still holds its text - the hidden topic is exactly that).
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Tab)
    $texts = New-Object System.Collections.Generic.List[string]
    Invoke-PanelOffscreen -Tabs @($Tab) -Action { param($t, $holder) Add-PanelVisibleText $holder $texts }
    return $texts.ToArray()
}

function Add-PanelVisibleText($Element, $Texts) {
    if ($Element -is [Windows.UIElement] -and $Element.Visibility -ne 'Visible') { return }
    if ($Element -is [Windows.Controls.TextBlock] -or $Element -is [Windows.Controls.TextBox]) { $Texts.Add([string]$Element.Text) }
    $n = 0
    try { $n = [Windows.Media.VisualTreeHelper]::GetChildrenCount($Element) } catch { }
    for ($i = 0; $i -lt $n; $i++) { Add-PanelVisibleText ([Windows.Media.VisualTreeHelper]::GetChild($Element, $i)) $Texts }
}

function Set-PanelDarkTitleBar {
    # Windows 10 20H1+ / 11 draw the title bar dark on request (DWMWA_USE_IMMERSIVE_DARK_MODE = 20). Older
    # Windows refuse the call and keep the light bar - harmless, so every failure is ignored.
    try {
        if (-not ('NT8Guard.Dwm' -as [type])) {
            Add-Type -Namespace NT8Guard -Name Dwm -MemberDefinition '[DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);'
        }
        $hwnd = (New-Object Windows.Interop.WindowInteropHelper($script:Win)).Handle
        $on = 1
        [void][NT8Guard.Dwm]::DwmSetWindowAttribute($hwnd, 20, [ref]$on, 4)
    } catch { }
}

function Show-OtherPanel {
    # A second start brings the open panel to the front instead of opening another.
    Add-Type -Namespace NT8Guard -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
'@
    foreach ($p in Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID -and $_.MainWindowTitle -eq 'NT8 Guard' }) {
        [void][NT8Guard.Win32]::ShowWindow($p.MainWindowHandle, 9)
        [void][NT8Guard.Win32]::SetForegroundWindow($p.MainWindowHandle)
    }
}

if ($ImportOnly) { return }

# A panel that cannot start must say why: started from the shortcut (wscript, no console) a failure would
# otherwise end it without a word. Logged to panel.log, and shown in a message box unless in screenshot mode.
try {
    if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'The panel needs a single-threaded apartment: run it with powershell -STA.' }
    $Nt8Dir = $(if ($Nt8Dir) { $Nt8Dir } else { Get-Nt8DataDir ([string](Get-GuardConfig $GuardHome).nt8.dataDir) })

    if ($ScreenshotDir) {
        Initialize-Panel | Out-Null
        Save-PanelScreenshots -Dir $ScreenshotDir | ForEach-Object { "saved $_" }
        exit 0
    }

    $created = $false
    $script:Mutex = New-Object Threading.Mutex($true, 'Local\NT8GuardPanel', [ref]$created)
    if (-not $created) { Show-OtherPanel; exit 0 }
    try {
        Initialize-Panel | Out-Null
        if ($Wizard) { Enter-PanelWizard (Get-GuardSetupState -GuardHome $GuardHome).Step } else { Select-PanelTab $Tab }
        $script:Iss = New-PanelSessionState
        $script:RefreshRs = [runspacefactory]::CreateRunspace($script:Iss)
        $script:RefreshRs.Open()
        $script:NextRefresh = [DateTime]::UtcNow.AddSeconds(2)
        $timer = New-Object Windows.Threading.DispatcherTimer
        $timer.Interval = [TimeSpan]::FromMilliseconds(250)
        $timer.Add_Tick({ try { Step-PanelTimer } catch { Set-PanelFooter "Refresh failed: $($_.Exception.Message)" } })
        $timer.Start()
        $script:Win.Add_SourceInitialized({ Set-PanelDarkTitleBar })
        $script:Win.Add_ContentRendered({ Update-PanelColumns })
        [void]$script:Win.ShowDialog()
        $timer.Stop()
    } finally {
        try { $script:RefreshRs.Dispose() } catch { }
        try { $script:Mutex.ReleaseMutex(); $script:Mutex.Dispose() } catch { }
    }
} catch {
    # The outer message: a XAML error's inner exceptions can be blank ("Exception calling Parse ...: There
    # are multiple root elements. Line 979" is the useful text).
    $msg = "The control panel could not start: $($_.Exception.Message)"
    try { $msg = Hide-GuardSecret -Text $msg -Config (Get-GuardConfig $GuardHome) } catch { }
    try { Write-GuardLog -Component panel -Message $msg -GuardHome $GuardHome } catch { }
    if (-not $ScreenshotDir) {
        try { [void][Windows.MessageBox]::Show("$msg`n`nThe same text is in Guard's logs folder (panel.log).", 'NT8 Guard') } catch { }
    }
    Write-Error $msg -ErrorAction Continue
    exit 1
}
