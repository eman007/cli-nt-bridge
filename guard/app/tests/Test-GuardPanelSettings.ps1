# Guard.Panel.psm1: what the control panel's buttons do, without the window - presets and toggles,
# saving (guard.json AND the feed guard's own file), the watch list's text boxes, the phone-alert
# topic and its QR code, the diagnostics zip (secrets masked) and Forget.
. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
Import-Module (Join-Path $script:AppDir 'lib\Guard.Panel.psm1') -Force -DisableNameChecking
$topic = 'guard-secret-topic-123'

'--- presets and toggles'
$h = Use-TestHomes 'panelset'
$c = Get-GuardDefaults
$c.protection.reenableStrategies = $true
Set-GuardPreset -Config $c -Preset watch | Out-Null
Check 'a preset clears the toggles set on top of the old one' ((Get-GuardProtection $c).preset -eq 'watch' -and -not $c.protection.ContainsKey('reenableStrategies')) ((Get-GuardProtection $c).preset)
Set-GuardToggle -Config $c -Name relaunch -Value $true | Out-Null
Check 'a toggle away from the preset reads Custom' ((Get-GuardProtection $c).preset -eq 'custom' -and (Get-GuardProtection $c).relaunch) ''
Set-GuardToggle -Config $c -Name crashDumps -Value $true | Out-Null
Check 'crash reports are their own switch' ($c.protection.crashDumps -eq $true) ''
$threw = $false; try { Set-GuardToggle -Config $c -Name 'nonsense' -Value $true } catch { $threw = $true }
Check 'an unknown toggle is refused' $threw ''

'--- saving'
$c = Get-GuardDefaults
$c.alerts.ntfyTopic = $topic
$c.watch.instruments = @('MNQ ##-##')
$r = Save-GuardPanelSettings -Config $c -Section 'Watch list'
Check 'saved' ($r.Ok) $r.Message
Check 'guard.json written' ((Get-GuardConfig).watch.instruments[0] -eq 'MNQ ##-##') ''
$fg = Join-Path $h.Nt8 'AutoReconnect.config.json'
Check 'the feed guard file is written from it' ((Test-Path $fg) -and ([IO.File]::ReadAllText($fg) -match '"watchdogInstruments": \["MNQ ##-##"\]')) (Read-AllText $fg)
Check 'and the add-on is told to re-read it' (Test-Path (Join-Path $h.Nt8 'AutoReconnect.reload')) ''
Check 'message says what happened, topic never shown' ($r.Message -match 'saved' -and $r.Message -notmatch $topic) $r.Message
$ev = @(Read-GuardEvents -KindPattern '^settings\.saved$')
Check 'an event names the section' ($ev.Count -eq 1 -and $ev[0].text -match 'Watch list') ($ev | ConvertTo-Json -Compress)

$before = [IO.File]::ReadAllText((Get-GuardConfigPath))
$bad = Get-GuardConfig
$bad.watch.instruments = @()
$r = Save-GuardPanelSettings -Config $bad -Section 'Watch list'
Check 'an invalid change is refused with the reason' (-not $r.Ok -and $r.Message -match 'at least one instrument') $r.Message
Check 'and nothing is written' ([IO.File]::ReadAllText((Get-GuardConfigPath)) -ceq $before) ''

$c = Get-GuardConfig
$c.alerts.ntfyTopic = ''                               # without ntfy the add-on posts to the webhook...
$c.alerts.webhookUrl = 'https://hooks.example/a"b'     # ...and its reader cannot take a quote
$r = Save-GuardPanelSettings -Config $c -Section 'Alerts'
Check 'a value the add-on cannot read is refused before anything is written' (-not $r.Ok -and $r.Message -match 'quote') $r.Message
Check 'guard.json untouched by the refused save' ([IO.File]::ReadAllText((Get-GuardConfigPath)) -ceq $before) ''

Remove-Item $h.Nt8 -Recurse -Force
$c = Get-GuardConfig
$c.watch.staleSeconds = 90
$r = Save-GuardPanelSettings -Config $c -Section 'Watch list'
Check 'no NinjaTrader folder: guard.json still saved' ($r.Ok -and (Get-GuardConfig).watch.staleSeconds -eq 90) $r.Message
Check 'and the folder is not created' (-not (Test-Path $h.Nt8)) ''
Check 'the message says why the feed guard file waits' ($r.Message -match 'NinjaTrader''s folder') $r.Message

'--- text boxes'
$l = @(ConvertFrom-GuardListText " ES ##-##`r`n`r`nNQ ##-##`nES ##-##  ")
Check 'one entry per line, trimmed, no blanks or repeats' (($l -join '|') -eq 'ES ##-##|NQ ##-##') ($l -join '|')
Check 'an empty box is an empty list' (@(ConvertFrom-GuardListText '').Count -eq 0) ''
$p = @(Test-GuardWatchEntries -Instruments @('NQ ##-##', 'C:\x') -ExtraDates @('2026-11-27 13:15', '2026-12-24', '27/11/2026', '2026-02-30'))
Check 'a backslash in an instrument is refused' (@($p | Where-Object { $_ -match 'C:\\x' }).Count -eq 1) ($p -join ' | ')
Check 'dates: day-first and impossible ones refused' (@($p | Where-Object { $_ -match '27/11/2026' }).Count -eq 1 -and @($p | Where-Object { $_ -match '2026-02-30' }).Count -eq 1) ($p -join ' | ')
Check 'a date and a date with a time are fine' (@($p | Where-Object { $_ -match "^'(2026-11-27 13:15|2026-12-24)'" }).Count -eq 0) ($p -join ' | ')

'--- phone alerts'
$t1 = New-GuardNtfyTopic; $t2 = New-GuardNtfyTopic
Check 'topic: long, random, ntfy-safe' ($t1 -match '^nt8guard-[a-z0-9]{22}$' -and $t1 -ne $t2) $t1
Check 'subscribe address' ((Get-GuardNtfyAddress -Config @{ alerts = @{ ntfyServer = 'https://ntfy.sh/'; ntfyTopic = 'abc' } }) -eq 'https://ntfy.sh/abc') ''
$py = (Get-Command py.exe -ErrorAction SilentlyContinue).Source
if (-not $py) { $py = (Get-Command python.exe -ErrorAction SilentlyContinue).Source }
if ($py) {
    $c = Get-GuardDefaults; $c.python.exe = $py
    $bytes = Get-GuardQrPng -Text "https://ntfy.sh/$topic" -Config $c
    Check 'QR code comes back as PNG bytes' ($bytes.Length -gt 100 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50) $bytes.Length
    Check 'the text is not left in this process''s environment' (-not $env:NT8GUARD_QR_TEXT) ''
} else { '  SKIP  no Python on this machine for the QR code' }
$threw = $false
try { Get-GuardQrPng -Text 'x' -Config (Get-GuardDefaults) -ToolsDir (New-TestDir noqr) | Out-Null } catch { $threw = ($_.Exception.Message -match 'QR code.*Python') }
Check 'no runtime: a clear error' $threw ''

'--- diagnostics zip'
$h = Use-TestHomes 'paneldiag'
$c = Get-GuardDefaults; $c.alerts.ntfyTopic = $topic
$c.alerts.webhookUrl = 'https://hooks.example.com/notify?key=ZQXJHOOKKEY1&chan=7'   # guard.json stores the & as \u0026
Save-GuardConfig $c
Write-GuardLog -Component overwatch -Message "posted to https://ntfy.sh/$topic"
Write-GuardEvent -Component overwatch -Kind 'nt8.fault' -Severity alert -Text "topic $topic"
$profileName = Split-Path ([Environment]::GetFolderPath('UserProfile')) -Leaf
Write-GuardEvent -Component overwatch -Kind 'nt8.fault' -Severity info -Text "dump at C:\Users\$env:USERNAME\Documents\x.dmp"   # stored as C:\\Users\\...
Write-GuardLog -Component overwatch -Message "python at C:/Users/$profileName/py/python.exe"
[IO.File]::WriteAllText((Join-Path (Get-GuardPath secrets) 'nt8.cred'), 'SECRET')
[IO.File]::WriteAllText((Join-Path (Get-GuardPath backups) 'rows.json'), '{"version":2,"rows":{"A@B":{"strategies":{"Userdata":"PROPRIETARY"}}}}')
[IO.File]::WriteAllText((Join-Path (Get-GuardPath state) 'overwatch.heartbeat'), 'x')
[IO.File]::WriteAllText((Join-Path $h.Nt8 'AutoReconnect.config.json'), "{ `"webhookUrl`": `"https://ntfy.sh/$topic`" }")
[IO.File]::WriteAllText((Join-Path $h.Nt8 'AutoReconnect.log'), ((1..3000 | ForEach-Object { "line $_" }) -join "`r`n"))
$zip = Join-Path (New-TestDir diagout) 'diag.zip'
# a watchdog writing its log at this very moment
$held = [IO.File]::Open((Join-Path (Get-GuardPath logs) 'overwatch.log'), 'Open', 'ReadWrite', 'ReadWrite')
try { $r = New-GuardDiagnosticsZip -OutPath $zip -Processes { , @() } -TaskState { param($n) 'Ready' } } finally { $held.Dispose() }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($zip)
$names = @($z.Entries | ForEach-Object { $_.FullName })
$all = ($z.Entries | ForEach-Object { $s = New-Object IO.StreamReader($_.Open()); $s.ReadToEnd(); $s.Close() }) -join "`n"
$arlog = @($z.Entries | Where-Object FullName -eq 'nt8/AutoReconnect.log')[0]
$arText = $(if ($arlog) { $s = New-Object IO.StreamReader($arlog.Open()); $s.ReadToEnd(); $s.Close() } else { '' })
$z.Dispose()
Check 'a log held open for writing is still read' ($all -match 'posted to https://ntfy\.sh/\*\*\*') ''
Check 'has settings, logs, events, status' ((@('guard.json', 'logs/overwatch.log', 'events/overwatch.jsonl', 'status.txt', 'nt8/AutoReconnect.config.json') | Where-Object { $names -notcontains $_ }).Count -eq 0) ($names -join ', ')
Check 'no secrets folder, no strategy settings' (-not ($names -match 'secrets|rows\.json')) ($names -join ', ')
Check 'the topic appears nowhere' ($all -notmatch [regex]::Escape($topic)) ''
Check 'the webhook address appears nowhere, raw or JSON-escaped' ($all -notmatch 'ZQXJHOOKKEY1') (([regex]::Match($all, '.{40}ZQXJHOOKKEY1.{10}')).Value)
$userRx = '(?i)Users[\\/]+(' + [regex]::Escape($env:USERNAME) + '|' + [regex]::Escape($profileName) + ')(?=[\\/"]|$)'
Check 'the Windows user name appears in no path, whatever the slashes' ($all -notmatch $userRx) (([regex]::Match($all, ".{20}$userRx")).Value)
Check 'the password file content is not there' ($all -notmatch 'SECRET') ''
Check 'forward slashes in entry names' (-not ($names -match '\\')) ($names -join ', ')
Check 'long logs keep their last 2000 lines' ($arText -match 'line 3000' -and $arText -notmatch 'line 1000\b') ''
Check 'returns what it wrote and a caution' ($r.Path -eq $zip -and $r.Note -match 'account') $r.Note

'--- forget'
$calls = New-Object System.Collections.Generic.List[string]
$fake = { param($s, $a, $t) $calls.Add("$s $($a -join ' ')"); [pscustomobject]@{ ExitCode = 0; Err = ''; TimedOut = $false
          Out = '{"forgotten": ["Alpha"], "kept": [], "saved": [], "failed": []}' } }
$r = Invoke-GuardBackupForget -Name 'Alpha' -Config (Get-GuardDefaults) -Python $fake
Check 'forget runs the exporter with --forget' ($calls[0] -match '^backup_export\.py .*--forget Alpha .*--json') $calls[0]
Check 'forgotten reported' ($r.Ok -and $r.Message -match 'Alpha' -and $r.Message -match 'forgot') $r.Message
$refuse = { param($s, $a, $t) [pscustomobject]@{ ExitCode = 1; Err = ''; TimedOut = $false
            Out = '{"forgotten": [], "kept": [{"name": "Alpha", "why": "is in NinjaTrader''s list right now - remove it there first"}]}' } }
$r = Invoke-GuardBackupForget -Name 'Alpha' -Config (Get-GuardDefaults) -Python $refuse
Check 'a refusal says why' (-not $r.Ok -and $r.Message -match 'remove it there first') $r.Message
# Forget is a full save: the exporter reports each strategy's change ONCE, in whichever save sees it first.
$withChange = { param($s, $a, $t) [pscustomobject]@{ ExitCode = 0; Err = ''; TimedOut = $false
                Out = '{"forgotten": ["Alpha"], "kept": [], "saved": [], "failed": [], "transitions": [{"name": "Zed", "old": "live", "new": "removed"}]}' } }
$r = Invoke-GuardBackupForget -Name 'Alpha' -Config (Get-GuardDefaults) -Python $withChange
Check 'a strategy that left the list in that same save is still reported' ($r.Ok -and @(Read-GuardEvents -KindPattern '^backups\.removed$' | Where-Object { $_.text -match 'Zed' }).Count -eq 1) ''
$crash = { param($s, $a, $t) [pscustomobject]@{ ExitCode = 1; Err = 'ERROR: no NinjaTrader database'; TimedOut = $false; Out = '' } }
$r = Invoke-GuardBackupForget -Name 'Alpha' -Config (Get-GuardDefaults) -Python $crash
Check 'a failure passes the reason on' (-not $r.Ok -and $r.Message -match 'no NinjaTrader database') $r.Message

Complete-Test
