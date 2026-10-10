# Shared by every Guard test. Dot-source it:  . (Join-Path $PSScriptRoot 'TestKit.ps1')
# Each test runs in its own powershell.exe (Run-GuardTests.ps1), points NT8GUARD_HOME and NT8_DIR at
# fresh temp folders, and NEVER touches the real NinjaTrader folder, the real Guard home, or real tasks.
$ErrorActionPreference = 'Stop'
$script:Fail = 0
$script:AppDir = Split-Path -Parent $PSScriptRoot

function Check([string]$Label, $Cond, $Detail = '') {
    if ($Cond) { "  PASS  $Label" } else { $script:Fail++; "  FAIL  $Label -> $Detail" }
}

function New-TestDir([string]$Name) {
    $d = Join-Path $env:TEMP ("guardtest_{0}_{1}" -f $Name, [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    return $d
}

function Use-TestHomes([string]$Name) {
    # Fresh Guard home + fake NinjaTrader folder for this test process.
    $env:NT8GUARD_HOME = New-TestDir "$Name-home"
    $env:NT8_DIR = New-TestDir "$Name-nt8"
    return @{ Home = $env:NT8GUARD_HOME; Nt8 = $env:NT8_DIR }
}

function Import-GuardModules {
    foreach ($m in 'Guard.Paths', 'Guard.Log', 'Guard.Config', 'Guard.Events', 'Guard.Alerts', 'Guard.Pause', 'Guard.Start', 'Guard.Overwatch', 'Guard.FeedGuard') {
        $p = Join-Path $script:AppDir "lib\$m.psm1"
        if (Test-Path $p) { Import-Module $p -Force -DisableNameChecking }
    }
}

function Read-AllText([string]$Path) { if (Test-Path $Path) { return [IO.File]::ReadAllText($Path) } else { return '' } }

function Get-OverwatchSource { return [IO.File]::ReadAllText((Join-Path $script:AppDir 'Guard-Overwatch.ps1')) }

function Get-OverwatchFunction([string]$Name) {
    # One function's source, cut out of Guard-Overwatch.ps1 by brace matching, so a test runs the REAL
    # logic and can never pass against a stale copy of it. Load it with Invoke-Expression.
    $text = Get-OverwatchSource
    $i = $text.IndexOf("function $Name")
    if ($i -lt 0) { throw "function $Name not found in Guard-Overwatch.ps1" }
    $depth = 0; $started = $false
    for ($j = $i; $j -lt $text.Length; $j++) {
        $ch = $text[$j]
        if ($ch -eq '{') { $depth++; $started = $true }
        elseif ($ch -eq '}') { $depth--; if ($started -and $depth -eq 0) { return $text.Substring($i, $j - $i + 1) } }
    }
    throw "unbalanced braces extracting $Name"
}

function Get-OverwatchBlock([string]$From, [string]$To) {
    # The top-level script text from the line containing $From up to (not including) $To.
    $text = Get-OverwatchSource
    $s = $text.IndexOf($From)
    $e = $(if ($s -ge 0) { $text.IndexOf($To, $s) } else { -1 })
    if ($s -lt 0 -or $e -lt 0) { throw "block '$From' .. '$To' not found in Guard-Overwatch.ps1" }
    return $text.Substring($s, $e - $s)
}

function Complete-Test {
    ''
    if ($script:Fail) { "RESULT: $script:Fail FAILED"; exit 1 } else { 'RESULT: all checks passed'; exit 0 }
}
