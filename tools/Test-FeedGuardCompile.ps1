# The WHOLE feed guard add-on (addon\AutoReconnect.cs) compiles against this machine's NinjaTrader
# assemblies with the C# 5 compiler.
#
# WHY: NinjaTrader compiles every NinjaScript file on the machine into ONE assembly. An add-on that does
# not compile there takes every indicator and strategy down with it - "silently dropped" is the good
# outcome. C# 5 is a subset of NinjaTrader's C# 9, so passing here means it compiles there; and the
# references are NinjaTrader's own DLLs, so a member that does not exist in this NinjaTrader version is
# caught here, not in a user's NinjaScript Editor.
#
# Skips (exit 0, says so) on a machine without NinjaTrader 8 - there is nothing to compile against.
. (Join-Path $PSScriptRoot 'FeedGuardTestKit.ps1')

$bin = Get-NinjaTraderBin
if (-not $bin) { 'SKIP  NinjaTrader 8 is not installed here - nothing to compile against'; 'RESULT: skipped'; exit 0 }
$nt8 = (Get-Item (Join-Path $bin 'NinjaTrader.exe')).VersionInfo.FileVersion
"compiling against NinjaTrader $nt8 ($bin)"

$fw = Split-Path $script:Csc -Parent
$refs = @((Join-Path $bin 'NinjaTrader.Core.dll'), (Join-Path $bin 'NinjaTrader.Gui.dll'),
          "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll",
          "$fw\System.Xaml.dll", "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Xml.dll")
$out = Join-Path $env:TEMP ("fgcompile_{0}.dll" -f [guid]::NewGuid().ToString('N').Substring(0, 8))
$refArgs = @($refs | ForEach-Object { "/r:$_" })
$msgs = & $script:Csc /nologo /t:library /nowarn:1701,1702 "/out:$out" @refArgs $script:AddOnPath 2>&1
$built = Test-Path $out
Remove-Item $out -ErrorAction SilentlyContinue
$errors = @($msgs | Where-Object { "$_" -match ': error ' })
$errors | ForEach-Object { "  $_" }
if ($built -and $errors.Count -eq 0) { '  PASS  AutoReconnect.cs compiles (C# 5) against NinjaTrader ' + $nt8; 'RESULT: all checks passed'; exit 0 }
"  FAIL  AutoReconnect.cs does not compile: $($errors.Count) error(s)"
'RESULT: 1 FAILED'
exit 1
