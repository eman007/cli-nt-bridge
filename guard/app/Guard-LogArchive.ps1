# Guard-LogArchive.ps1 - keep NinjaTrader's logs past a restart. Scheduled task, every 10 minutes.
#
# A restart recycles NinjaTrader's log, so the record of WHY it just died is destroyed by the act of
# recovering - unless it was copied first. Copies log\*.txt, trace\*.txt and the NT8 link's output
# into <GuardHome>\logs\nt8, keeps them logkeeper.retentionDays, and removes LIVE log/trace files older
# than logKeeper.livePruneDays - but only a file whose own copy is verified complete.
#
# Two details that matter:
#   1. The files are OPEN: NinjaTrader holds them, so they are read with FileShare ReadWrite+Delete.
#   2. The NAME is reused: each session writes log.<date>.00000.txt again, so each archive is keyed on
#      the file's creation time plus a hash of its first bytes; within a session it is refreshed in
#      place, a new session gets a new archive. Do not "simplify" this to copy-if-newer.
[CmdletBinding()]
param([string]$GuardHome, [int]$RetentionDays, [int]$LivePruneDays, [switch]$Report)

$ErrorActionPreference = 'SilentlyContinue'
foreach ($m in 'Guard.Paths', 'Guard.Log', 'Guard.Config', 'Guard.Pause') { Import-Module (Join-Path $PSScriptRoot "lib\$m.psm1") }

$cfg = Get-GuardConfig -GuardHome $GuardHome
$base = Get-Nt8DataDir $cfg.nt8.dataDir
if (-not $RetentionDays) { $RetentionDays = [int]$cfg.logKeeper.retentionDays }
if (-not $LivePruneDays) { $LivePruneDays = [int]$cfg.logKeeper.livePruneDays }
$archiveDir = Get-GuardPath nt8logs $GuardHome
$sources = @(
    @{ Path = (Join-Path $base 'log');              Filter = '*.txt' },
    @{ Path = (Join-Path $base 'trace');            Filter = '*.txt' },
    @{ Path = (Join-Path $base 'NT8Bridge\output'); Filter = '*.jsonl' }
)
function Log([string]$m) { Write-GuardLog -Component logarchive -Message $m -GuardHome $GuardHome }

function Copy-OpenFile([string]$src, [string]$dst) {
    $in = $null; $out = $null
    try {
        $in  = New-Object System.IO.FileStream($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
                   ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
        $out = New-Object System.IO.FileStream($dst, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $in.CopyTo($out)
        return $true
    } catch { Log "copy failed '$([IO.Path]::GetFileName($src))': $($_.Exception.Message)"; return $false }
    finally { if ($out) { $out.Dispose() }; if ($in) { $in.Dispose() } }
}

function Get-SessionKey([string]$path) {
    $stamp = 'unknown'
    try { $stamp = (Get-Item $path).CreationTimeUtc.ToString('yyyyMMdd-HHmmss') } catch { }
    $hash = '00000000'
    try {
        $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
                  ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
        try {
            $buf = New-Object byte[] 512
            $n = $fs.Read($buf, 0, 512)
            if ($n -gt 0) {
                $md5 = [System.Security.Cryptography.MD5]::Create()
                $hash = ([BitConverter]::ToString($md5.ComputeHash($buf, 0, $n)) -replace '-', '').Substring(0, 8).ToLower()
            }
        } finally { $fs.Dispose() }
    } catch { }
    return "$stamp`_$hash"
}

if ((Get-GuardPauseState -Component logarchive -GuardHome $GuardHome -Nt8Dir $base).Paused) { exit 0 }
[IO.File]::WriteAllText((Join-Path (Get-GuardPath state $GuardHome) 'logarchive.heartbeat'), [DateTime]::UtcNow.ToString('o'))

$copied = 0; $skipped = 0; $examined = 0; $rows = @()
$archivedOk = @{}          # live files with a verified complete archive OF THEIR OWN SESSION, this run
foreach ($s in $sources) {
    if (-not (Test-Path $s.Path)) { continue }
    foreach ($f in @(Get-ChildItem -Path $s.Path -Filter $s.Filter -File -ErrorAction SilentlyContinue)) {
        $examined++
        $key  = Get-SessionKey $f.FullName
        $name = [IO.Path]::GetFileNameWithoutExtension($f.Name)
        $ext  = [IO.Path]::GetExtension($f.Name)
        $dst  = Join-Path $archiveDir ("{0}__{1}{2}" -f $name, $key, $ext)
        $need = $true
        if (Test-Path $dst) { try { $need = ((Get-Item $dst).Length -lt $f.Length) } catch { $need = $true } }
        if ($need) {
            if (Copy-OpenFile $f.FullName $dst) { $copied++; $rows += "      + $([IO.Path]::GetFileName($dst)) ($([math]::Round($f.Length/1KB,1)) KB)" }
        } else { $skipped++ }
        try { if ((Test-Path $dst) -and ((Get-Item $dst).Length -ge $f.Length)) { $archivedOk[$f.FullName] = $dst } } catch { }
    }
}

# Retention, keyed on the ARCHIVE's own write time.
$purged = 0
$cut = [DateTime]::UtcNow.AddDays(-$RetentionDays)
foreach ($a in @(Get-ChildItem -Path $archiveDir -File -ErrorAction SilentlyContinue)) {
    if ($a.LastWriteTimeUtc -lt $cut) { try { Remove-Item $a.FullName -Force; $purged++ } catch { } }
}

# Prune the LIVE log/ and trace/ folders: only a file older than LivePruneDays whose OWN archive is
# verified this run (asking whether the NAME has an archive is wrong - names are reused by design).
$livePruned = 0
$liveCut = [DateTime]::UtcNow.AddDays(-$LivePruneDays)
foreach ($dir in @((Join-Path $base 'log'), (Join-Path $base 'trace'))) {
    if (-not (Test-Path $dir)) { continue }
    foreach ($f in @(Get-ChildItem -Path $dir -Filter '*.txt' -File -ErrorAction SilentlyContinue)) {
        if ($f.LastWriteTimeUtc -ge $liveCut) { continue }
        if (-not $archivedOk.ContainsKey($f.FullName)) { Log "live prune SKIPPED (no verified archive of this file): $($f.Name)"; continue }
        try { Remove-Item $f.FullName -Force -ErrorAction Stop; $livePruned++ } catch { }   # held open = today's file
    }
}
if ($livePruned -gt 0) { Log "live prune: removed $livePruned archived file(s) older than $LivePruneDays day(s) from log/ and trace/" }

$total = (Get-ChildItem -Path $archiveDir -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum)
if ($copied -gt 0 -or $purged -gt 0) {
    Log ("archived {0}, unchanged {1}, purged {2}; archive holds {3} files / {4:N1} MB" -f $copied, $skipped, $purged, $total.Count, ($total.Sum / 1MB))
    $rows | ForEach-Object { Log $_ }
}
if ($Report) {
    [ordered]@{ base = $base; archiveDir = $archiveDir; retentionDays = $RetentionDays; examined = $examined
                copiedThisRun = $copied; unchanged = $skipped; purged = $purged; livePruned = $livePruned
                livePruneDays = $LivePruneDays; archiveFiles = $total.Count; archiveMB = [math]::Round($total.Sum / 1MB, 2) } | ConvertTo-Json
}
