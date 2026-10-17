# The overwatch's settings and its newer helpers. The older decision logic lives in Guard-Overwatch.ps1
# itself, where its tests extract it function by function.
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Log.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Config.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Events.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Alerts.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Start.psm1')

function Get-OverwatchSettings {
    # guard.json -> the flat settings the overwatch's logic reads ($cfg inside Guard-Overwatch.ps1).
    # Protection decides ACT or ALERT for each remedy (spec section 4). The detection, the records and
    # the exposure check always run: an 'alert' setting still has to know what it would have done.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config)
    $p = Get-GuardProtection $Config
    $s = @{}
    foreach ($k in $Config.thresholds.Keys) { $s[$k] = $Config.thresholds[$k] }
    foreach ($k in $Config.overwatch.Keys) { $s[$k] = $Config.overwatch[$k] }
    $s.marketTimeZoneId         = [string]$Config.watch.marketTimeZone
    $s.restartWhenFrozen        = [bool]$p.restartWhenFrozen     # kill a wedged NinjaTrader
    $s.reconnect                = [bool]$p.reconnect             # reconnect what was connected before
    $s.reenableStrategies       = [bool]$p.reenableStrategies    # switch back on what was running before
    $s.flattenNakedBeforeEnable = [bool]$p.flattenUnprotected    # close an unprotected position first
    $s.dumpBeforeKill           = [bool]$p.crashDumps            # crash report before the kill
    $s.relaunch                 = [bool]$p.relaunch              # a killed NinjaTrader is started again
    $s.restoreStrategies        = $true                          # the record + restore pass always runs
    $s.restoreConnections       = $true
    $s.exposureGate             = $true                          # never switch a strategy on blind
    return $s
}

function ConvertTo-GuardSeverity {
    # The overwatch's ntfy priorities ('3' / '4' / '5') as Guard severities.
    [CmdletBinding()] param([string]$Priority)
    switch ([string]$Priority) { '5' { return 'alert' } '4' { return 'warn' } default { return 'info' } }
}

function Test-RelauncherFresh {
    # The relauncher stamps state\relaunch.heartbeat every minute - also while paused or held at the
    # lock screen. Fresh = it is running and owns starting NinjaTrader. Stale or missing = its task is
    # gone, disabled or broken, and the overwatch starts NinjaTrader itself (spec 3.4). Decided from the
    # heartbeat as it is NOW, never from a remembered timestamp.
    [CmdletBinding()] param([string]$GuardHome, [int]$MaxAgeSeconds = 180, [Nullable[DateTime]]$NowUtc)
    $now = $(if ($null -ne $NowUtc) { $NowUtc } else { [DateTime]::UtcNow })
    $f = Join-Path (Get-GuardPath state $GuardHome) 'relaunch.heartbeat'
    if (-not (Test-Path $f)) { return $false }
    $t = $null
    try { $t = ConvertFrom-GuardUtc ([IO.File]::ReadAllText($f).Trim()) } catch { }
    if ($null -eq $t) { return $false }
    return (($now - $t).TotalSeconds -le $MaxAgeSeconds)
}

function Test-FeedGuardWritesEvents {
    # Does the feed guard running in NinjaTrader write structured events? Its heartbeat says so
    # ("events": true, add-on 5.0.0 and later). Then its events are the ONLY evidence the overwatch reads:
    # its log sentences are for people, and rewording one must never change what the overwatch does. An
    # older add-on that writes no events is still read from its log sentences.
    [CmdletBinding()] param([string]$HeartbeatPath)
    if (-not $HeartbeatPath -or -not (Test-Path $HeartbeatPath)) { return $false }
    try { return ((Get-Content $HeartbeatPath -Raw | ConvertFrom-Json).events -eq $true) } catch { return $false }
}

function Get-FeedEventUtc {
    # The newest feed-guard event of a kind since $SinceUtc, or $null. The feed guard (component
    # 'feedguard') writes feedguard-events.jsonl in NinjaTrader's folder; Read-GuardEvents merges it in.
    [CmdletBinding()] param([string]$GuardHome, [string]$Nt8Dir, [Parameter(Mandatory)][string]$KindPattern,
                            [Parameter(Mandatory)][DateTime]$SinceUtc)
    $e = @(Read-GuardEvents -GuardHome $GuardHome -Nt8Dir $Nt8Dir -Max 1 -Component feedguard -KindPattern $KindPattern -SinceUtc $SinceUtc)
    if (-not $e.Count) { return $null }
    return (ConvertFrom-GuardUtc ([string]$e[0].utc))
}

function Get-BackupAdvice {
    # What to tell the user about strategies that left NinjaTrader's list. 'missing' = dropped across a
    # restart; 'removed' = gone while NinjaTrader was running. Guard puts 'missing' ones back itself only
    # when it both starts NinjaTrader and puts strategies back (Protection); otherwise it must not promise
    # to. Putting one back now is the control panel's button: it checks you are flat, pauses Guard, closes
    # NinjaTrader, puts the strategy back on its chart and lets NinjaTrader start again.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config, [Parameter(Mandatory)][ValidateSet('missing', 'removed')][string]$Kind)
    $p = Get-GuardProtection $Config
    $how = 'it checks you are flat, pauses Guard, closes NinjaTrader, puts it back on its chart and lets NinjaTrader start again.'
    if ($Kind -eq 'removed') {
        return "If you removed them, nothing to do - their settings stay in Strategy backups. If you did not: open NT8 Guard, Strategy backups, and press Bring back - $how"
    }
    if ($p.restoreStrategies -and $p.relaunch) {
        return 'Guard puts them back automatically the next time it starts NinjaTrader - or now: Strategy backups, Put back now.'
    }
    return "Putting them back is set to alert only in Protection, so Guard will not do it by itself. To put one back now: open NT8 Guard, Strategy backups, and press Put back now - $how"
}

function Invoke-BackupSave {
    # Save every strategy's settings (P1's backup_export.py, which reads NinjaTrader's database, not
    # its window) and turn what it reports into plain alerts. Holds the start lock, so it never runs
    # while a start is putting strategies back. Returns @{ Result = saved|busy|failed; Summary; Report }.
    # Each strategy's change is reported by the exporter exactly once, so each alert fires once - which is
    # why the panel's Forget (-Forget NAME, also a full save) goes through here too.
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [string]$Nt8Dir, [scriptblock]$Python,
          [string]$Component = 'overwatch', [string[]]$Forget = @())
    if (-not $Python) { $Python = { param($s, $a, $t) Invoke-GuardPython -Script $s -Arguments $a -TimeoutSeconds $t -Config $Config } }
    $log = { param($m) Write-GuardLog -Component $Component -Message $m -GuardHome $GuardHome }
    $state = Get-GuardPath state $GuardHome
    $lock = Open-GuardStartLock $GuardHome
    if (-not $lock) { & $log 'strategy backups: a start is in progress - saving next time'; return @{ Result = 'busy'; Summary = ''; Report = $null } }
    $pyArgs = @('--nt8-dir', $Nt8Dir, '--guard-home', (Get-GuardHome $GuardHome))
    foreach ($n in $Forget) { $pyArgs += @('--forget', $n) }
    try {
        $r = & $Python 'backup_export.py' ($pyArgs + '--json') 120
    } finally { $lock.Dispose() }

    $rep = $null
    if ($r.ExitCode -ge 0 -and $r.Out) { try { $rep = $r.Out | ConvertFrom-Json } catch { } }
    $errFile = Join-Path $state "$Component.backuperror"
    if ($null -eq $rep) {
        $why = ([string]$r.Err).Trim()
        if (-not $why) { $why = "exit code $($r.ExitCode)" }
        $summary = "strategy backups could not be saved: $why"
        & $log $summary
        $was = $(if (Test-Path $errFile) { [IO.File]::ReadAllText($errFile) } else { '' })
        if ($was -ne $why) {
            [IO.File]::WriteAllText($errFile, $why)
            Write-GuardEvent -Component $Component -Kind 'backups.save-failed' -Severity warn -Text $summary -GuardHome $GuardHome
            Send-GuardAlert -Title 'NT8 strategy backups not saved' -Body "Guard could not save your strategies' settings: $why. Until this is fixed, a strategy NinjaTrader drops cannot be put back." `
                            -Severity warn -Config $Config -Component $Component -GuardHome $GuardHome | Out-Null
        }
        return @{ Result = 'failed'; Summary = $summary; Report = $null }
    }
    if (Test-Path $errFile) { Remove-Item $errFile -Force -ErrorAction SilentlyContinue; & $log 'strategy backups: saving works again' }

    $tr = @($rep.transitions)
    $missing = @($tr | Where-Object { $_.new -eq 'missing' } | ForEach-Object { [string]$_.name })
    $removed = @($tr | Where-Object { $_.new -eq 'removed' } | ForEach-Object { [string]$_.name })
    $failed = @($rep.failed | ForEach-Object { "$($_.name): $($_.error)" })
    $summary = "strategy backups: $(@($rep.saved).Count) saved, $($failed.Count) failed, $($missing.Count) missing, $($removed.Count) removed"
    & $log $summary
    foreach ($w in @($rep.warnings)) { & $log "  warning: $w" }
    foreach ($d in @($rep.duplicates)) { & $log "  duplicate name: $($d.name) (ids $(@($d.ids) -join ', ')) - only the lowest id can be put back" }

    if ($removed.Count) {
        $list = $removed -join "`n"
        Write-GuardEvent -Component $Component -Kind 'backups.removed' -Severity alert -Text "Left the Strategies list while NinjaTrader was running: $($removed -join ', ')" -GuardHome $GuardHome
        Send-GuardAlert -Title 'NT8 strategies removed while running' `
                        -Body ("These left NinjaTrader's Strategies list while it was running:`n$list`n" + (Get-BackupAdvice -Config $Config -Kind removed)) `
                        -Severity alert -Config $Config -Component $Component -GuardHome $GuardHome | Out-Null
    }
    if ($missing.Count) {
        Write-GuardEvent -Component $Component -Kind 'backups.missing' -Severity warn -Text "Dropped across a restart: $($missing -join ', ')" -GuardHome $GuardHome
        Send-GuardAlert -Title 'NT8 strategies missing' `
                        -Body ("NinjaTrader dropped these from its Strategies list across a restart:`n" + ($missing -join "`n") + "`n" + (Get-BackupAdvice -Config $Config -Kind missing)) `
                        -Severity warn -Config $Config -Component $Component -GuardHome $GuardHome | Out-Null
    }
    $failFile = Join-Path $state "$Component.backupfailed"
    $failKey = ($failed | Sort-Object) -join '|'
    $wasFail = $(if (Test-Path $failFile) { [IO.File]::ReadAllText($failFile) } else { '' })
    if ($failKey -ne $wasFail) {
        if ($failKey) {
            [IO.File]::WriteAllText($failFile, $failKey)
            Write-GuardEvent -Component $Component -Kind 'backups.failed' -Severity warn -Text "Could not save: $($failed -join '; ')" -GuardHome $GuardHome
            Send-GuardAlert -Title 'NT8 strategy settings not saved' -Body ("These strategies' settings could not be saved:`n" + ($failed -join "`n")) `
                            -Severity warn -Config $Config -Component $Component -GuardHome $GuardHome | Out-Null
        } else { Remove-Item $failFile -Force -ErrorAction SilentlyContinue }
    }
    return @{ Result = 'saved'; Summary = $summary; Report = $rep }
}

function Get-Nt8CurrentLog {
    # NinjaTrader's current log in ENGLISH: the newest log\log.*.en.txt. NinjaTrader writes each log twice,
    # in its own language (log.*.txt) and in English (log.*.en.txt); the lines Guard looks for are
    # translated in the first ("Aktiviere NinjaScript Strategie ..." on a German install). Without an
    # English twin, the newest log.*.txt.
    [CmdletBinding()] param([string]$Nt8Dir)
    $d = Join-Path $Nt8Dir 'log'
    if (-not (Test-Path $d)) { return $null }
    $all = @(Get-ChildItem -LiteralPath $d -Filter 'log.*.txt' -File -ErrorAction SilentlyContinue)
    $f = @($all | Where-Object { $_.Name -like '*.en.txt' } | Sort-Object LastWriteTimeUtc -Descending) | Select-Object -First 1
    if (-not $f) { $f = @($all | Sort-Object LastWriteTimeUtc -Descending) | Select-Object -First 1 }
    return $(if ($f) { $f.FullName } else { $null })
}

function Find-DuplicateStrategyStarts {
    # NinjaTrader logs one "Enabling NinjaScript strategy 'Name/Id'" line per strategy start. Two for the
    # same Id within $WindowSeconds, with no "Disabling" of it between, mean TWO live instances of one
    # row - both trade, so the position is double size, and only a NinjaTrader restart clears them.
    # Seen after connects landing seconds apart, and after a restore. Times are NinjaTrader's (local).
    # Reports only pairs completed after $SinceLocal, but looks $WindowSeconds further back so a pair
    # split across two scans is still seen. Returns @{ Duplicates = @(@{ Strategy; At }); NewestLocal }.
    [CmdletBinding()] param([string[]]$Lines = @(), [Nullable[DateTime]]$SinceLocal, [double]$WindowSeconds = 3)
    $rx = '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}:\d{3})\|\d+\|\d+\|(Enabling|Disabling) NinjaScript strategy ''([^'']+)'''
    $from = $(if ($null -ne $SinceLocal) { $SinceLocal.AddSeconds(-$WindowSeconds) } else { [DateTime]::MinValue })
    $open = @{}
    $dups = New-Object System.Collections.Generic.List[object]
    $newest = $SinceLocal
    foreach ($l in $Lines) {
        $m = [regex]::Match([string]$l, $rx)
        if (-not $m.Success) { continue }
        $t = $null
        try { $t = [DateTime]::ParseExact($m.Groups[1].Value, 'yyyy-MM-dd HH:mm:ss:fff', [Globalization.CultureInfo]::InvariantCulture) } catch { continue }
        if ($t -le $from) { continue }
        if ($null -eq $newest -or $t -gt $newest) { $newest = $t }
        $name = $m.Groups[3].Value
        if ($m.Groups[2].Value -eq 'Disabling') { $open.Remove($name); continue }
        if ($open.ContainsKey($name) -and ($t - $open[$name]).TotalSeconds -le $WindowSeconds -and
            ($null -eq $SinceLocal -or $t -gt $SinceLocal)) {
            $dups.Add([pscustomobject]@{ Strategy = $name; At = $t })
        }
        $open[$name] = $t
    }
    return [pscustomobject]@{ Duplicates = @($dups.ToArray()); NewestLocal = $newest }
}

function Read-Nt8Windows {
    # Every top-level window of one process: @{ Title; Visible; Hung }. Only calls that send the target NO
    # message - EnumWindows, GetWindowThreadProcessId, IsWindowVisible, InternalGetWindowText,
    # IsHungAppWindow - because a probe that sends a message to a hung window hangs with it (GetWindowText
    # did exactly that on 2026-08-25).
    [CmdletBinding()] param([Parameter(Mandatory)][int]$ProcessId)
    if (-not ('NT8Guard.WinHealth' -as [type])) {
        Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
namespace NT8Guard {
    public static class WinHealth {
        public class Win { public string Title; public bool Visible; public bool Hung; }
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr h);
        public static List<Win> ForProcess(int processId) {
            List<Win> list = new List<Win>();
            EnumWindows(delegate (IntPtr h, IntPtr l) {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid == (uint)processId) {
                    StringBuilder sb = new StringBuilder(256); InternalGetWindowText(h, sb, 256);
                    Win w = new Win(); w.Title = sb.ToString(); w.Visible = IsWindowVisible(h); w.Hung = IsHungAppWindow(h);
                    list.Add(w);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }
}
'@
    }
    return [NT8Guard.WinHealth]::ForProcess($ProcessId)
}

function Get-Nt8WindowHealth {
    # Which of NinjaTrader's windows have stopped pumping messages, and whether the Control Center is one.
    # NinjaTrader runs its windows on several UI threads - the Control Center on its own, charts and the
    # other windows spread over the rest - so "is NinjaTrader responding" has no single answer, and
    # Process.Responding tests only the topmost window. On the VPS on 2026-10-16 the Control Center's thread
    # stayed stuck for almost 7 hours while that check, and both add-on heartbeats, stayed green.
    # The Control Center is found by its title ("Control Center - <tab>"). On an install that translates
    # the title it is not found; the overwatch then has only the strategy-list read, which alerts but
    # never restarts NinjaTrader.
    #   -Windows { -> @(@{ Title; Visible; Hung }) } replaces the Win32 read (tests).
    # Returns @{ ControlCenterFound; ControlCenterHung; HungTitles }.
    [CmdletBinding()] param([Parameter(Mandatory)][int]$ProcessId, [scriptblock]$Windows)
    if (-not $Windows) { $Windows = { Read-Nt8Windows -ProcessId $ProcessId } }
    # ForEach-Object unrolls both shapes: a list the reader returns, and one array object a test returns.
    $all = @(& $Windows | ForEach-Object { $_ } | Where-Object { $_.Visible -and [string]$_.Title })
    $cc = @($all | Where-Object { [string]$_.Title -match '^Control Center( - |$)' })
    return [pscustomobject]@{
        ControlCenterFound = ($cc.Count -gt 0)
        ControlCenterHung  = (@($cc | Where-Object { $_.Hung }).Count -gt 0)
        HungTitles         = @($all | Where-Object { $_.Hung } | ForEach-Object { [string]$_.Title } | Select-Object -Unique)
    }
}

function Get-ControlCenterReadResult {
    # What one strategy-list read through the NT8 link says about the Control Center:
    #   ok          it answered;
    #   timeout     the link reached the Control Center's dispatcher and it did not run the read within
    #               the link's own bound ("... did not respond within 20s (saturated)") - the ONLY answer
    #               that is evidence of a frozen Control Center;
    #   unavailable anything else: no answer from the link at all (busy with a long job, or down), or an
    #               error that is not a freeze (the Strategies tab closed, an NT8 update moved the grid).
    # -WindowLongHung: the Control Center's window has not responded for several checks running. No answer
    # is then a timeout too: the link's bound covers only the wait for the read to START, so a Control
    # Center that froze in the middle of a read holds the link's request thread and nothing comes back.
    # Returns @{ Result; Why }.
    [CmdletBinding()] param($Response, [switch]$WindowLongHung)
    if ($null -eq $Response) {
        if ($WindowLongHung) { return [pscustomobject]@{ Result = 'timeout'; Why = 'the NT8 link did not answer while the Control Center window has not responded for several checks (the link is most likely stuck on it)' } }
        return [pscustomobject]@{ Result = 'unavailable'; Why = 'the NT8 link did not answer' }
    }
    if ("$($Response.status)" -eq 'ok') { return [pscustomobject]@{ Result = 'ok'; Why = '' } }
    $notes = @($Response.notes | ForEach-Object { [string]$_ })
    if (@($notes | Where-Object { $_ -match 'did not respond within' }).Count) { return [pscustomobject]@{ Result = 'timeout'; Why = ($notes -join '; ') } }
    $why = @($notes + @($Response.errors | ForEach-Object { [string]$_.message })) | Where-Object { $_ }
    return [pscustomobject]@{ Result = 'unavailable'; Why = $(if ($why) { $why -join '; ' } else { "status $($Response.status)" }) }
}

function Get-ControlCenterStep {
    # One cycle of a CONTROL_CENTER_HUNG fault: 'count' (not confirmed yet), 'alert' (the first page),
    # 'wait', 'restart', or 'hold' (due for a restart, but only the strategy-list read is failing and the
    # Control Center's window is not seen hung - never restart on one signal alone). -WindowHung is the
    # caller's verdict over several checks, not one 5-second sample.
    # The gap between the alert and the restart leaves room for a long compile in the NinjaScript Editor.
    [CmdletBinding()] param([int]$Consecutive, [int]$AlertAt, [int]$RestartAt, [bool]$WindowHung)
    if ($Consecutive -lt $AlertAt) { return 'count' }
    if ($Consecutive -ge $RestartAt) { return $(if ($WindowHung) { 'restart' } else { 'hold' }) }
    if ($Consecutive -eq $AlertAt) { return 'alert' }
    return 'wait'
}

Export-ModuleMember -Function Get-OverwatchSettings, ConvertTo-GuardSeverity, Test-RelauncherFresh, Get-FeedEventUtc, Test-FeedGuardWritesEvents,
                              Get-BackupAdvice, Invoke-BackupSave, Get-Nt8CurrentLog, Find-DuplicateStrategyStarts,
                              Read-Nt8Windows, Get-Nt8WindowHealth, Get-ControlCenterReadResult, Get-ControlCenterStep
