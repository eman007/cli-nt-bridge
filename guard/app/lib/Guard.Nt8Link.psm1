# Asking the NT8 link add-on inside NinjaTrader, and the one question every risky button asks first:
# are you flat? (spec 5: no open position, no working order, no running strategy - and when the NT8 link
# cannot answer, refuse: "can't confirm you're flat").
#
# The NT8 link answers over a FILE protocol, no socket and no Python: a request <kind>_<id>.json dropped in
# NinjaTrader's NT8Bridge\trigger folder, its answer written to NT8Bridge\result under the same name. Every
# call is bounded, and a request nobody answered is taken back so it cannot fire late against a later
# NinjaTrader. (The overwatch keeps its own copy of this in Guard-Overwatch.ps1, where its tests cut it out.)
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Config.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Start.psm1')    # Get-GuardSessionProcess
Import-Module (Join-Path $PSScriptRoot 'Guard.Status.psm1')   # Get-GuardBackupList: an empty Strategies list contradicted

# A strategy's own NinjaScript state while it runs. The grid's tick (`enabled`) only says the click landed;
# the add-on's header: believe `state`.
$script:RunningStates = @('Active', 'DataLoaded', 'Historical', 'Transition', 'Realtime')

function Invoke-Nt8Link {
    # The answer as an object, or $null: no NT8 link folder (never created here), or no answer in time.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Kind, [hashtable]$Extra = @{}, [int]$TimeoutSeconds = 30, [string]$Nt8Dir)
    $base = [IO.Path]::Combine((Get-Nt8DataDir $Nt8Dir), 'NT8Bridge')
    $trig = [IO.Path]::Combine($base, 'trigger')
    $res = [IO.Path]::Combine($base, 'result')
    if (-not (Test-Path -LiteralPath $trig)) { return $null }
    $id = [guid]::NewGuid().ToString('N')
    $req = [ordered]@{ id = $id; kind = $Kind }
    foreach ($k in $Extra.Keys) { $req[$k] = $Extra[$k] }
    $reqPath = [IO.Path]::Combine($trig, "$($Kind)_$id.json")
    $resPath = [IO.Path]::Combine($res, "$($Kind)_$id.json")
    # Written under another name, then renamed: the add-on reads every *.json it finds, never a half-written one.
    $tmp = [IO.Path]::Combine($trig, "$($Kind)_$id.tmp")
    try {
        [IO.File]::WriteAllText($tmp, ($req | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false)))
        [IO.File]::Move($tmp, $reqPath)
    } catch { try { [IO.File]::Delete($tmp) } catch { }; return $null }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $resPath) {
            # an answer caught mid-write does not parse: read it again on the next pass
            try { $o = [IO.File]::ReadAllText($resPath) | ConvertFrom-Json; [IO.File]::Delete($resPath); return $o } catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    try { [IO.File]::Delete($reqPath) } catch { }
    return $null
}

function Get-Nt8FlatState {
    # May Guard close NinjaTrader, or cut its feed? Returns @{ State; Safe; Reasons; Text }:
    #   closed   NinjaTrader is not running in this Windows session
    #   login    it sits at its login window - nothing is loaded, so nothing can be open or running
    #   flat     the NT8 link answered: no position on any account, no working order, no strategy running
    #   busy     it answered with something open; Reasons names each, in words
    #   unknown  the NT8 link could not answer (not installed, not loaded, stuck, an error), or answered
    #            something that cannot be true (no account at all; an empty Strategies list while Guard's
    #            backups saw strategies on it) - refuse
    # Safe = closed | login | flat. Every NinjaTrader in this Windows session counts: 'login' only when all of
    # them sit at the login window. -Processes and -Link are test seams; the defaults are the real thing.
    [CmdletBinding()]
    param([hashtable]$Config, [string]$Nt8Dir, [string]$GuardHome, [scriptblock]$Processes, [scriptblock]$Link, [Nullable[DateTime]]$NowUtc)
    $now = $(if ($null -ne $NowUtc) { [DateTime]$NowUtc } else { [DateTime]::UtcNow })
    if (-not $Config) { $Config = Get-GuardConfig }
    $nt8 = Get-Nt8DataDir $(if ($Nt8Dir) { $Nt8Dir } else { [string]$Config.nt8.dataDir })
    if (-not $Processes) {
        $Processes = {
            , @(Get-GuardSessionProcess -Name NinjaTrader | ForEach-Object {
                $start = $null; try { $start = $_.StartTime.ToUniversalTime() } catch { }
                [pscustomobject]@{ Id = $_.Id; StartUtc = $start; Title = $_.MainWindowTitle } })
        }
    }
    if (-not $Link) {
        $t = [int]$Config.overwatch.bridgeTimeoutSeconds
        $Link = { param($kind) Invoke-Nt8Link -Kind $kind -Nt8Dir $nt8 -TimeoutSeconds $t }
    }
    $out = { param($state, $text, $reasons = @())
             [pscustomobject]@{ State = $state; Safe = (@('closed', 'login', 'flat') -contains $state); Reasons = @($reasons); Text = $text } }
    $unknown = { param($why) & $out 'unknown' "Guard can't confirm you're flat: $why" }

    $procs = @(@(& $Processes) | ForEach-Object { $_ })      # the seam hands back ONE array: unroll it
    if (-not $procs.Count) { return (& $out 'closed' 'NinjaTrader is not running.') }
    if (-not @($procs | Where-Object { $_.Title -ne 'Welcome' }).Count) { return (& $out 'login' 'NinjaTrader is at its login window, so nothing is loaded yet.') }
    $newest = @($procs | Where-Object { $_.StartUtc } | Sort-Object StartUtc -Descending) | Select-Object -First 1

    $beat = [IO.Path]::Combine($nt8, 'NT8Bridge\result\heartbeat.json')
    if (-not (Test-Path -LiteralPath $beat)) { return (& $unknown 'the NT8 link add-on is not installed in NinjaTrader (Setup, step 3).') }
    $written = (Get-Item -LiteralPath $beat).LastWriteTimeUtc
    $age = ($now - $written).TotalSeconds
    if ($age -gt [double]$Config.thresholds.bridgeStaleSeconds) {
        if ($newest -and $written -lt $newest.StartUtc) { return (& $unknown 'the NT8 link add-on is not loaded in this NinjaTrader (compile it with F5 in the NinjaScript Editor).') }
        return (& $unknown "the NT8 link add-on has not answered for $([math]::Floor($age / 60)) min.")
    }

    $why = { param($r, $what)
        if ($null -eq $r) { return "the NT8 link did not answer when asked for $what." }
        $m = @($r.errors | ForEach-Object { [string]$_.message } | Where-Object { $_ }) -join '; '
        return "the NT8 link could not read $what$(if ($m) { " ($m)" })."
    }
    $a = & $Link 'account'
    if ($null -eq $a -or $a.status -ne 'ok') { return (& $unknown (& $why $a 'the accounts')) }
    # The add-on answers ok with no account when it could not copy NinjaTrader's list; a logged-in
    # NinjaTrader always has one (Sim101).
    if (-not @($a.accounts | Where-Object { $_ }).Count) { return (& $unknown 'the NT8 link answered with no account at all - NinjaTrader may still be loading. Try again in a minute.') }
    $s = & $Link 'strategies'
    if ($null -eq $s -or $s.status -ne 'ok' -or $s.gridResolved -eq $false) { return (& $unknown (& $why $s 'the Strategies list')) }
    # An ok answer with zero rows has hidden eight running strategies before (2026-09-08, the overwatch's
    # record). Trust it only when Guard's own backups have nothing on NinjaTrader's list either.
    if (-not @($s.strategies | Where-Object { $_ }).Count) {
        $live = @((Get-GuardBackupList -GuardHome $GuardHome).Rows | Where-Object State -eq 'live').Count
        if ($live) { return (& $unknown "the Strategies list came back empty, but Guard's last backup saw $live on it. Try again in a minute - or, if you removed them, press Save now in Strategy backups first.") }
    }

    $reasons = New-Object System.Collections.Generic.List[string]
    foreach ($acc in @($a.accounts)) {
        foreach ($p in @($acc.positions | Where-Object { $_ -and [string]$_.marketPosition -ne 'Flat' })) {
            $reasons.Add("$($acc.name): $($p.marketPosition) $($p.quantity) $($p.instrument)")
        }
        $ords = @($acc.workingOrders | Where-Object { $_ })
        if ($ords.Count) {
            $inst = (@($ords | ForEach-Object { [string]$_.instrument } | Select-Object -Unique) -join ', ')
            $reasons.Add("$($acc.name): $($ords.Count) working order$(if ($ords.Count -ne 1) { 's' }) ($inst)")
        }
    }
    foreach ($r in @($s.strategies | Where-Object { $_ -and ($_.enabled -eq $true -or $script:RunningStates -contains [string]$_.state) })) {
        $reasons.Add("strategy $($r.name) is running ($($r.account), $($r.instrument))")
    }
    if ($reasons.Count) {
        return (& $out 'busy' ("You are not flat: " + ($reasons -join '; ') + '. Close them, cancel the orders and switch the strategies off first.') $reasons.ToArray())
    }
    return (& $out 'flat' 'Flat: no position, no working order, no strategy running.')
}

Export-ModuleMember -Function Invoke-Nt8Link, Get-Nt8FlatState
