. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes events

'=== write, then read newest first'
Write-GuardEvent -Component relaunch -Kind 'nt8.launch' -Severity warn -Text 'first'
Start-Sleep -Milliseconds 20
Write-GuardEvent -Component overwatch -Kind 'fault.FEED_DEAD' -Severity alert -Text 'second "quoted"' -Data @{ feedAge = 312 }
Start-Sleep -Milliseconds 20
Write-GuardEvent -Component relaunch -Kind 'nt8.stuck' -Severity warn -Text "third`nwith a newline"
$all = @(Read-GuardEvents)
Check 'three events' ($all.Count -eq 3) $all.Count
Check 'newest first' ($all[0].text -like 'third*' -and $all[2].text -eq 'first')
Check 'quotes and newlines round-trip' ($all[1].text -eq 'second "quoted"' -and $all[0].text -eq "third`nwith a newline")
Check 'data kept' ($all[1].data.feedAge -eq 312)

'=== filters'
Check 'by component' (@(Read-GuardEvents -Component relaunch).Count -eq 2)
Check 'by kind pattern' (@(Read-GuardEvents -KindPattern '^nt8\.(launch|stuck)$').Count -eq 2)
Check 'by time' (@(Read-GuardEvents -SinceUtc ([DateTime]::UtcNow.AddMinutes(1))).Count -eq 0)
Check 'max' (@(Read-GuardEvents -Max 1).Count -eq 1)

'=== a damaged line is skipped, never fatal'
Add-Content -Path (Join-Path $h.Home 'events\relaunch.jsonl') -Value '{ this is not json' -Encoding UTF8
Check 'still three good events' (@(Read-GuardEvents).Count -eq 3)

'=== the feed guard writes its own file inside NT8''s folder; readers merge it'
$fg = Get-FeedGuardEventsPath
New-Item -ItemType Directory -Path (Split-Path $fg -Parent) -Force | Out-Null
[IO.File]::WriteAllText($fg, ('{"utc":"' + [DateTime]::UtcNow.AddSeconds(5).ToString('o') + '","component":"feedguard","kind":"feed.stall","severity":"warn","text":"no data","data":{}}' + "`n"))
$m = @(Read-GuardEvents)
Check 'merged and newest' ($m.Count -eq 4 -and $m[0].component -eq 'feedguard')

'=== reading shares the file: a writer mid-append is neither blocked nor hidden'
# The control panel reads every 2 s. A reader that denies writing makes a watchdog's Write-GuardEvent fail
# (it does not retry) - and the relauncher's backoff counts those events.
$held = [IO.File]::Open($fg, 'Open', 'ReadWrite', 'ReadWrite')     # the add-on, half way through a write
try { $m = @(Read-GuardEvents) } finally { $held.Dispose() }
Check 'a file held open for writing is still read' (@($m | Where-Object { $_.component -eq 'feedguard' }).Count -eq 1) "$($m.Count) events"
$rl = Join-Path $h.Home 'events\relaunch.jsonl'
$reader = [IO.File]::Open($rl, 'Open', 'Read', 'ReadWrite, Delete')     # how Read-GuardEvents opens a file now
try { Write-GuardEvent -Component relaunch -Kind 'nt8.launch' -Text 'started while being read' } finally { $reader.Dispose() }
Check 'a write while a reader holds the file lands' (@(Read-GuardEvents -Component relaunch -KindPattern '^nt8\.launch$' | Where-Object { $_.text -eq 'started while being read' }).Count -eq 1) ''

Complete-Test
