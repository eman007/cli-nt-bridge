# The NinjaTrader members the feed guard reads BY REFLECTION exist in this machine's NinjaTrader, under
# the names and types the add-on asks for. (Spec section 11.3: can the add-on read NinjaTrader's roll
# dates? Section 9: can it still reach the Strategies grid to re-enable after a recovery?) Reflection is
# used on purpose - a renamed member then degrades to a fallback instead of breaking the compile of every
# NinjaScript - so a rename would otherwise go unnoticed until someone wondered why the heartbeat says
# "rollover":"rule". Reads the DLLs only; never touches a running NinjaTrader. Skips (exit 0, says so)
# where NinjaTrader 8 is not installed.
. (Join-Path $PSScriptRoot 'FeedGuardTestKit.ps1')
$bin = Get-NinjaTraderBin
if (-not $bin) { 'SKIP  NinjaTrader 8 is not installed here - nothing to check against'; 'RESULT: skipped'; exit 0 }
"NinjaTrader $((Get-Item (Join-Path $bin 'NinjaTrader.exe')).VersionInfo.FileVersion) ($bin)"
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'NinjaTrader.Core.dll'))
$fail = 0
function Check([string]$Label, $Cond, [string]$Detail = '') {
    if ($Cond) { "  PASS  $Label" } else { $script:fail++; "  FAIL  $Label -> $Detail" }
}
$pubInst = [Reflection.BindingFlags]'Public,Instance'
$anyInst = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$pubStatic = [Reflection.BindingFlags]'Public,Static'

'=== the roll schedule (AutoReconnect.ReadNt8Rollovers)'
$mt = $asm.GetType('NinjaTrader.Cbi.MasterInstrument')
Check 'type NinjaTrader.Cbi.MasterInstrument' ($null -ne $mt) 'missing'
if ($mt) {
    $all = $mt.GetProperty('All', $pubStatic)
    Check 'MasterInstrument.All - public static, enumerable' ($all -and [Collections.IEnumerable].IsAssignableFrom($all.PropertyType)) "$($all.PropertyType)"
    $name = $mt.GetProperty('Name')
    Check 'MasterInstrument.Name - string' ($name -and $name.PropertyType -eq [string]) "$($name.PropertyType)"
    $rc = $mt.GetProperty('RolloverCollection')
    Check 'MasterInstrument.RolloverCollection - enumerable' ($rc -and [Collections.IEnumerable].IsAssignableFrom($rc.PropertyType)) "$($rc.PropertyType)"
}
$rt = $asm.GetType('NinjaTrader.Cbi.Rollover')
Check 'type NinjaTrader.Cbi.Rollover' ($null -ne $rt) 'missing'
if ($rt) {
    foreach ($p in 'ContractMonth', 'Date') {
        $pi = $rt.GetProperty($p)
        Check "Rollover.$p - DateTime" ($pi -and $pi.PropertyType -eq [DateTime]) "$($pi.PropertyType)"
    }
    $risk = $rt.GetProperty('IsRiskManagementOnly')
    Check 'Rollover.IsRiskManagementOnly - bool (risk-only entries are skipped)' ($risk -and $risk.PropertyType -eq [bool]) "$($risk.PropertyType)"
}

'=== the feed provider (AutoReconnect._mdConnProp) and the expiry check'
$it = $asm.GetType('NinjaTrader.Cbi.Instrument')
$md = $(if ($it) { $it.GetProperty('MarketDataConnection', $anyInst) })
Check 'Instrument.MarketDataConnection (internal) - a Connection' ($md -and $md.PropertyType.FullName -eq 'NinjaTrader.Cbi.Connection') "$($md.PropertyType)"
$exp = $(if ($it) { $it.GetProperty('Expiry', $pubInst) })
Check 'Instrument.Expiry - DateTime' ($exp -and $exp.PropertyType -eq [DateTime]) "$($exp.PropertyType)"

'=== the re-enable after a recovery (GetControlCenter / WithStrategiesGrid / GridEntries / ToggleEnableCheckbox)'
Add-Type -AssemblyName PresentationFramework
$gui = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'NinjaTrader.Gui.dll'))
$anyStatic = [Reflection.BindingFlags]'Public,NonPublic,Static'
$cc = $gui.GetType('NinjaTrader.Gui.ControlCenter')
Check 'type NinjaTrader.Gui.ControlCenter' ($null -ne $cc) 'missing'
if ($cc) {
    Check 'ControlCenter.Instance (or .Current) - static' (($null -ne $cc.GetProperty('Instance', $anyStatic)) -or ($null -ne $cc.GetProperty('Current', $anyStatic))) 'neither'
    Check 'ControlCenter.MainTabControl' (($null -ne $cc.GetProperty('MainTabControl', $anyInst)) -or ($null -ne $cc.GetField('MainTabControl', $anyInst))) 'missing'
}
$grid = $gui.GetType('NinjaTrader.Gui.NinjaScript.StrategiesGrid')
Check 'StrategiesGrid and its row source ("source")' ($null -ne $grid -and (($null -ne $grid.GetField('source', $anyInst)) -or ($null -ne $grid.GetProperty('source', $anyInst)))) 'missing'
$entry = $gui.GetType('NinjaTrader.Gui.NinjaScript.StrategiesGridEntry')
Check 'StrategiesGridEntry.Strategy and .IsEnabled' ($null -ne $entry -and $null -ne $entry.GetProperty('Strategy', $anyInst) -and $null -ne $entry.GetProperty('IsEnabled', $anyInst)) 'missing'
Check 'ButtonBase.OnClick (the real click)' ($null -ne [System.Windows.Controls.Primitives.ButtonBase].GetMethod('OnClick', [Reflection.BindingFlags]'NonPublic,Instance')) 'missing'

''
if ($fail -eq 0) { 'RESULT: all checks passed'; exit 0 }
"RESULT: $fail FAILED"; exit 1
