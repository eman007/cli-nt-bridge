. (Join-Path $PSScriptRoot 'TestKit.ps1')
Import-GuardModules
$h = Use-TestHomes alerts
$topic = 'private_topic_7f3a9'
$script:calls = @()
$fake = { param($Uri, $Headers, $Body, $ContentType)
          $script:calls += [pscustomobject]@{ Uri = $Uri; Headers = $Headers; Body = [Text.Encoding]::UTF8.GetString($Body); Type = $ContentType } }

'=== no channel: logged and evented, nothing sent'
$c = Get-GuardDefaults
$r = Send-GuardAlert -Title 'NT8 is DOWN' -Body 'b' -Severity alert -Config $c -Component relaunch -Sender $fake
Check 'both off' ($r.ntfy -eq 'off' -and $r.webhook -eq 'off')
Check 'nothing sent' ($script:calls.Count -eq 0)
Check 'logged' ((Read-AllText (Join-Path $h.Home 'logs\relaunch.log')) -match 'NT8 is DOWN')
Check 'evented' (@(Read-GuardEvents -KindPattern '^alert\.sent$').Count -eq 1)

'=== ntfy: right address, host-tagged ASCII title, priority from severity, UTF-8 body'
$c.alerts.ntfyTopic = $topic
$r = Send-GuardAlert -Title "Strategies put back - caf$([char]0xE9)" -Body "r$([char]0xE9)sum$([char]0xE9) ok" -Severity info -Config $c -Sender $fake
Check 'sent' ($r.ntfy -eq 'sent')
Check 'address' ($script:calls[0].Uri -eq "https://ntfy.sh/$topic")
Check 'title tagged with the computer name' ($script:calls[0].Headers.Title.StartsWith("[$env:COMPUTERNAME] "))
Check 'title is plain ASCII' ($script:calls[0].Headers.Title -notmatch '[^\x20-\x7E]')
Check 'info -> priority 3' ($script:calls[0].Headers.Priority -eq '3')
Check 'body is UTF-8' ($script:calls[0].Body -eq "r$([char]0xE9)sum$([char]0xE9) ok")
$null = Send-GuardAlert -Title 't' -Body 'b' -Severity alert -Config $c -Sender $fake
Check 'alert -> priority 5' ($script:calls[1].Headers.Priority -eq '5')

'=== webhook gets JSON'
$c.alerts.webhookUrl = 'https://hooks.example.test/abc'
$script:calls = @()
$null = Send-GuardAlert -Title 'Webhook' -Body 'x' -Severity warn -Config $c -Sender $fake
$w = $script:calls | Where-Object { $_.Uri -eq 'https://hooks.example.test/abc' }
$j = $w.Body | ConvertFrom-Json
Check 'json fields' ($j.title -eq 'Webhook' -and $j.severity -eq 'warn' -and $j.host -eq $env:COMPUTERNAME)

'=== a system name of your own (two PCs on one topic, 2026-10-15)'
$c.alerts.systemName = 'VPS'
$script:calls = @()
$null = Send-GuardAlert -Title 'NT8 is DOWN' -Body 'b' -Severity alert -Config $c -Sender $fake
$n = $script:calls | Where-Object { $_.Uri -like 'https://ntfy.sh/*' }
Check 'the title starts with the name you gave' ($n.Headers.Title -eq '[VPS] NT8 is DOWN') $n.Headers.Title
$j = ($script:calls | Where-Object { $_.Uri -eq 'https://hooks.example.test/abc' }).Body | ConvertFrom-Json
Check 'the webhook carries it, and the Windows name still' ($j.system -eq 'VPS' -and $j.host -eq $env:COMPUTERNAME) "$($j.system) $($j.host)"
$script:calls = @()
$null = Test-GuardAlert -Config $c -Sender $fake
$n = $script:calls | Where-Object { $_.Uri -like 'https://ntfy.sh/*' }
Check 'the test alert says which system sent it' ($n.Body -match '^Test alert from VPS ' -and $n.Headers.Title -eq '[VPS] NT8 Guard test') "$($n.Headers.Title) / $($n.Body)"
$c.alerts.systemName = '   '
$script:calls = @()
$null = Send-GuardAlert -Title 't' -Body 'b' -Severity alert -Config $c -Sender $fake
Check 'blank = the Windows name' (($script:calls | Where-Object { $_.Uri -like 'https://ntfy.sh/*' }).Headers.Title -eq "[$env:COMPUTERNAME] t") ''
$c.alerts.systemName = ''

'=== a failing send never throws and never leaks the topic or webhook address'
$boom = { param($Uri) throw "could not reach $Uri" }
$r = Send-GuardAlert -Title 't' -Body 'b' -Config $c -Sender $boom
Check 'reported as failed' ($r.ntfy -like 'failed:*' -and $r.webhook -like 'failed:*')
Check 'topic masked in the result' ($r.ntfy -notmatch $topic -and $r.ntfy -match '\*\*\*')
$everything = (Get-ChildItem $h.Home -Recurse -File | ForEach-Object { Read-AllText $_.FullName }) -join "`n"
Check 'topic in no log or event file' ($everything -notmatch $topic)
Check 'webhook address in no log or event file' ($everything -notmatch 'hooks\.example\.test')

'=== test alert'
$c2 = Get-GuardDefaults
Check 'no channel -> says so' (-not (Test-GuardAlert -Config $c2 -Sender $fake).ok)
$t = Test-GuardAlert -Config $c -Sender $fake
Check 'with a channel -> ok' $t.ok
Check 'message names no topic' ($t.message -notmatch $topic)

Complete-Test
