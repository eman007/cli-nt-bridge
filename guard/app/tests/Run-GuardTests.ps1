# Run every Guard test (Test-*.ps1 here), each in its own powershell.exe so environment changes and
# module state never leak between tests. -Integration also runs Test-Integration-*.ps1, which touch
# Windows Task Scheduler (with throwaway task names and a temp Guard home).
#   powershell -NoProfile -ExecutionPolicy Bypass -File guard\app\tests\Run-GuardTests.ps1 [-Integration]
[CmdletBinding()] param([switch]$Integration)
$tests = @(Get-ChildItem $PSScriptRoot -Filter 'Test-*.ps1' | Where-Object { $_.Name -notlike 'Test-Integration-*' })
# The feed guard add-on's own tests live beside the bridge's tools (they cut real code out of
# addon\AutoReconnect.cs): every Test-FeedGuard*.ps1, plus the three that predate NT8 Guard.
$addOnTests = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\tools'))
$tests += @(Get-ChildItem $addOnTests -Filter 'Test-*.ps1' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'Test-FeedGuard*' -or @('Test-StallGate.ps1', 'Test-LadderTarget.ps1', 'Test-FrontMonth.ps1') -contains $_.Name })
if ($Integration) { $tests += @(Get-ChildItem $PSScriptRoot -Filter 'Test-Integration-*.ps1') }
$failed = @()
foreach ($t in $tests | Sort-Object Name) {
    "=== $($t.Name)"
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $t.FullName 2>&1
    $code = $LASTEXITCODE
    $out | ForEach-Object { "$_" } | Where-Object { $_ -match 'FAIL|RESULT|Exception|error' }
    if ($code -ne 0) { $failed += $t.Name }
}
''
if ($failed.Count) { "SUITE: $($failed.Count) of $($tests.Count) test files FAILED: $($failed -join ', ')"; exit 1 }
"SUITE: all $($tests.Count) test files passed"; exit 0
