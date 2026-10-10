# Guard-Overwatch.ps1 - an EXTERNAL overwatch for NinjaTrader 8 and its feed guard add-on.
# Scheduled task, every minute (Register-GuardTasks.ps1).
#
# ============================================================================================
# WHY THIS EXISTS - a silent death that nothing on the box noticed (2026-08-25).
# ============================================================================================
# The feed guard (AutoReconnect) is a single guardian thread whose Tick() is:
#
#       if (!Monitor.TryEnter(_gate)) return;  try { ... } finally { Monitor.Exit(_gate); }
#
# Correct for re-entrancy, catastrophic for observability. That day the guardian called into the
# Control Center's UI thread, that thread was hung, and the guardian blocked inside the gate. Every
# later tick returned instantly at TryEnter. Result: 24 minutes, ~145 missed ticks, ZERO log bytes, a
# dead market-data feed, and every connection cheerfully reading Connected.
#
#   * A 20s bounded Dispatcher.Invoke was ALREADY in place for exactly this, and it did not release.
#     Do not assume the next bound will either.
#   * The relauncher was structurally blind: it acts when NinjaTrader.exe is DOWN, and the process
#     was up and responsive on its main dispatcher the whole time.
#
# THE STRUCTURAL LESSON, which is the entire reason this file is a separate process:
#   A watchdog whose only failure signal is its own log cannot report its own death, because the
#   thing that would report it is the thing that died. Health has to be proven by a signal that
#   STOPS, read by something that is not in that process. Here, ABSENCE IS THE ALARM.
#
# ============================================================================================
# DIVISION OF LABOUR
# ============================================================================================
#   Guard-Relaunch.ps1  owns "NinjaTrader is DOWN": starts it through the shared start routine, with
#                       its own backoff and the stuck-at-login detector.
#   this overwatch      owns "NinjaTrader is UP but SICK": prove it, then KILL the process and let the
#                       relauncher bring it back. It starts NinjaTrader itself ONLY when the relauncher
#                       is not running (its heartbeat is stale) - through the same routine, so the same
#                       rules (pause, lock screen, backoff, refused password, one start at a time) hold.
#
# ============================================================================================
# THE ONE RULE THIS SCRIPT MUST NEVER BREAK
# ============================================================================================
#   EVERY probe must be bounded. This script exists because something blocked forever; an overwatch
#   that can itself block is worthless. So:
#     - never enumerate top-level windows or call GetWindowText: both block on WM_GETTEXT against a
#       non-pumping UI thread, and both hung during the incident. A useful SYMPTOM but a fatal probe.
#     - every bridge call and every web call takes an explicit timeout.
#     - file reads only ever touch local paths.
#
# Protection (guard.json) decides whether each remedy ACTS or only ALERTS:
#   restartWhenFrozen  kill a wedged NinjaTrader             reconnect           reconnect what was up
#   reenableStrategies switch back on what was running       flattenUnprotected  close a naked position first
#   crashDumps         save a crash report before the kill
# The checks and records always run: an alert has to know what it would have done.
#
#   Guard-Overwatch.ps1 [-GuardHome DIR]       one scheduled cycle
#   Guard-Overwatch.ps1 -Report                the checks once, as JSON; reads only, never acts
#   Guard-Overwatch.ps1 -TestAlert             push a test alert from THIS machine
#   Guard-Overwatch.ps1 -WhatIfRemedy          report what it WOULD do, never touch the process

[CmdletBinding()]
param(
    [string]$GuardHome,
    # Report what it WOULD do and never touch the process.
    [switch]$WhatIfRemedy,
    # Run the checks once and print the verdict as JSON. Reads only; never acts, never writes.
    [switch]$Report,
    # Push a test alert through the REAL configured path. Run it ON the watched box: an alert path
    # verified from another machine is not verified.
    [switch]$TestAlert,
    # Define the functions and stop (for tests).
    [switch]$ImportOnly
)

$ErrorActionPreference = 'SilentlyContinue'
foreach ($m in 'Guard.Paths', 'Guard.Log', 'Guard.Config', 'Guard.Events', 'Guard.Alerts', 'Guard.Pause', 'Guard.Start', 'Guard.Overwatch') {
    Import-Module (Join-Path $PSScriptRoot "lib\$m.psm1")
}

# ---------------------------------------------------------------------------------------------
# Paths + settings
# ---------------------------------------------------------------------------------------------
$guard = Get-GuardConfig -GuardHome $GuardHome        # guard.json
$cfg   = Get-OverwatchSettings $guard                 # the flat settings the logic below reads
$base  = Get-Nt8DataDir $guard.nt8.dataDir            # NinjaTrader's folder

$stateDir   = Get-GuardPath state $GuardHome
$stateFile  = Join-Path $stateDir 'overwatch.state.json'
$cacheFile  = Join-Path $stateDir 'overwatch.strategies.json'
$connFile   = Join-Path $stateDir 'overwatch.connections.json'
$heartFile  = Join-Path $stateDir 'overwatch.heartbeat'
$stratLog   = Join-Path (Get-GuardPath logs $GuardHome) 'overwatch-strategies.log'
$dumpDir    = Get-GuardPath dumps $GuardHome
# The feed guard's and the NT8 link's own files, in NinjaTrader's folder.
$arHeart    = Join-Path $base 'AutoReconnect.heartbeat.json'
$arLog      = Join-Path $base 'AutoReconnect.log'
$brHeart    = Join-Path $base 'NT8Bridge\result\heartbeat.json'
$trigDir    = Join-Path $base 'NT8Bridge\trigger'
$resDir     = Join-Path $base 'NT8Bridge\result'

# -Report reads only: it never writes, not even a log line.
function Log([string]$m) { if (-not $Report) { Write-GuardLog -Component overwatch -Message $m -GuardHome $GuardHome } }

# ---------------------------------------------------------------------------------------------
# Alerting. Bounded, never throws, and a failure to alert must never abort the remedy.
# Priorities '3' / '4' / '5' are info / warn / alert.
# ---------------------------------------------------------------------------------------------
function Send-Alert([string]$title, [string]$body, [string]$priority) {
    Log "ALERT [$title] $body"
    Send-GuardAlert -Title $title -Body $body -Severity (ConvertTo-GuardSeverity $priority) -Config $guard -Component overwatch -GuardHome $GuardHome | Out-Null
}

# ONE page per episode. A fault that persists is seen every minute; paging it every minute trains the
# owner to mute the phone. An alert under $key is pushed again only after $hours; Clear-AlertOnce ends
# the episode, so the next occurrence pages at once.
function Send-AlertOnce([string]$key, [string]$title, [string]$body, [string]$priority, [double]$hours = 6) {
    $f = Join-Path $stateDir 'overwatch.alerts.json'
    $m = @{}
    if (Test-Path $f) { try { (Get-Content $f -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $m[$_.Name] = [string]$_.Value } } catch { } }
    $last = $null
    if ($m.ContainsKey($key)) { try { $last = [DateTime]::Parse($m[$key], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() } catch { } }
    if ($null -ne $last -and ([DateTime]::UtcNow - $last).TotalHours -lt $hours) {
        Log "alert not repeated (paged $([math]::Round(([DateTime]::UtcNow - $last).TotalMinutes))min ago): [$title]"
        return
    }
    $m[$key] = [DateTime]::UtcNow.ToString('o')
    try { ($m | ConvertTo-Json) | Out-File -FilePath $f -Encoding utf8 } catch { }
    Send-Alert $title $body $priority
}
function Clear-AlertOnce([string]$prefix) {
    $f = Join-Path $stateDir 'overwatch.alerts.json'
    if (-not (Test-Path $f)) { return }
    $m = @{}
    try { (Get-Content $f -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { if (-not $_.Name.StartsWith($prefix)) { $m[$_.Name] = [string]$_.Value } } } catch { }
    try { ($m | ConvertTo-Json) | Out-File -FilePath $f -Encoding utf8 } catch { }
}

# ---------------------------------------------------------------------------------------------
# State (consecutive fault counts, cooldown, pending restore)
# ---------------------------------------------------------------------------------------------
function Read-State {
    # feedEpisodeUtc: set while a feed outage is in flight, cleared once the strategies it knocked out
    # have been put back. It is what lets the drift check fire exactly ONCE per episode instead of
    # polling the Control Center grid every cycle - that poll is the operation that wedged the Control
    # Center on 2026-08-25, so it stays rare and evidence-driven.
    $s = [ordered]@{ consecutive = 0; fault = ''; lastRemedyUtc = ''; pendingRestore = $false; killedAtUtc = ''
                     restoredForStart = ''; verifyNames = ''; verifyUntil = ''; lastAliveUtc = ''
                     feedEpisodeUtc = ''; connAttemptUtc = ''; disableHandledUtc = ''
                     restoreDeferSinceUtc = ''; lockHoldAlertUtc = ''; downAlerted = ''; lastBackupUtc = ''
                     dupScanLocal = ''; startNote = ''; noFeedGuardNoted = '' }
    if (Test-Path $stateFile) {
        try { $j = Get-Content $stateFile -Raw | ConvertFrom-Json
              foreach ($k in @($s.Keys)) { if ($null -ne $j.$k) { $s[$k] = $j.$k } } } catch { }
    }
    return $s
}
function Write-State($s) {
    try { ($s | ConvertTo-Json -Depth 4) | Out-File -FilePath $stateFile -Encoding utf8 } catch { }
}

# Did the feed guard already run its whole recovery ladder and blame the FEED SOURCE? It reports that
# verdict only after re-subscribe -> disconnect -> reconnect has been tried and the feed is STILL dead,
# i.e. exactly when restarting NinjaTrader is useless. Returns the verdict's UTC time, or $null.
# Its structured events (kind feed.source-outage) - and ONLY those once its heartbeat says it writes them;
# an older feed guard that writes none is read from its log sentence. The heartbeat's `rung` cannot
# tell: it reads 0 both for "idle" and for "exhausted, in the post-alert cooldown".
function Get-SourceOutageVerdictUtc {
    $cut = [DateTime]::UtcNow.AddMinutes(-[double]$cfg.sourceVerdictMaxAgeMinutes)
    try {
        $ev = Get-FeedEventUtc -GuardHome $GuardHome -Nt8Dir $base -KindPattern '^feed\.source-outage$' -SinceUtc $cut
        if ($null -ne $ev) { return $ev }
    } catch { }
    if (Test-FeedGuardWritesEvents $arHeart) { return $null }
    if (-not (Test-Path $arLog)) { return $null }
    try {
        $hit = $null
        foreach ($line in (Get-Content $arLog -Tail 400 -ErrorAction Stop)) {
            if ($line -notmatch 'feed likely down at the source') { continue }
            if ($line -match '^(\S+)\s') {
                try {
                    $ts = [DateTime]::Parse($matches[1]).ToUniversalTime()
                    if ($ts -gt $cut) { $hit = $ts }
                } catch { }
            }
        }
        return $hit
    } catch { return $null }
}

# When did something last DISABLE strategies without anyone asking? Returns the newest marker's UTC
# time inside the window, or $null. The feed guard's events (kind feed.strategies-disabled) - and ONLY
# those once its heartbeat says it writes them; an older feed guard is read from its log sentences, each
# a case where NinjaTrader turns strategies off by itself:
#   "Connected/ConnectionLost"            - the link flapped; NinjaTrader disables on its own, before
#                                           any watchdog acts. This is what did it on 2026-09-09.
#   "NT8 will DISABLE running strategies" - the feed guard's own disconnect rung.
#   "(strategies disabled)"               - its recovery line confirming the same.
#
# ! Evidence-driven ON PURPOSE. No marker means no action, so a strategy you switch off by hand is
#   never switched back on - which is the only thing separating "restore" from "fight the operator".
function Get-InvoluntaryDisableUtc {
    $cut = [DateTime]::UtcNow.AddMinutes(-[double]$cfg.strategyDisableMarkerWindowMinutes)
    $hit = $null
    try { $hit = Get-FeedEventUtc -GuardHome $GuardHome -Nt8Dir $base -KindPattern '^feed\.strategies-disabled$' -SinceUtc $cut } catch { }
    if (Test-FeedGuardWritesEvents $arHeart) { return $hit }
    if (-not (Test-Path $arLog)) { return $hit }
    try {
        foreach ($line in (Get-Content $arLog -Tail 400 -ErrorAction Stop)) {
            if ($line -notmatch 'Connected/ConnectionLost' -and
                $line -notmatch 'NT8 will DISABLE running strategies' -and
                $line -notmatch '\(strategies disabled\)') { continue }
            if ($line -match '^(\S+)\s') {
                try {
                    $ts = [DateTime]::Parse($matches[1]).ToUniversalTime()
                    if ($ts -gt $cut -and ($null -eq $hit -or $ts -gt $hit)) { $hit = $ts }
                } catch { }
            }
        }
        return $hit
    } catch { return $hit }
}

function Get-AgeSeconds([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    try { return [math]::Round(([DateTime]::UtcNow - (Get-Item $path).LastWriteTimeUtc).TotalSeconds, 1) } catch { return $null }
}

# A heartbeat that has not been written since THIS NinjaTrader started belongs to an add-on that is not
# running in it at all - not loaded, removed, or failed to compile after an update. That is not a wedge,
# and restarting cannot fix it: restarting every cooldown, forever, would only knock the strategies off
# each time. ($ntStartUtc $null = unknown -> $false.)
function Test-HeartbeatPredatesStart([string]$path, $staleSeconds, $ntStartUtc) {
    if ($null -eq $ntStartUtc -or -not (Test-Path $path)) { return $false }
    try {
        $w = (Get-Item $path).LastWriteTimeUtc
        return ((([DateTime]::UtcNow - $w).TotalSeconds -gt $staleSeconds) -and ($w -lt $ntStartUtc))
    } catch { return $false }
}

# Are NinjaTrader's add-ons loaded? Either one's fresh heartbeat proves it - the feed guard's or the
# NT8 link's. Requiring the feed guard alone left a restart "pending" forever on a box without it, and
# a pending restore holds back the strategy record and the backups.
function Test-AddonsFresh {
    $ar = Get-AgeSeconds $arHeart
    $br = Get-AgeSeconds $brHeart
    return (($null -ne $ar -and $ar -le $cfg.guardianStaleSeconds) -or ($null -ne $br -and $br -le $cfg.bridgeStaleSeconds))
}

# ---------------------------------------------------------------------------------------------
# Market-hours gate. A feed is SUPPOSED to be silent at a scheduled CME close; alerting then is how
# a watchdog trains its owner to ignore it. Mirrors the feed guard's own gate (daily 17:00-18:00 ET,
# Fri 17:00 -> Sun 18:00 ET). Fails OPEN, so a timezone problem never silences a real fault.
# ---------------------------------------------------------------------------------------------
function Test-ScheduledClosed {
    try {
        $tz = [TimeZoneInfo]::FindSystemTimeZoneById($cfg.marketTimeZoneId)
        $et = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $tz)
        $d = $et.DayOfWeek; $h = $et.TimeOfDay
        if ($d -eq 'Saturday') { return $true }
        if ($d -eq 'Friday'   -and $h -ge [TimeSpan]'17:00') { return $true }
        if ($d -eq 'Sunday'   -and $h -lt [TimeSpan]'18:00') { return $true }
        if ($h -ge [TimeSpan]'17:00' -and $h -lt [TimeSpan]'18:00') { return $true }
        return $false
    } catch { return $false }
}

# ---------------------------------------------------------------------------------------------
# -Report must ALWAYS answer and must NEVER act. Every early exit routes through here when -Report is
# set, and this function only reads: a diagnostic that goes silent in the states you most want to
# diagnose looks broken, and a read-only flag that acts is a trap.
# ---------------------------------------------------------------------------------------------
function Write-VerdictAndExit([object]$proc, [string]$note) {
    $arA = Get-AgeSeconds $arHeart
    $brA = Get-AgeSeconds $brHeart
    $h = $null
    if (Test-Path $arHeart) { try { $h = Get-Content $arHeart -Raw | ConvertFrom-Json } catch { } }
    $resp = $null; if ($proc) { try { $resp = $proc.Responding } catch { } }
    ([ordered]@{
        base                         = $base
        utc                          = [DateTime]::UtcNow.ToString('o')
        note                         = $note
        ntRunning                    = [bool]$proc
        ntPid                        = $(if ($proc) { $proc.Id } else { $null })
        responding                   = $resp
        autoReconnectHeartbeatAgeSec = $arA
        autoReconnectRung            = $(if ($h) { $h.rung } else { $null })
        autoReconnectBuild           = $(if ($h) { $h.build } else { '' })
        bridgeHeartbeatAgeSec        = $brA
        lastL1AgeSec                 = $(if ($h) { $h.lastL1AgeSeconds } else { $null })
        marketScheduledClosed        = (Test-ScheduledClosed)
        overwatchHeartbeatAgeSec     = (Get-AgeSeconds $heartFile)
        relaunchHeartbeatAgeSec      = (Get-AgeSeconds (Join-Path $stateDir 'relaunch.heartbeat'))
    } | ConvertTo-Json -Depth 4)
    exit 0
}

# ---------------------------------------------------------------------------------------------
# Drive the NT8 link over its FILE protocol: JSON files in trigger/ and result/, no socket and no
# Python needed. Always bounded.
# ---------------------------------------------------------------------------------------------
function Invoke-Bridge([string]$kind, [hashtable]$extra, [int]$timeoutSec) {
    if (-not (Test-Path $trigDir)) { return $null }
    $id  = [guid]::NewGuid().ToString('N')
    $req = [ordered]@{ id = $id; kind = $kind }
    if ($extra) { foreach ($k in $extra.Keys) { $req[$k] = $extra[$k] } }
    $reqPath = Join-Path $trigDir "$kind`_$id.json"
    $resPath = Join-Path $resDir  "$kind`_$id.json"
    try { ($req | ConvertTo-Json -Compress) | Out-File -FilePath $reqPath -Encoding utf8 } catch { return $null }
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path $resPath) {
            try { $t = Get-Content $resPath -Raw; Remove-Item $resPath -Force; return ($t | ConvertFrom-Json) } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    # Clean up our own unanswered request so it cannot fire late against a fresh NinjaTrader.
    try { Remove-Item $reqPath -Force } catch { }
    Log "bridge: '$kind' did not answer within ${timeoutSec}s"
    return $null
}

# ---------------------------------------------------------------------------------------------
# A CRASH REPORT (minidump) BEFORE KILLING NINJATRADER - Protection 'crash reports', off by default.
#
# * WHY: on 2026-08-25 NinjaTrader hung mid-trade. Finding the cause took a source audit plus a 76-day
#   event-log history to land on one synchronous Dispatcher.Invoke. A dump of the wedged process
#   would have shown it in one look - the UI thread sitting in a wait, and what it was waiting on.
#
# ! Windows' LocalDumps does NOT cover this. That facility is for CRASHES; a hang is reported through
#   a different path. The only reliable hang dump is the one taken while the process is still wedged
#   - exactly the moment before we kill it. We are about to destroy the evidence; capture it first.
#
# Dumping a HUNG process works: MiniDumpWriteDump reads the target's memory and does not need it to
# pump messages. BOUNDED like every other probe: the capture runs in a background job with a hard
# timeout - a dump that stalls delays the remedy, never prevents it.
# ---------------------------------------------------------------------------------------------
function Save-NT8Dump([int]$procId, [string]$reason) {
    if (-not $cfg.dumpBeforeKill) { return $null }
    try { if (-not (Test-Path $dumpDir)) { New-Item -ItemType Directory -Path $dumpDir -Force | Out-Null } } catch { return $null }

    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $path  = Join-Path $dumpDir ("NinjaTrader_{0}_{1}.dmp" -f $stamp, ($reason -replace '[^A-Za-z0-9]','_'))

    # MINIDUMP_TYPE. Full: WithFullMemory|WithHandleData|WithFullMemoryInfo|WithThreadInfo = 0x1806,
    # which a managed-heap analysis needs. Small: WithHandleData|WithThreadInfo = 0x1004 - native
    # stacks only, but a small fraction of the size.
    $dumpType = $(if ($cfg.dumpFullMemory) { 0x1806 } else { 0x1004 })

    Log "dump: capturing NinjaTrader pid=$procId ($(if($cfg.dumpFullMemory){'full memory'}else{'stacks only'})) -> $([IO.Path]::GetFileName($path))"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $job = Start-Job -ScriptBlock {
        param($procId, $path, $dumpType)
        $sig = @'
[DllImport("dbghelp.dll", SetLastError=true)]
public static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, IntPtr hFile, int dumpType,
                                            IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
'@
        $t = Add-Type -MemberDefinition $sig -Name 'Dbg' -Namespace 'NT8Guard' -PassThru
        $p = Get-Process -Id $procId -ErrorAction Stop
        $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $ok = $t::MiniDumpWriteDump($p.Handle, [uint32]$procId, $fs.SafeFileHandle.DangerousGetHandle(), $dumpType,
                                        [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)
            if (-not $ok) { throw "MiniDumpWriteDump failed, win32=$([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
        } finally { $fs.Dispose() }
        return $true
    } -ArgumentList $procId, $path, $dumpType

    $done = Wait-Job $job -Timeout $cfg.dumpTimeoutSeconds
    if (-not $done) {
        Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
        Log "dump: ABANDONED after $($cfg.dumpTimeoutSeconds)s - proceeding with the remedy anyway"
        return $null
    }
    $err = $job.ChildJobs[0].Error
    Receive-Job $job -ErrorAction SilentlyContinue | Out-Null
    Remove-Job $job -Force -ErrorAction SilentlyContinue

    if ($err -and $err.Count -gt 0) { Log "dump: FAILED - $($err[0].ToString())"; return $null }
    $mb = 0; try { $mb = [math]::Round((Get-Item $path).Length/1MB,1) } catch { }
    Log ("dump: wrote {0} MB in {1}s -> {2}" -f $mb, [math]::Round($sw.Elapsed.TotalSeconds,1), $path)

    # Keep only the newest N. A full dump runs to gigabytes; unbounded these would fill the disk,
    # which is itself a documented cause of NinjaTrader instability.
    try {
        Get-ChildItem $dumpDir -Filter '*.dmp' -File | Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -Skip ([int]$cfg.dumpKeep) | ForEach-Object { Remove-Item $_.FullName -Force; Log "dump: pruned $($_.Name)" }
    } catch { }
    return $path
}

# ---------------------------------------------------------------------------------------------
# STRATEGY RECORD + RESTORE
#
# NinjaTrader does NOT restore enabled strategies across an app restart: every Control Center
# strategy comes back off. The relauncher only gets the process running again.
#
# ! ORDER MATTERS AND IS EASY TO GET FATALLY WRONG: after a restart the grid reads ALL DISABLED, so
#   refreshing the record first would overwrite the very list needed to restore from - destroying the
#   evidence and then "correctly" restoring nothing. RESTORE FIRST, RECORD SECOND, always.
# ---------------------------------------------------------------------------------------------
function Read-CachedEnabled {
    if (-not (Test-Path $cacheFile)) { return $null }
    try { return (Get-Content $cacheFile -Raw | ConvertFrom-Json) } catch { return $null }
}

# The snapshot answers "what should be running". The appended trail answers "when did it stop, and
# did anyone mean it" - the question you actually have at 3am, which one overwritten snapshot can
# never answer.
# ! IDENTITY IS name+account, NOT name. Strategy names are NOT unique in the Control Center grid -
#   observed live 2026-08-25: two rows with one name, one on a Sim account (enabled) and one on a
#   FUNDED account (deliberately off). A name-only record cannot tell them apart, and enabling by name
#   clicks whichever row the grid yields first - so a restore could switch on a strategy against a
#   funded account. Never key an action on a non-unique identifier.
function StratKey($row) { "$($row.name)@$($row.account)" }

function Update-StrategyCache([object]$rows, [string]$why) {
    $enabled = @($rows | Where-Object { $_.enabled } | ForEach-Object {
        [ordered]@{ name = $_.name; account = $_.account; instrument = $_.instrument } })
    $prev = Read-CachedEnabled
    $prevSet = @()
    if ($prev) { $prevSet = @($prev.enabled | ForEach-Object { if ($_ -is [string]) { "$_@" } else { StratKey $_ } }) }
    $curSet  = @($enabled | ForEach-Object { StratKey $_ })

    # ! AN EMPTY READ MUST NEVER ERASE A NON-EMPTY RECORD.
    #   2026-09-08: a fault fired, and the pre-kill poll answered status=ok with ZERO enabled rows
    #   while eight strategies were in fact running and two positions were open. The record was
    #   overwritten with [], the restore 59s later logged "nothing recorded as enabled", and the box
    #   came back with every strategy off. A status=ok grid read is NOT the same as a trustworthy one.
    #   The guard is deliberately asymmetric: an empty record can never restore anything, so keeping
    #   a stale non-empty one costs nothing that restoreMaxAgeMinutes does not already bound, while
    #   erasing a good one is unrecoverable. `utc` is NOT refreshed when refusing, so a record that
    #   really is obsolete still ages out by itself rather than being pinned fresh forever.
    $prevEnabled = @(); if ($prev) { $prevEnabled = @($prev.enabled) }
    $refused = $false
    $streak  = 0
    if ($enabled.Count -eq 0 -and $prevEnabled.Count -gt 0) {
        if ($prev.PSObject.Properties['emptySeen']) { $streak = [int]$prev.emptySeen }
        # pre-kill is taken mid-fault, moments before we destroy the process, and is the least
        # trustworthy read this script ever takes. It never gets to clear the record. Any other
        # caller must see the same empty answer twice before it counts.
        if ($why -eq 'pre-kill') {
            # ! An untrusted read must not COUNT AS EVIDENCE either, or one mid-fault read plus ONE
            #   healthy read would reach the two-strike threshold.
            $refused = $true
        } else {
            $streak++
            $refused = ($streak -lt 2)
        }
    }

    $added   = @($curSet  | Where-Object { $prevSet -notcontains $_ })
    $removed = @($prevSet | Where-Object { $curSet  -notcontains $_ })
    if ($added.Count -or $removed.Count -or $null -eq $prev) {
        $line = "$([DateTime]::UtcNow.ToString('o'))  [$why] enabled=[$($curSet -join ', ')]"
        if ($added.Count)   { $line += "  ADDED=[$($added -join ', ')]" }
        if ($removed.Count) { $line += "  REMOVED=[$($removed -join ', ')]" }
        if ($refused)       { $line += "  (READ REJECTED, record kept, empty streak $streak)" }
        try { $line | Out-File -FilePath $stratLog -Append -Encoding utf8 } catch { }
        if ($added.Count -or $removed.Count) { Log "strategies changed [$why]: +[$($added -join ', ')] -[$($removed -join ', ')]" }
    }

    if ($refused) {
        Log "record [$why]: grid returned ZERO enabled while $($prevEnabled.Count) are recorded - REFUSING to erase the record (empty streak $streak)"
        try { ([ordered]@{ utc = $prev.utc; why = $prev.why; enabled = $prevEnabled; emptySeen = $streak } |
                ConvertTo-Json -Depth 4) | Out-File -FilePath $cacheFile -Encoding utf8 } catch { }
        return $prevEnabled
    }
    if ($streak -ge 2) { Log "record [$why]: zero enabled confirmed on $streak consecutive reads - accepting the empty record" }

    try { ([ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); why = $why; enabled = $enabled; emptySeen = 0 } | ConvertTo-Json -Depth 4) |
            Out-File -FilePath $cacheFile -Encoding utf8 } catch { }
    return $enabled
}

function Read-CachedConnections {
    if (-not (Test-Path $connFile)) { return $null }
    try { return (Get-Content $connFile -Raw | ConvertFrom-Json) } catch { return $null }
}

# What was CONNECTED while the box was healthy. This is the whole point: the feed guard cannot tell a
# connection you parked on purpose from one that simply never came up after a restart, because both
# read `Disconnected`. A record taken while healthy can.
# Same asymmetry as the strategy record: an empty read never erases a non-empty record. Unlike the
# grid poll this is cheap - no UI dispatcher - so it can run every cycle.
function Update-ConnectionCache([object]$rows, [string]$why) {
    $connected = @($rows | Where-Object { "$($_.status)" -eq 'Connected' } | ForEach-Object { "$($_.name)" })
    $prev = Read-CachedConnections
    $prevList = @(); if ($prev) { $prevList = @($prev.connected) }

    if ($connected.Count -eq 0 -and $prevList.Count -gt 0) {
        $streak = 0; if ($prev.PSObject.Properties['emptySeen']) { $streak = [int]$prev.emptySeen }
        # An untrusted (pre-kill) read refuses WITHOUT counting as evidence - see Update-StrategyCache.
        $refuseIt = $true
        if ($why -ne 'pre-kill') { $streak++; $refuseIt = ($streak -lt 2) }
        if ($refuseIt) {
            Log "connections [$why]: read ZERO connected while $($prevList.Count) are recorded - keeping the record (empty streak $streak)"
            try { ([ordered]@{ utc = $prev.utc; why = $prev.why; connected = $prevList; emptySeen = $streak } |
                    ConvertTo-Json -Depth 4) | Out-File -FilePath $connFile -Encoding utf8 } catch { }
            return $prevList
        }
    }
    $added   = @($connected | Where-Object { $prevList -notcontains $_ })
    $dropped = @($prevList  | Where-Object { $connected -notcontains $_ })
    if ($added.Count -or $dropped.Count) {
        Log "connections changed [$why]: +[$($added -join ', ')] -[$($dropped -join ', ')]"
    }
    try { ([ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); why = $why; connected = $connected; emptySeen = 0 } |
            ConvertTo-Json -Depth 4) | Out-File -FilePath $connFile -Encoding utf8 } catch { }
    return $connected
}

# Bring back the connections that were up before the outage. Returns $true when handled.
#
# ! MUST RUN BEFORE THE STRATEGY RESTORE. Re-enabling a strategy whose account sits on a dead
#   connection produces something that looks restored and trades nothing.
# ! `reconnect` is safe here: the NT8 link only calls Connection.Connect() when the connection is NOT
#   already connected, so it never cycles a live one and cannot disable strategies the way a
#   Disconnect would.
# Protection 'reconnect' off: say which are down, never connect.
function Invoke-ConnectionRestore([string]$reason, $upSeconds) {
    if (-not $cfg.restoreConnections) { return $true }
    $rec = Read-CachedConnections
    $want = @(); if ($null -ne $rec) { $want = @($rec.connected) }
    if ($want.Count -eq 0) { return $true }

    # ! WAIT FOR REAL UPTIME BEFORE CONNECTING. The add-on loads ~8s after launch, and the strategy
    #   restore rightly fires on that; connections wait until NinjaTrader has finished coming up. Until
    #   then this reports NOT-READY - without burning the cooldown and without paging anyone, because
    #   there is nothing wrong yet. (Three "STILL DOWN after 30s" at 38-53s after launch were first blamed
    #   on Connect() no-oping at start-up; in 2026-10 the cause turned out to be the request itself - see
    #   the reconnect call below. The wait stays: a connect into a platform still loading gains nothing.)
    if ($null -ne $upSeconds -and $upSeconds -lt $cfg.connRestoreMinUptimeSeconds) {
        Log ("connections [{0}]: NinjaTrader is {1}s old (needs {2}s before a connect sticks) - not ready, retrying next cycle" -f `
             $reason, [math]::Round($upSeconds), $cfg.connRestoreMinUptimeSeconds)
        return $false
    }

    # Bounded and spaced: a failed connect must page a human, not become a retry loop against a broker.
    if ($state.connAttemptUtc) {
        try {
            $sinceMin = ([DateTime]::UtcNow - [DateTime]::Parse($state.connAttemptUtc).ToUniversalTime()).TotalMinutes
            if ($sinceMin -lt $cfg.connRetryCooldownMinutes) {
                Log "connections [$reason]: last attempt was $([math]::Round($sinceMin))min ago (cooldown $($cfg.connRetryCooldownMinutes)min) - not retrying yet"
                return $true
            }
        } catch { }
    }

    $c = Invoke-Bridge 'connections' @{} $cfg.bridgeTimeoutSeconds
    if ($null -eq $c -or $c.status -ne 'ok') { Log "connections [$reason]: not readable yet - will retry next cycle"; return $false }

    $down = @(); $gone = @()
    foreach ($n in $want) {
        $row = @($c.connections | Where-Object { "$($_.name)" -eq $n })[0]
        if ($null -eq $row) { $gone += $n; continue }          # no longer configured in NinjaTrader
        if ("$($row.status)" -eq 'Connected') { continue }
        $down += $n
    }
    if ($gone.Count) { Log "connections [$reason]: recorded but no longer configured, skipped: $($gone -join ', ')" }
    if ($down.Count -eq 0) { return $true }

    if ($cfg.dryRunRemedy) { Log "connections [$reason]: DRY RUN, would connect: $($down -join ', ')"; return $true }
    if ($cfg.reconnect -eq $false) {
        $state.connAttemptUtc = [DateTime]::UtcNow.ToString('o')
        Log "connections [$reason]: were connected before and are DOWN now; reconnecting is set to alert only: $($down -join ', ')"
        Send-Alert 'NT8 connections down' ("These were connected before and are down now. Reconnecting is set to alert only in Protection, so connect them by hand, ONE AT A TIME:`n" + ($down -join "`n")) '5'
        return $true
    }

    $state.connAttemptUtc = [DateTime]::UtcNow.ToString('o')
    Log "connections [$reason]: were connected before the outage but are DOWN now -> connecting: $($down -join ', ')"

    # ! ONE AT A TIME, each confirmed before the next.
    #   NinjaTrader restarts every running strategy when a connection connects, and that restart is not
    #   serialized: connects 2-8s apart on 2026-09-21 left SIX grid rows with TWO live instances each
    #   (one then entered 10 contracts for a 5-lot template) and, earlier the same afternoon, silently
    #   dropped one. Restores normally run with the grid parked, where it cannot bite; this keeps it
    #   safe if that ever stops being true.
    $stillDown = @()
    foreach ($n in $down) {
        # The NT8 link reads the name from "connection" (nt8bridge reconnect.py's contract). Until 2026-10 this
        # sent "name": the link answered "reconnect requires a connection name", nothing logged that answer,
        # and every reconnect read as "STILL DOWN after 30s".
        $r = Invoke-Bridge 'reconnect' @{ connection = $n } $cfg.bridgeTimeoutSeconds
        if ($null -eq $r) { Log "connections [$reason]: '$n' - reconnect did not answer" }
        elseif ($r.status -ne 'ok') {
            Log "connections [$reason]: '$n' - the NT8 link refused the reconnect: $(@($r.errors | ForEach-Object { $_.message }) -join '; ')"
        }
        elseif ($r.connectThrew -or $r.connectTimedOut) {
            # Name the failure. "STILL DOWN" alone cost three post-mortems before anyone knew whether
            # Connect() had even been reached.
            Log "connections [$reason]: '$n' - connect threw=$($r.connectThrew) timedOut=$($r.connectTimedOut) $($r.connectError)"
        }
        # Connecting is asynchronous; give this one a bounded settle before touching the next.
        $deadline = [DateTime]::UtcNow.AddSeconds($cfg.connSettleSeconds)
        $up = $false
        while (-not $up -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Seconds 5
            $c2 = Invoke-Bridge 'connections' @{} $cfg.bridgeTimeoutSeconds
            if ($null -eq $c2 -or $c2.status -ne 'ok') { continue }
            $row2 = @($c2.connections | Where-Object { "$($_.name)" -eq $n })[0]
            if ($null -ne $row2 -and "$($row2.status)" -eq 'Connected') { $up = $true }
        }
        if (-not $up) { $stillDown += $n }
    }

    $back = @($down | Where-Object { $stillDown -notcontains $_ })
    if ($back.Count) { Log "connections [$reason]: reconnected: $($back -join ', ')" }
    if ($stillDown.Count) {
        # ! Say this out loud. A connect that FAILS is exactly what the feed guard then marks "parked"
        #   and never touches again, which is how a connection stays dead all session in silence.
        Log "connections [$reason]: STILL DOWN after $($cfg.connSettleSeconds)s: $($stillDown -join ', ')"
        Send-Alert 'NT8 connections did NOT come back' ("These were connected before the outage and are still down after a reconnect attempt. The feed guard will NOT retry them by itself - a failed connect is classed 'parked'. Connect them by hand:`n" + ($stillDown -join "`n")) '5'
    } elseif ($back.Count) {
        Send-Alert 'NT8 connections restored' ("Reconnected after the outage: " + ($back -join ', ')) '3'
    }
    return $true
}

# What one account holds on one strategy's instrument: 'flat', 'protected' (a position with working
# orders - its stop/target) or 'naked' (a position with nothing working against it). @{ State; What; Orders }.
function Get-PositionState($acct, $t) {
    $liveStates = @('Working','Accepted','Submitted','TriggerPending','PartFilled','ChangeSubmitted','CancelSubmitted')
    $a = @($acct.accounts | Where-Object { $_.name -eq $t.account })[0]
    $pos = $null
    if ($null -ne $a) { $pos = @($a.positions | Where-Object { $_.instrument -eq $t.instrument -and "$($_.marketPosition)" -ne 'Flat' })[0] }
    if ($null -eq $pos) { return [pscustomobject]@{ State = 'flat'; What = ''; Orders = 0 } }
    $prot = @()
    if ($null -ne $a) { $prot = @($a.workingOrders | Where-Object { $_.instrument -eq $t.instrument -and $liveStates -contains "$($_.state)" }) }
    return [pscustomobject]@{ State = $(if ($prot.Count -gt 0) { 'protected' } else { 'naked' })
                              What = "$($t.name)@$($t.account) [$($t.instrument) $($pos.marketPosition) $($pos.quantity)]"
                              Orders = $prot.Count }
}

# Returns $true when this restart has been dealt with (restored, or nothing to do), $false to retry
# on a later cycle - so a grid that is not readable yet does not burn the one attempt.
# Protection 're-enable strategies' off: say which were running and are off now, never switch them on.
function Invoke-StrategyRestore([string]$reason, $upSeconds) {
    # CONNECTIONS FIRST, always. A strategy re-enabled onto a dead connection looks restored and
    # trades nothing. A connect that FAILED does not block the strategies - the ones on healthy
    # connections should still come back - but "not ready yet" is different: that is a wait, and
    # waiting is the whole point. Restoring strategies first and connecting afterwards is what forces
    # someone to connect by hand with the grid live, and NinjaTrader duplicates strategy instances
    # when that happens (2026-09-21). So a NOT-READY connection restore defers this whole pass; the
    # caller retries next cycle, and the deferral is bounded so a broker outage cannot keep the grid
    # off all session.
    if (-not (Invoke-ConnectionRestore $reason $upSeconds)) {
        $waited = $null
        if ($state.restoreDeferSinceUtc) { try { $waited = ([DateTime]::UtcNow - [DateTime]::Parse($state.restoreDeferSinceUtc).ToUniversalTime()).TotalMinutes } catch { } }
        if ($null -eq $waited) { $state.restoreDeferSinceUtc = [DateTime]::UtcNow.ToString('o'); $waited = 0 }
        if ($waited -lt $cfg.restoreDeferMaxMinutes) {
            Log ("restore [{0}]: deferred {1}min - connections are not restorable yet; strategies stay OFF until they are (max {2}min)" -f `
                 $reason, [math]::Round($waited), $cfg.restoreDeferMaxMinutes)
            return $false
        }
        Log "restore [$reason]: connections still not restorable after $($cfg.restoreDeferMaxMinutes)min - restoring strategies anyway"
        Send-Alert 'NT8 strategies restored with connections DOWN' ("The connection restore could not run for $($cfg.restoreDeferMaxMinutes) minutes, so the strategies are being re-enabled without it. Connect the missing connections BY HAND ONE AT A TIME, and only after disabling the grid - NinjaTrader restarts every running strategy on each connect and duplicates them when two land together.") '5'
    }
    $state.restoreDeferSinceUtc = ''

    $cache = Read-CachedEnabled
    $names = @(); if ($null -ne $cache) { $names = @($cache.enabled) }
    if ($names.Count -eq 0) {
        Log "restore [$reason]: nothing recorded as enabled; nothing to restore"
        # If WE killed NinjaTrader, an empty record is not a quiet no-op - it means the box has just
        # come back with every strategy off and nobody has been told. That is precisely how
        # 2026-09-08 went unnoticed for four hours. An empty record after our own kill is a FAULT.
        if ($reason -eq 'we restarted it') {
            Send-Alert 'NT8 restarted with NO strategies restored' ("We killed and relaunched NinjaTrader but the enabled-strategy record was empty, so nothing was turned back on. The box is running with every strategy OFF until someone acts.") '5'
        }
        return $true
    }

    if ($null -ne $cache.utc) {
        try {
            $ageMin = ([DateTime]::UtcNow - [DateTime]::Parse($cache.utc).ToUniversalTime()).TotalMinutes
            if ($ageMin -gt $cfg.restoreMaxAgeMinutes) {
                Log "restore [$reason]: record is $([math]::Round($ageMin))min old (max $($cfg.restoreMaxAgeMinutes)) - too stale to trust, NOT restoring"
                # Render the records ({name, account, instrument}), tolerating the legacy name-only format.
                $shown = @($names | ForEach-Object { if ($_ -is [string]) { $_ } elseif ($_.account) { "$($_.name)@$($_.account)" } else { $_.name } })
                Send-Alert 'NT8 restarted - strategies NOT restored' "Recorded enabled set is $([math]::Round($ageMin)) min old, past restoreMaxAgeMinutes. Left off deliberately: $($shown -join ', ')" '4'
                return $true
            }
        } catch { }
    }

    $r = Invoke-Bridge 'strategies' @{} $cfg.bridgeTimeoutSeconds
    if ($null -eq $r -or $r.status -ne 'ok') { Log "restore [$reason]: grid not readable yet - will retry next cycle"; return $false }

    # $targets carries account+instrument next to each name. The NT8 link can only ENABLE by name, but
    # the exposure gate below has to look at the right account's holdings, so both are kept.
    $missing = @(); $ambiguous = @(); $gone = @(); $targets = @()
    foreach ($rec in $names) {
        # Tolerate the old name-only record format.
        $recName = $(if ($rec -is [string]) { $rec } else { $rec.name })
        $recAcct = $(if ($rec -is [string]) { $null } else { $rec.account })

        $byName = @(@($r.strategies) | Where-Object { $_.name -eq $recName })
        if ($byName.Count -eq 0) { $gone += $recName; continue }

        $row = $null
        if ($recAcct) {
            # Account-qualified record: resolve the exact row.
            $row = @($byName | Where-Object { $_.account -eq $recAcct })[0]
            if ($null -eq $row) { $ambiguous += "$recName (account $recAcct no longer in the grid)"; continue }
            if ($row.enabled) { continue }
        } else {
            # Legacy name-only record. We cannot tell WHICH row it meant - so if any row of that name
            # is already enabled, treat it as satisfied rather than raising a false alarm.
            if (@($byName | Where-Object { $_.enabled }).Count -ge 1) { continue }
        }

        # ! Even having resolved the right ROW, we can only ask the NT8 link to enable it BY NAME - and
        #   a duplicated name makes that request ambiguous at the point of the click. Refuse. Turning
        #   on the wrong account's strategy is far worse than leaving one off and saying so.
        if ($byName.Count -gt 1) { $ambiguous += "$recName (name appears $($byName.Count)x: $(($byName | ForEach-Object { $_.account }) -join ', '))"; continue }
        if ($null -eq $row) { $row = $byName[0] }   # legacy name-only record, unique by the check above
        $missing += $recName
        $targets += [pscustomobject]@{ name = $recName; account = $row.account; instrument = $row.instrument }
    }

    if ($gone.Count)      { Log "restore [$reason]: no longer in the grid, skipped: $($gone -join ', ')" }
    if ($ambiguous.Count) {
        Log "restore [$reason]: AMBIGUOUS, refusing to guess: $($ambiguous -join ' | ')"
        Send-Alert 'NT8 restore SKIPPED (ambiguous)' ("These were running before the restart but cannot be safely targeted, because the name is not unique in the grid. Enable them by hand:`n" + ($ambiguous -join "`n")) '5'
    }
    if ($missing.Count -eq 0) { Log "restore [$reason]: nothing left to re-enable"; return $true }

    if ($cfg.reenableStrategies -eq $false) {
        # Alert only - but the exposure check still runs (read-only), so the alert can say which
        # accounts hold a position with nothing protecting it. That is the one fact the user needs first.
        $acctRO = Invoke-Bridge 'account' @{} $cfg.bridgeTimeoutSeconds
        $readable = ($null -ne $acctRO -and $acctRO.status -eq 'ok')
        $shown = @(); $nakedAny = $false
        foreach ($t in $targets) {
            if (-not $readable) { $shown += "$($t.name)@$($t.account) - positions could not be read"; continue }
            $x = Get-PositionState $acctRO $t
            switch ($x.State) {
                'flat'      { $shown += "$($t.name)@$($t.account) - no position" }
                'protected' { $shown += "$($x.What) - protected by $($x.Orders) working order(s); switching the strategy on would cancel them" }
                'naked'     { $shown += "$($x.What) - UNPROTECTED: no stop or target is working"; $nakedAny = $true }
            }
        }
        Log "restore [$reason]: were running and are OFF now; re-enabling is set to alert only: $($shown -join ' | ')"
        if ($nakedAny) {
            Send-Alert 'NT8 UNPROTECTED position - strategies OFF' ("These were running before ($reason) and are switched off now, and at least one account holds a position with NO stop or target. Close or protect it by hand first. Re-enabling is set to alert only in Protection:`n" + ($shown -join "`n")) '5'
        } else {
            Send-Alert 'NT8 strategies are OFF' ("These were running before ($reason) and are switched off now. Re-enabling is set to alert only in Protection, so switch them back on by hand - check each account's position first, because enabling a strategy cancels the stop/target it was holding:`n" + ($shown -join "`n")) '5'
        }
        return $true
    }

    # ---------------------------------------------------------------------------------------------
    # EXPOSURE GATE. Enabling a strategy CANCELS the stop/target it was holding - that is how
    # 2026-08-26 produced a naked position twice. So look at what each account is actually holding
    # before flipping anything on.
    # ---------------------------------------------------------------------------------------------
    if ($cfg.exposureGate -and $targets.Count -gt 0) {
        $acct = Invoke-Bridge 'account' @{} $cfg.bridgeTimeoutSeconds
        if ($null -eq $acct -or $acct.status -ne 'ok') {
            # ! Refuse rather than assume flat. "I could not see the positions" and "there are no
            #   positions" are the same empty answer, and acting on that confusion is the whole reason
            #   this file exists. Returning $false retries on a later cycle.
            Log "restore [$reason]: cannot read account state - refusing to re-enable blind, will retry"
            Send-AlertOnce 'restore:held' 'NT8 restore HELD (exposure unknown)' ("Could not read positions, so re-enabling was skipped rather than done blind. Strategies still OFF: " + ($missing -join ', ')) '5'
            return $false
        }

        $protected = @(); $naked = @(); $closed = @(); $failed = @(); $clear = @()

        foreach ($t in $targets) {
            $x = Get-PositionState $acct $t
            if ($x.State -eq 'flat') { $clear += $t; continue }
            $what = $x.What

            if ($x.State -eq 'protected') {
                # STILL PROTECTED. Enabling would cancel these orders and leave the position naked, so
                # the safe move is to leave the strategy OFF and keep the protection that exists.
                $protected += "$what protected by $($x.Orders) working order(s)"
                continue
            }

            # NAKED: a position with nothing working against it. Leaving it open risks a drawdown
            # breach exactly as much as closing it does, which is why this is not gated on the account
            # being a simulator - naked is naked.
            if (-not $cfg.flattenNakedBeforeEnable) { $naked += "$what - closing an unprotected position is set to alert only"; continue }
            if ($cfg.dryRunRemedy) { $naked += "$what - DRY RUN, no order sent"; continue }

            Log "restore [$reason]: $what is NAKED (no working orders) -> flattening before re-enable"
            $f = Invoke-Bridge 'flatten' @{ account = $t.account; instrument = $t.instrument } $cfg.bridgeTimeoutSeconds
            if ($null -eq $f) { $failed += "$what (flatten did not answer)"; continue }
            $closed += $t
        }

        # Believe the account, not the flatten's own reply: re-read and confirm each one is really flat
        # before enabling anything on it.
        if ($closed.Count -gt 0) {
            Start-Sleep -Seconds 3
            $after = Invoke-Bridge 'account' @{} $cfg.bridgeTimeoutSeconds
            foreach ($t in $closed) {
                $ok = $false
                if ($null -ne $after -and $after.status -eq 'ok') {
                    $a2 = @($after.accounts | Where-Object { $_.name -eq $t.account })[0]
                    $still = $null
                    if ($null -ne $a2) { $still = @($a2.positions | Where-Object { $_.instrument -eq $t.instrument -and "$($_.marketPosition)" -ne 'Flat' })[0] }
                    $ok = ($null -eq $still)
                }
                if ($ok) { $clear += $t; Log "restore [$reason]: $($t.name)@$($t.account) confirmed FLAT after the close" }
                else     { $failed += "$($t.name)@$($t.account) [$($t.instrument)] (flatten fired but it is NOT flat)" }
            }
        }

        if ($protected.Count) {
            Log "restore [$reason]: LEFT OFF, position still protected: $($protected -join ' | ')"
            Send-Alert 'NT8 strategies left OFF (position still protected)' ("Re-enabling these would CANCEL the stop/target currently holding the position, so they were left off on purpose. Flatten or take them over by hand:`n" + ($protected -join "`n")) '5'
        }
        if ($naked.Count) {
            Log "restore [$reason]: LEFT OFF, UNPROTECTED position: $($naked -join ' | ')"
            Send-Alert 'NT8 UNPROTECTED position - strategy left OFF' ("These accounts hold a position with NO stop or target working, and the strategy was left off. Close or protect the position by hand, then switch the strategy on:`n" + ($naked -join "`n")) '5'
        }
        if ($failed.Count) {
            Log "restore [$reason]: NOT enabled, could not clear exposure: $($failed -join ' | ')"
            Send-Alert 'NT8 strategies NOT restored (exposure not cleared)' ("A naked position could not be closed, so these were left off:`n" + ($failed -join "`n")) '5'
        }
        if ($closed.Count -and -not $failed.Count) {
            Send-Alert 'NT8 closed a naked position' ("These had a position with NO working orders protecting it. Closed it, then re-enabled the strategy:`n" + (($closed | ForEach-Object { "$($_.name)@$($_.account) [$($_.instrument)]" }) -join "`n")) '4'
        }

        $keepNames = @($clear | ForEach-Object { $_.name })
        $missing = @($missing | Where-Object { $keepNames -contains $_ })
        if ($missing.Count -eq 0) { Log "restore [$reason]: nothing left to re-enable after the exposure gate"; return $true }
    }

    Log "restore [$reason]: re-enabling $($missing.Count): $($missing -join ', ')"
    $res = Invoke-Bridge 'strategies' @{ enable = ($missing -join ','); settleMs = '5000' } $cfg.bridgeTimeoutSeconds
    if ($null -eq $res) {
        Send-AlertOnce 'restore:failed' 'NT8 strategy restore FAILED' "NinjaTrader restarted ($reason) but re-enabling did not answer. Check by hand: $($missing -join ', ')" '5'
        return $false
    }
    # Believe `state`, never the checkbox. An `enabled` row sitting in Terminated is exactly the
    # looks-healthy-but-isn't case this whole exercise exists to catch.
    $live = @(@($res.strategies) | Where-Object { $_.state -eq 'Realtime' } | ForEach-Object { $_.name })
    $still = @($missing | Where-Object { $live -notcontains $_ })
    if ($still.Count -gt 0) {
        # ! DO NOT ALERT YET. Enabling is asynchronous: a strategy loading historical data is still
        #   walking Configure -> DataLoaded -> Realtime when the settle expires. Alerting here pages
        #   someone at 3am about a restore that worked. Re-check instead.
        $state.verifyNames = ($still -join ',')
        $state.verifyUntil = [DateTime]::UtcNow.AddSeconds(180).ToString('o')
        Log "restore [$reason]: not Realtime yet, will re-check for 180s before alerting: $($still -join ', ')"
    } else {
        Send-Alert 'NT8 strategies restored' "After restart ($reason), re-enabled and Realtime: $($missing -join ', ')" '3'
    }
    Log "restore [$reason]: realtime=[$($live -join ', ')] stillOff=[$($still -join ', ')]"
    try { "$([DateTime]::UtcNow.ToString('o'))  [restore/$reason] re-enabled=[$($missing -join ', ')] stillOff=[$($still -join ', ')]" |
            Out-File -FilePath $stratLog -Append -Encoding utf8 } catch { }
    return $true
}

# Two live copies of one strategy row (see Find-DuplicateStrategyStarts): look at NinjaTrader's log
# lines written since the last look, alert once per pair. Alert only - the cure is a NinjaTrader
# restart, which is the user's call while positions may be open.
function Invoke-DuplicateWatch {
    $logFile = Get-Nt8CurrentLog $base
    if (-not $logFile) { return }
    $since = $null
    if ($state.dupScanLocal) { try { $since = [DateTime]::Parse($state.dupScanLocal, [Globalization.CultureInfo]::InvariantCulture) } catch { } }
    if ($null -eq $since) { $since = (Get-Date).AddMinutes(-10) }   # first look: recent lines only, not old history
    $lines = @()
    try { $lines = @(Get-Content -LiteralPath $logFile -Tail 4000 -ErrorAction Stop) } catch { return }
    $r = Find-DuplicateStrategyStarts -Lines $lines -SinceLocal $since
    if ($null -ne $r.NewestLocal) { $state.dupScanLocal = $r.NewestLocal.ToString('yyyy-MM-ddTHH:mm:ss.fff') }
    $names = @($r.Duplicates | ForEach-Object { $_.Strategy } | Select-Object -Unique)
    if (-not $names.Count) { return }
    Log "DUPLICATE strategy instances: two live copies of $($names -join ', ')"
    Write-GuardEvent -Component overwatch -Kind 'nt8.duplicate-instances' -Severity alert -Text "Two live copies of: $($names -join ', ')" -GuardHome $GuardHome
    Send-Alert 'NT8 is running TWO copies of a strategy' ("NinjaTrader started these strategies twice. Both copies trade, so orders and positions can be DOUBLE size:`n" + ($names -join "`n") + "`nOnly a NinjaTrader restart clears the extra copy (switching the strategy off does not). Check the positions, then restart NinjaTrader when it is safe.") '5'
}

# NinjaTrader is not running. The relauncher owns this; the overwatch steps in ONLY when the
# relauncher is not running - its heartbeat is stale, i.e. its task is missing, disabled or broken.
# Decided from the heartbeat as it is now, never from a remembered timestamp: a remembered "down
# since" once let the overwatch jump the queue and skip the relauncher's own checks (2026-08-26).
# Starting goes through the shared routine, so pause, lock screen, backoff, a refused password and
# 'one start at a time' hold here exactly as for the relauncher. Logged and alerted once per outage.
function Invoke-OverwatchDown {
    $state.consecutive = 0; $state.fault = ''
    if (Test-RelauncherFresh -GuardHome $GuardHome -MaxAgeSeconds ([int]$cfg.relauncherStaleSeconds)) {
        # Starts held back by the backoff: say so - "the relauncher starts it" read as a broken promise (2026-10-10).
        $bo = Join-Path $stateDir 'start.backoff-until'
        $until = $null
        if (Test-Path $bo) { try { $until = ConvertFrom-GuardUtc ([IO.File]::ReadAllText($bo).Trim()) } catch { } }
        if ($until -and [DateTime]::UtcNow -lt $until) {
            if ($state.startNote -ne 'backoff') {
                Log ("NinjaTrader is down - starts are backing off (too many tries) until " + $until.ToLocalTime().ToString('HH:mm') + '; the relauncher starts it after that')
                $state.startNote = 'backoff'
            }
        } elseif ($state.startNote -ne 'relauncher') { Log 'NinjaTrader is down - the relauncher is running and starts it'; $state.startNote = 'relauncher' }
        return
    }
    $hbAge = Get-AgeSeconds (Join-Path $stateDir 'relaunch.heartbeat')
    $why = $(if ($null -eq $hbAge) { 'it has never run' } else { "no heartbeat for $([math]::Round($hbAge))s" })
    $r = Invoke-Nt8Start -Component overwatch -Config $guard -GuardHome $GuardHome -Nt8Dir $base
    if ($state.startNote -ne $r.Result) {
        Log "NinjaTrader is down and the relauncher is NOT running ($why) -> start: $($r.Result) $($r.Restore)"
        $state.startNote = $r.Result
    }
    if (-not $state.downAlerted) {
        $state.downAlerted = [DateTime]::UtcNow.ToString('o')
        $did = switch ($r.Result) {
            'launched'     { 'The overwatch started NinjaTrader instead.' }
            'relaunch-off' { 'Starting NinjaTrader is set to alert only in Protection, so it stays down.' }
            default        { "The overwatch could not start it either right now ($($r.Result)); it keeps trying every minute." }
        }
        Send-Alert 'NT8 relauncher is not running' "NinjaTrader is down and the relauncher has stopped ($why). $did Check the NT8Guard-Relaunch task (Register-GuardTasks.ps1 re-installs it)." '5'
    }
}

# ---------------------------------------------------------------------------------------------
# CHECKS  - each returns a fault name or $null. Ordered most-fundamental first.
# ---------------------------------------------------------------------------------------------
if ($ImportOnly) { return }

if ($TestAlert) {
    $r = Test-GuardAlert -Config $guard -GuardHome $GuardHome
    Write-Host $r.message
    exit $(if ($r.ok) { 0 } else { 1 })
}

# ONE RUN AT A TIME. The task starts a run every minute whether or not the last one has finished (its
# hidden launcher does not wait), and a restore with reconnects, a backup save or a crash report can
# take longer than that. Two runs at once would repeat a restore - a second connect seconds after the
# first is exactly the race that duplicates strategies - or dump and kill the same process twice. An
# exclusive open of state\overwatch.lock, released when this process ends, however it ends.
if (-not $Report) {
    $runLock = $null
    try { $runLock = [IO.File]::Open((Join-Path $stateDir 'overwatch.lock'), 'OpenOrCreate', 'ReadWrite', 'None') } catch { }
    # Held by the previous run, or by a control-panel action (the fire drill holds it for minutes).
    if (-not $runLock) { Log 'skipped: the overwatch is busy elsewhere - its previous run or a panel action (fire drill, Put back, Test login) is still working'; exit 0 }
}

# Proof-of-life for the OVERWATCH ITSELF, every scheduled run - also while paused. A healthy overwatch
# writes nothing else most minutes, so without this file a running one is indistinguishable from one
# that never fired. NOT under -Report: checking must never falsify the thing being checked.
if (-not $Report) {
    try { [DateTime]::UtcNow.ToString('o') | Out-File -FilePath $heartFile -Encoding utf8 } catch { }
    Publish-GuardConfigProblem -Config $guard -Component overwatch -GuardHome $GuardHome
}

$nt = @(Get-GuardSessionProcess -Name 'NinjaTrader') | Select-Object -First 1

if ((Get-GuardPauseState -Component overwatch -GuardHome $GuardHome -Nt8Dir $base).Paused) {
    if ($Report) { Write-VerdictAndExit $nt 'Guard is paused' }
    exit 0
}
$state = Read-State

# 1. A login is in flight -> let it finish. Logged, because a SILENT early exit here is
#    indistinguishable from the task never firing.
if (Test-LoginInFlight -Config $guard -GuardHome $GuardHome) {
    if ($Report) { Write-VerdictAndExit $nt 'a NinjaTrader login is in progress' }
    Log 'stand down: a NinjaTrader login is in progress'; exit 0
}

# 2. Process down (Invoke-OverwatchDown).
if (-not $nt) {
    if ($Report) { Write-VerdictAndExit $null 'NinjaTrader.exe is not running' }
    Invoke-OverwatchDown
    Write-State $state
    exit 0
}

# 3. Work in progress? A compile or reload legitimately freezes NinjaTrader's UI for a long time, and
#    killing it mid-assembly-swap is how you corrupt Custom.dll. Any in-flight NT8 link request means
#    someone is deliberately driving this box -> stand down this cycle.
$inFlight = @(Get-ChildItem $trigDir -Filter '*.json' -ErrorAction SilentlyContinue)
if ($inFlight.Count -gt 0) {
    if ($Report) { Write-VerdictAndExit $nt "$($inFlight.Count) NT8 link request(s) in flight" }
    Log "stand down: $($inFlight.Count) NT8 link request(s) in flight ($($inFlight[0].Name))"
    exit 0
}

# 4. STARTUP GRACE. A freshly launched NinjaTrader has not compiled Custom.dll or loaded its add-ons
#    yet, so the feed guard's and the NT8 link's heartbeats are still the STALE files from before the
#    restart. Without this, the overwatch reads a healthy, still-booting NinjaTrader as GUARDIAN_DEAD
#    and kills it - and with a relauncher in play that is a restart LOOP.
$upSeconds = $null
$startUtc  = ''
try { $startUtc = $nt.StartTime.ToUniversalTime().ToString('o'); $upSeconds = ([DateTime]::UtcNow - $nt.StartTime.ToUniversalTime()).TotalSeconds } catch { }

# Has THIS NinjaTrader instance already had its strategies restored? Keyed on process start time, so
# each restart is handled exactly once - which is what stops the overwatch fighting you if you
# deliberately disable something AFTER the restore has run.
$needsRestore = ($cfg.restoreStrategies -and $startUtc -and $state.restoredForStart -ne $startUtc -and
                 ([bool]$cfg.restoreAfterAnyRestart -or [bool]$state.pendingRestore))

if ($null -ne $upSeconds -and $upSeconds -lt $cfg.startupGraceSeconds) {
    if ($state.consecutive -gt 0) { $state.consecutive = 0; $state.fault = '' }
    # Do NOT make the strategies wait out the full grace. The grace exists because the HEARTBEATS are
    # stale while NinjaTrader boots - but a fresh add-on heartbeat (feed guard or NT8 link) proves the
    # add-ons are loaded and the grid is live, which is the only precondition a restore actually has.
    $addonsUp = Test-AddonsFresh
    # -Report is read-only: without this guard a diagnostic call would silently re-enable strategies.
    if ($needsRestore -and $addonsUp -and -not $Report) {
        $why = $(if ($state.pendingRestore) { 'we restarted it' } else { 'restart we did not cause' })
        if (Invoke-StrategyRestore $why $upSeconds) { $state.restoredForStart = $startUtc; $state.pendingRestore = $false }
    }
    if ($Report) { Write-VerdictAndExit $nt ("in startup grace - NinjaTrader started {0}s ago" -f [math]::Round($upSeconds)) }
    $state.downAlerted = ''; $state.startNote = ''
    Write-State $state
    Log ("stand down: NinjaTrader started {0}s ago (startup grace {1}s)" -f [math]::Round($upSeconds), $cfg.startupGraceSeconds)
    exit 0
}

$now        = [DateTime]::UtcNow
$closed     = Test-ScheduledClosed
$responding = $true
try { $responding = $nt.Responding } catch { }
$arAge      = Get-AgeSeconds $arHeart
$brAge      = Get-AgeSeconds $brHeart
# An add-on whose heartbeat has not been written since THIS NinjaTrader started is not running in it at
# all (see Test-HeartbeatPredatesStart): that is ADDON_NOT_LOADED below, never a wedge.
$ntStartUtc = $null
try { $ntStartUtc = $nt.StartTime.ToUniversalTime() } catch { }
$arNotLoaded = Test-HeartbeatPredatesStart $arHeart $cfg.guardianStaleSeconds $ntStartUtc
$brNotLoaded = Test-HeartbeatPredatesStart $brHeart $cfg.bridgeStaleSeconds $ntStartUtc

# The feed guard's own view of the feed, read from its heartbeat rather than asked for - asking costs
# a round trip and this is already being written every tick. Not from a feed guard that is not running:
# its last heartbeat's feed age would read as a dead feed and restart NinjaTrader forever.
$feedAge = $null; $arRung = $null; $arBuild = ''; $arClosed = $false
if ((Test-Path $arHeart) -and -not $arNotLoaded) {
    try { $h = Get-Content $arHeart -Raw | ConvertFrom-Json
          $feedAge = $h.lastL1AgeSeconds; $arRung = $h.rung; $arBuild = $h.build; $arClosed = ($h.scheduledClosed -eq $true) } catch { }
}
# The feed guard's calendar also knows CME holidays and early closes, which Test-ScheduledClosed does not
# (it knows the weekend and the daily break only). A holiday's quiet feed is not a dead one; restarting
# NinjaTrader over it would only take its strategies down for nothing.
if ($arClosed) { $closed = $true }

$uiHung        = (-not $responding)
$guardianStale = ($null -ne $arAge -and $arAge -gt $cfg.guardianStaleSeconds -and -not $arNotLoaded)
$bridgeStale   = ($null -ne $brAge -and $brAge -gt $cfg.bridgeStaleSeconds -and -not $brNotLoaded)
$feedDead      = ((-not $closed) -and $null -ne $feedAge -and $feedAge -gt $cfg.feedStaleSeconds)
# Is the feed guard actively working the problem right now? rung > 0 = climbing (re-subscribe ->
# disconnect -> reconnect). Killing mid-climb also risks capturing a pre-kill strategy record while
# the ladder has strategies disabled, which would restore the wrong set after the restart.
$ladderActive  = ($null -ne $arRung -and $arRung -gt 0)
# rung 0 does NOT mean "has not tried" - see feedSourceOutageMaxSeconds. Ask what actually happened
# before treating a quiet rung as an untried ladder.
$srcVerdictUtc = $(if ($feedDead) { Get-SourceOutageVerdictUtc } else { $null })
$sourceBlamed  = ($null -ne $srcVerdictUtc)
$feedCeiling   = $(if ($sourceBlamed) { $cfg.feedSourceOutageMaxSeconds } else { $cfg.feedDeferMaxSeconds })
$feedDeferred  = ($feedDead -and ($ladderActive -or $sourceBlamed) -and $feedAge -lt $feedCeiling)
if ($feedDeferred) {
    if ($sourceBlamed) {
        # NOTE the trailing `+`: inside parentheses a line ending in a closing quote looks like a
        # COMPLETE expression, so a leading `+` on the next line is a parse error.
        Log ("FEED_DEAD held off: L1 dead ${feedAge}s but the feed guard ran its full ladder at " +
             $srcVerdictUtc.ToString('o') + " and blamed the FEED SOURCE. Restarting NinjaTrader cannot" +
             " fix an outage upstream of it (ceiling $($cfg.feedSourceOutageMaxSeconds)s, then we kill anyway).")
    } else {
        Log "FEED_DEAD held off: L1 dead ${feedAge}s but the feed guard is mid-ladder at rung $arRung (ceiling $($cfg.feedDeferMaxSeconds)s)"
    }
}

if ($null -eq $arAge -and $arBuild -eq '') {
    if (-not $state.noFeedGuardNoted) { Log 'note: no feed guard heartbeat file - install the feed guard add-on, or the feed checks are blind'; $state.noFeedGuardNoted = 'yes' }
} elseif ($state.noFeedGuardNoted) { $state.noFeedGuardNoted = '' }

# Faults listed in $alertOnly are reported but never acted on. Everything else can kill the process.
$alertOnly = @('UI_HUNG', 'ADDON_NOT_LOADED')

$fault = $null; $detail = ''
if ($uiHung -and ($guardianStale -or $bridgeStale)) {
    # THE 2026-08-25 SIGNATURE, and the only combination that earns a kill on UI evidence: the UI is
    # not pumping AND something that runs on a background thread has also stopped.
    $fault = 'NT8_WEDGED'
    $detail = "main window not pumping AND " + $(if ($guardianStale) { "feed guard heartbeat ${arAge}s stale (rung $arRung)" } else { "NT8 link heartbeat ${brAge}s stale" })
}
elseif ($guardianStale) {
    $fault = 'GUARDIAN_DEAD'; $detail = "feed guard heartbeat is ${arAge}s old (tick is 10s); guardian wedged at rung $arRung"
}
elseif ($bridgeStale) {
    $fault = 'BRIDGE_DEAD'; $detail = "NT8 link heartbeat is ${brAge}s old"
}
elseif ($arNotLoaded -or $brNotLoaded) {
    # ALERT-ONLY: restarting cannot load an add-on that fails to compile or is not installed; it would
    # only knock every strategy off again each cooldown.
    $which = @()
    if ($arNotLoaded) { $which += 'the feed guard add-on (AutoReconnect)' }
    if ($brNotLoaded) { $which += 'the NT8 link add-on (NT8BridgeServer)' }
    $fault = 'ADDON_NOT_LOADED'
    $detail = "$($which -join ' and ') has not run since NinjaTrader started - it is not loaded, was removed, or failed to compile (often after a NinjaTrader update). A restart will not fix that, so NinjaTrader is left running. Open the NinjaScript Editor, press F5 to compile and fix any errors, or reinstall the add-on."
}
elseif ($uiHung) {
    # ! ALERT-ONLY, ON PURPOSE. A manual F5 in the NinjaScript editor makes NinjaTrader unresponsive
    #   for minutes and creates no NT8 link request, so the in-flight stand-down does not cover it.
    #   Killing the process mid-assembly-swap is how you corrupt Custom.dll and lose the whole box.
    $fault = 'UI_HUNG'; $detail = 'main window is not pumping, but background threads are alive (likely a compile) - alerting only, will not restart'
}
elseif ($feedDead -and -not $feedDeferred) {
    # The guardian is alive and still has not fixed it -> its own ladder has failed or given up.
    $fault = 'FEED_DEAD'
    $detail = $(if ($sourceBlamed) {
        "no market data for ${feedAge}s with the market open. The feed guard ran its FULL ladder and blamed the feed source, and the feed is STILL dead past the $($cfg.feedSourceOutageMaxSeconds)s hold-off, so we restart anyway in case the fault is local after all"
    } else {
        "no market data for ${feedAge}s with the market open; the feed guard at rung $arRung has not recovered it"
    })
}

$verdict = [ordered]@{
    base = $base
    utc = $now.ToString('o'); ntPid = $nt.Id; responding = $responding
    autoReconnectHeartbeatAgeSec = $arAge; autoReconnectRung = $arRung; autoReconnectBuild = $arBuild
    bridgeHeartbeatAgeSec = $brAge; lastL1AgeSec = $feedAge
    marketScheduledClosed = $closed; fault = $fault; detail = $detail
    consecutive = $state.consecutive; pendingRestore = $state.pendingRestore
}
if ($Report) { $verdict | ConvertTo-Json -Depth 4; exit 0 }

$state.downAlerted = ''; $state.startNote = ''

# ---------------------------------------------------------------------------------------------
# PENDING RESTORE - NinjaTrader came back after a restart; put the strategies back.
# ---------------------------------------------------------------------------------------------
if ($needsRestore -and $responding -and (Test-AddonsFresh)) {
    $why = $(if ($state.pendingRestore) { 'we restarted it' } else { 'restart we did not cause' })
    if (Invoke-StrategyRestore $why $upSeconds) {
        $state.restoredForStart = $startUtc; $state.pendingRestore = $false; $state.killedAtUtc = ''
    }
    Write-State $state
    exit 0
}

# ---------------------------------------------------------------------------------------------
# STRATEGY DRIFT AFTER A FEED EPISODE - put them back WITHOUT restarting NinjaTrader.
#
# ! THIS IS WHAT MAKES THE HOLD-OFF ABOVE SAFE. Simply declining to kill would otherwise leave the
#   strategies off for the whole hold-off with nothing to bring them back.
#
#   NinjaTrader disables strategies by itself on ConnectionLost (2026-09-08: the feed flapped ~8x in
#   ten seconds and NinjaTrader turned all eight off - long before any watchdog acted). So "strategies
#   are off" is a NORMAL after-effect of a feed episode, not evidence of a crash, and it does not need
#   a restart to undo. Once the feed is back, just put them back.
#
#   Gated on an EPISODE, not on a timer: it fires once when a feed outage ends, so the Control Center
#   grid is not polled every cycle. That poll is the call that wedged the Control Center on 2026-08-25.
# ---------------------------------------------------------------------------------------------
if ($cfg.restoreStrategies -and -not $Report) {
    if ($feedDead -or $ladderActive -or $sourceBlamed) {
        if (-not $state.feedEpisodeUtc) {
            $state.feedEpisodeUtc = $now.ToString('o')
            Log "feed episode OPEN (L1 ${feedAge}s, rung $arRung): any strategies NinjaTrader disables now are expected back when the feed returns"
        }
    }
    elseif ($state.feedEpisodeUtc -and $responding) {
        $openedFor = ''
        try { $openedFor = "$([math]::Round(([DateTime]::UtcNow - [DateTime]::Parse($state.feedEpisodeUtc).ToUniversalTime()).TotalMinutes))min" } catch { }
        Log "feed episode CLOSED after ${openedFor} (L1 ${feedAge}s): checking whether the strategies came back on their own"
        # Invoke-StrategyRestore only enables what the record says was on and the grid says is off, so
        # this is a no-op when NinjaTrader restored them itself. It returns $false when the grid is not
        # readable yet - leave the episode OPEN in that case so a later cycle retries.
        if (Invoke-StrategyRestore 'feed recovered without a restart' $upSeconds) { $state.feedEpisodeUtc = '' }
    }
}

# ---------------------------------------------------------------------------------------------
# HEALTHY - record the enabled set, occasionally.
#
# Deliberately NOT every cycle: reading the Control Center grid is the exact call that wedged the
# guardian, so we touch it as rarely as we can while still holding a list worth restoring. The
# interval is also the blast radius of a wrong restore - anything you disable is in the record
# within strategyCacheMinutes, after which it will never be switched back on.
#
# ! Guarded on $needsRestore being FALSE. Refreshing before a pending restore has run would capture
#   the post-restart all-disabled grid and destroy the list we are about to restore from.
# ---------------------------------------------------------------------------------------------
if (-not $fault) {
    if ($state.consecutive -gt 0) { Log "cleared: '$($state.fault)' resolved on its own" }
    $state.consecutive = 0; $state.fault = ''
    Clear-AlertOnce 'fault:'                               # the episode is over: the next fault pages at once
    if (-not $needsRestore) { Clear-AlertOnce 'restore:' }

    # Record what is CONNECTED, every healthy cycle - cheap, no UI dispatcher - so a connection you
    # park on purpose stops being restored within a minute rather than an hour.
    # ! Guarded on $needsRestore for the same reason as the strategy record.
    if ($cfg.restoreConnections -and -not $needsRestore -and -not $Report) {
        $cr = Invoke-Bridge 'connections' @{} $cfg.bridgeTimeoutSeconds
        if ($null -ne $cr -and $cr.status -eq 'ok') { [void](Update-ConnectionCache $cr.connections 'periodic') }
        else { Log 'connections: not readable this cycle - record left alone' }
        # A clean cycle means any earlier connect attempt is done with; let the next outage retry.
        if ($state.connAttemptUtc) { $state.connAttemptUtc = '' }
    }

    # -----------------------------------------------------------------------------------------
    # SOMETHING DISABLED THE STRATEGIES AND IT WASN'T YOU. Put them back.
    #
    # The general case the feed-episode trigger missed: NinjaTrader disables strategies on a
    # ConnectionLost by itself, and the feed guard can fix the feed in seconds - far quicker than
    # feedStaleSeconds - so a box can end up fully healthy with every strategy unchecked and no fault
    # ever raised. That is exactly 2026-09-09.
    #
    # ! WAIT FOR THE DUST TO SETTLE. Only act with the feed guard idle (rung 0) and market data
    #   flowing. Re-enabling mid-ladder is pointless - its disconnect rung would just disable them
    #   again - and during a flap the markers keep arriving, so acting on each would thrash the grid.
    # -----------------------------------------------------------------------------------------
    if ($cfg.restoreStrategies -and -not $needsRestore -and -not $Report -and
        ($null -ne $arRung -and $arRung -eq 0) -and
        ($null -ne $feedAge -and $feedAge -lt $cfg.feedStaleSeconds)) {

        $mark = Get-InvoluntaryDisableUtc
        if ($null -ne $mark) {
            $handled = $null
            if ($state.disableHandledUtc) { try { $handled = [DateTime]::Parse($state.disableHandledUtc).ToUniversalTime() } catch { } }
            if ($null -eq $handled -or $mark -gt $handled) {
                Log ("strategies were disabled by something at " + $mark.ToString('o') +
                     " (connection drop / recovery disconnect) and the feed is back - checking the record")
                if (Invoke-StrategyRestore 'strategies disabled by a connection drop' $upSeconds) {
                    $state.disableHandledUtc = $mark.ToString('o')
                }
            }
        }
    }

    # Deferred restore verification: a strategy clicked ON is asynchronous, so "not Realtime" at the
    # moment of the click means "not yet", not "failed". Re-read until it settles, and only alert if
    # it genuinely never gets there.
    if ($state.verifyNames) {
        $pending = @($state.verifyNames -split ',' | Where-Object { $_ })
        $vr = Invoke-Bridge 'strategies' @{} $cfg.bridgeTimeoutSeconds
        if ($null -ne $vr -and $vr.status -eq 'ok') {
            $liveNow = @(@($vr.strategies) | Where-Object { $_.state -eq 'Realtime' } | ForEach-Object { $_.name })
            $stillOff = @($pending | Where-Object { $liveNow -notcontains $_ })
            if ($stillOff.Count -eq 0) {
                Send-Alert 'NT8 strategies restored' "All restored strategies reached Realtime: $($pending -join ', ')" '3'
                Log "restore verify: all Realtime: $($pending -join ', ')"
                $state.verifyNames = ''; $state.verifyUntil = ''
            } else {
                $expired = $false
                try { $expired = ([DateTime]::UtcNow -gt [DateTime]::Parse($state.verifyUntil).ToUniversalTime()) } catch { $expired = $true }
                if ($expired) {
                    Send-Alert 'NT8 strategy restore INCOMPLETE' "After a restart these never reached Realtime: $($stillOff -join ', ')" '5'
                    Log "restore verify: TIMED OUT still not Realtime: $($stillOff -join ', ')"
                    $state.verifyNames = ''; $state.verifyUntil = ''
                } else {
                    Log "restore verify: still transitioning: $($stillOff -join ', ')"
                }
            }
        }
    }
    if ($startUtc -and $state.restoredForStart -ne $startUtc -and -not $cfg.restoreAfterAnyRestart) {
        # restore-on-any-restart is off, so treat this instance as already handled
        $state.restoredForStart = $startUtc
    }
    $cacheAge = Get-AgeSeconds $cacheFile
    $pollDue  = ($null -eq $cacheAge -or $cacheAge -gt ($cfg.strategyCacheMinutes * 60))
    if (-not $needsRestore -and $pollDue) {
        # ! LOG EVERY GRID POLL, even when nothing changed. This poll cycles Control Center tabs - the
        #   SAME operation that wedged the Control Center on 2026-08-25. If NinjaTrader locks up, the
        #   first question is "did it start within seconds of a poll?", and a silent poll makes that
        #   unanswerable.
        Log 'grid poll: reading Control Center strategies (touches the Control Center UI thread)'
        $r = Invoke-Bridge 'strategies' @{} $cfg.bridgeTimeoutSeconds
        if ($null -ne $r -and $r.status -eq 'ok') {
            Update-StrategyCache $r.strategies 'periodic' | Out-Null
            Log 'grid poll: ok'
        } else { Log 'grid poll: NO ANSWER (Control Center may be busy or wedged)' }
    }

    # STRATEGY BACKUPS. The record above knows WHICH strategies to switch back on; it cannot recreate
    # one. NinjaTrader drops a switched-off strategy's database row on shutdown, and once took five
    # strategies with it. This saves every strategy's full settings (and its chart links) from the
    # database - not through the Control Center - so a dropped one can be put back. Healthy cycles
    # only, under the start lock, bounded.
    $bkDue = $true
    if ($state.lastBackupUtc) { try { $bkDue = (([DateTime]::UtcNow - [DateTime]::Parse($state.lastBackupUtc).ToUniversalTime()).TotalMinutes -ge [double]$cfg.backupEveryMinutes) } catch { } }
    if ($bkDue -and -not $needsRestore) {
        $b = Invoke-BackupSave -Config $guard -GuardHome $GuardHome -Nt8Dir $base -Component overwatch
        if ($b.Result -ne 'busy') { $state.lastBackupUtc = [DateTime]::UtcNow.ToString('o') }
    }

    # Two live copies of one strategy: cheap (the tail of one log file), every healthy cycle.
    try { Invoke-DuplicateWatch } catch { Log "duplicate watch: $($_.Exception.Message)" }

    # Hourly proof-of-life IN THE LOG. The heartbeat file answers "is it alive right now"; it cannot
    # answer "was it alive at 09:50 this morning".
    $needAlive = $true
    if ($state.lastAliveUtc) {
        try { $needAlive = (([DateTime]::UtcNow - [DateTime]::Parse($state.lastAliveUtc).ToUniversalTime()).TotalMinutes -ge 60) } catch { }
    }
    if ($needAlive) {
        $upMin = if ($null -ne $upSeconds) { [math]::Round($upSeconds/60) } else { '?' }
        Log ("overwatch: alive; NinjaTrader up {0}min, feed guard {1}s, NT8 link {2}s, market data {3}s" -f $upMin, $arAge, $brAge, $feedAge)
        $state.lastAliveUtc = [DateTime]::UtcNow.ToString('o')
    }
    if ($state.killedAtUtc) { $state.killedAtUtc = '' }
    Write-State $state
    exit 0
}

# ---------------------------------------------------------------------------------------------
# FAULT - count it, and only act once it has persisted.
# ---------------------------------------------------------------------------------------------
if ($state.fault -eq $fault) { $state.consecutive = [int]$state.consecutive + 1 } else { $state.consecutive = 1; $state.fault = $fault }
Log "FAULT $fault ($($state.consecutive)/$($cfg.consecutiveToAct)) - $detail"

if ($state.consecutive -lt $cfg.consecutiveToAct) { Write-State $state; exit 0 }
if ($state.consecutive -eq $cfg.consecutiveToAct) {
    Write-GuardEvent -Component overwatch -Kind 'nt8.fault' -Severity warn -Text "$fault - $detail" -GuardHome $GuardHome
}

# Alert-only faults stop here: they get one push per episode, and never a remedy.
if ($alertOnly -contains $fault) {
    if ($state.consecutive -eq $cfg.consecutiveToAct) {
        Send-Alert "NT8 $fault" "$detail`n`nNo action taken. If this persists well past any compile, look at the box." '4'
    }
    Write-State $state; exit 0
}

# Cooldown so a box that will not stay healthy is not restarted in a loop.
if ($state.lastRemedyUtc) {
    try {
        $since = ([DateTime]::UtcNow - [DateTime]::Parse($state.lastRemedyUtc).ToUniversalTime()).TotalMinutes
        if ($since -lt $cfg.cooldownMinutes) {
            Log "remedy suppressed: $([math]::Round($since,1)) min since the last one (cooldown $($cfg.cooldownMinutes) min)"
            Send-AlertOnce "fault:${fault}:still" "NT8 $fault (still)" "$detail`n`nStill broken $([math]::Round($since,1)) min after the last restart. Not restarting again - this needs a human." '5'
            Write-State $state; exit 0
        }
    } catch { }
}

# ---------------------------------------------------------------------------------------------
# REMEDY - kill NinjaTrader and hand off to the relauncher.
#
# NOTE, deliberately: there is NO open-position gate here. A blind strategy on a dead feed is more
# dangerous than a restart, because on a funded account the protective orders are server-side and
# survive. Logged, not gated - so the record always shows what was open at the moment it acted.
# ---------------------------------------------------------------------------------------------
$exposure = 'unknown'
$acct = Invoke-Bridge 'account' @{} 10
if ($null -ne $acct) {
    $pos = 0; $ord = 0
    foreach ($a in $acct.accounts) { $pos += @($a.positions).Count; $ord += @($a.workingOrders).Count }
    $exposure = "$pos position(s), $ord working order(s)"
}
Log "REMEDY: $fault confirmed x$($state.consecutive). Exposure at this moment: $exposure"

# A restart is a kill plus a start: with 'Start NinjaTrader when it is down' off nothing would start it
# again, and a frozen NinjaTrader would become a dead one. Hold the kill and say so.
if ($WhatIfRemedy -or $cfg.dryRunRemedy -or -not $cfg.restartWhenFrozen -or $cfg.relaunch -eq $false) {
    $src = $(if ($WhatIfRemedy) { '-WhatIfRemedy' } elseif ($cfg.dryRunRemedy) { 'dryRunRemedy in guard.json' }
             elseif (-not $cfg.restartWhenFrozen) { 'restarting a frozen NinjaTrader is set to alert only in Protection' }
             else { "'Start NinjaTrader when it is down' is off in Protection, so a restart would leave it down" })
    Log "NOT RESTARTED ($src): would kill NinjaTrader.exe here and let the relauncher bring it back"
    Send-AlertOnce "fault:${fault}:notrestarted" "NT8 $fault - NOT restarted" "$detail`nExposure: $exposure`nNinjaTrader was left alone ($src). Act by hand if this is real." '5'
    Write-State $state; exit 0
}

# ! NEVER KILL INTO A LOCKED SCREEN. The remedy is "kill, and something starts it again and logs in"
#   - and the login cannot reach NinjaTrader's window while Windows' lock screen owns the desktop.
#   While locked every start is held, so killing converts a stale feed into NO NinjaTrader AT ALL
#   until a human logs in. Not an edge case: a desktop that sleeps overnight wakes to a lock screen
#   with the market open. Leaving it running is strictly better: the in-process feed guard needs no
#   desktop and can still recover the feed by itself.
#   Only THIS Windows session's lock screen counts. FAIL OPEN: if the probe throws we proceed.
$consoleLocked = $false
try { $consoleLocked = [bool](@(Get-GuardSessionProcess -Name 'LogonUI').Count) } catch { $consoleLocked = $false }
if ($consoleLocked) {
    Log "REMEDY HELD: the screen is LOCKED (LogonUI up). The login cannot reach NinjaTrader's window, so a kill would leave NinjaTrader DOWN until someone logs in. Leaving it up for the feed guard to recover in-process."
    if (-not $state.lockHoldAlertUtc -or ([DateTime]::UtcNow - [DateTime]::Parse($state.lockHoldAlertUtc).ToUniversalTime()).TotalHours -ge 6) {
        Send-Alert "NT8 $fault - NOT restarted (screen locked)" "$detail`nExposure: $exposure`nThe screen is locked, so a restart would leave NinjaTrader down until you log in - it was left running instead. The feed guard is still trying in-process. Log in when you can." '5'
        $state.lockHoldAlertUtc = [DateTime]::UtcNow.ToString('o')
    }
    Write-State $state; exit 0
}
$state.lockHoldAlertUtc = ''

$dumpNote = $(if ($cfg.dumpBeforeKill) { "`nSaving a crash report first (up to $($cfg.dumpTimeoutSeconds)s)." } else { '' })
Send-Alert "NT8 $fault - restarting" "$detail`nExposure: $exposure$dumpNote`nKilling NinjaTrader; the relauncher will bring it back." '5'

# Cache the enabled list one last time ONLY if the UI is still answering; if it is hung this call
# would block, which is the one thing this script may never do.
if ($responding) {
    $r = Invoke-Bridge 'strategies' @{} 15
    if ($null -ne $r -and $r.status -eq 'ok') {
        $enabled = Update-StrategyCache $r.strategies 'pre-kill'
        Log "record (pre-kill): $((@($enabled | ForEach-Object { StratKey $_ })) -join ', ')"
    } else {
        # Do NOT clear the record just because we could not read the grid. The existing one is the
        # only thing that will bring the strategies back after the restart.
        Log 'record (pre-kill): grid unreadable - keeping the previous record'
    }
}

# CAPTURE BEFORE DESTROYING. This is the only point at which the wedged process still exists, and a
# hang leaves no Windows dump of its own. Bounded, and a failure here never blocks the kill.
$dumpPath = $null
try { $dumpPath = Save-NT8Dump $nt.Id $fault } catch { Log "dump: unexpected error - $($_.Exception.Message)" }

try {
    Stop-Process -Id $nt.Id -Force -ErrorAction Stop
    Log "killed NinjaTrader.exe pid=$($nt.Id)"
    Write-GuardEvent -Component overwatch -Kind 'nt8.kill' -Severity alert -Text "Restarted a frozen NinjaTrader ($fault): $detail" -GuardHome $GuardHome
} catch {
    Log "kill FAILED: $($_.Exception.Message)"
    Send-Alert 'NT8 kill FAILED' "Could not stop NinjaTrader (pid $($nt.Id)): $($_.Exception.Message). Needs a human." '5'
    Write-State $state; exit 1
}

# The relauncher brings it back within a minute; if the relauncher is not running, the next overwatch
# cycle starts it (step 2), through the same routine.
$state.lastRemedyUtc = [DateTime]::UtcNow.ToString('o')
$state.killedAtUtc   = $state.lastRemedyUtc
$state.pendingRestore = [bool]$cfg.restoreStrategies
# We killed it, so the RESTART path now owns putting the strategies back. Close the feed episode so
# the drift check does not also fire and race it with a second restore.
$state.feedEpisodeUtc = ''
$state.consecutive = 0; $state.fault = ''
Write-State $state
exit 0
