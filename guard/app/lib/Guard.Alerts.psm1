# Phone alerts (ntfy) and an optional generic webhook. Bounded (10 s), never throws, and never writes
# the private topic or the webhook address into a log, an event, or the console: the topic IS the
# password to the alert channel - anyone who reads it can read (and send) your alerts.
Import-Module (Join-Path $PSScriptRoot 'Guard.Paths.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Log.psm1')
Import-Module (Join-Path $PSScriptRoot 'Guard.Events.psm1')

$script:DefaultSender = {
    param([string]$Uri, [hashtable]$Headers, [byte[]]$Body, [string]$ContentType)
    # Bytes, not a string: Windows PowerShell 5.1 encodes a string body as ISO-8859-1.
    Invoke-RestMethod -Uri $Uri -Method Post -Headers $Headers -Body $Body -ContentType $ContentType -TimeoutSec 10 | Out-Null
}

function Enable-GuardTls12 {
    # A fresh Windows 10 PowerShell 5.1 may not offer TLS 1.2, which ntfy.sh requires.
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
}

function Hide-GuardSecret {
    [CmdletBinding()] param([AllowEmptyString()][string]$Text, [hashtable]$Config)
    $out = $Text
    foreach ($s in @($Config.alerts.ntfyTopic, $Config.alerts.webhookUrl)) {
        if ($s) { $out = $out.Replace([string]$s, '***') }
    }
    return $out
}

function Send-GuardAlert {
    # Returns @{ ntfy = 'sent' | 'off' | 'failed: ...'; webhook = same }. Severity maps to an ntfy
    # priority through alerts.priorities (info 3, warn 4, alert 5 by default).
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Title, [Parameter(Mandatory)][AllowEmptyString()][string]$Body,
          [ValidateSet('info', 'warn', 'alert')][string]$Severity = 'alert', [Parameter(Mandatory)][hashtable]$Config,
          [string]$Component = 'guard', [string]$GuardHome, [scriptblock]$Sender)
    if (-not $Sender) { $Sender = $script:DefaultSender }
    Enable-GuardTls12
    $a = $Config.alerts
    $res = [ordered]@{ ntfy = 'off'; webhook = 'off' }
    # HTTP headers are ASCII; a non-ASCII title would make the whole request fail.
    $tagged = ("[$env:COMPUTERNAME] $Title") -replace '[^\x20-\x7E]', '-'
    $prio = [string]$a.priorities[$Severity]
    if (-not $prio) { $prio = @{ info = '3'; warn = '4'; alert = '5' }[$Severity] }
    if ($a.ntfyTopic) {
        $uri = ([string]$a.ntfyServer).TrimEnd('/') + '/' + $a.ntfyTopic
        try {
            & $Sender $uri @{ Title = $tagged; Priority = $prio; Tags = 'rotating_light' } ([Text.Encoding]::UTF8.GetBytes($Body)) 'text/plain; charset=utf-8'
            $res.ntfy = 'sent'
        } catch { $res.ntfy = 'failed: ' + (Hide-GuardSecret $_.Exception.Message $Config) }
    }
    if ($a.webhookUrl) {
        $json = [ordered]@{ title = $Title; body = $Body; severity = $Severity; host = $env:COMPUTERNAME
                            component = $Component; utc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Compress
        try {
            & $Sender ([string]$a.webhookUrl) @{} ([Text.Encoding]::UTF8.GetBytes($json)) 'application/json; charset=utf-8'
            $res.webhook = 'sent'
        } catch { $res.webhook = 'failed: ' + (Hide-GuardSecret $_.Exception.Message $Config) }
    }
    Write-GuardLog -Component $Component -Message "ALERT [$Title] $Body | ntfy=$($res.ntfy) webhook=$($res.webhook)" -GuardHome $GuardHome
    Write-GuardEvent -Component $Component -Kind 'alert.sent' -Severity $Severity -Text $Title `
                     -Data @{ body = $Body; ntfy = $res.ntfy; webhook = $res.webhook } -GuardHome $GuardHome
    return [pscustomobject]$res
}

function Test-GuardAlert {
    # Push a test through the real configured path, from THIS machine (an alert path verified from
    # another machine is not verified). Says what happened without revealing the topic.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config, [string]$GuardHome, [scriptblock]$Sender)
    if (-not $Config.alerts.ntfyTopic -and -not $Config.alerts.webhookUrl) {
        return [pscustomobject]@{ ok = $false; message = 'No alert channel is set up: alerts are only logged.' }
    }
    $r = Send-GuardAlert -Title 'NT8 Guard test' -Body "Test alert from $env:COMPUTERNAME at $([DateTime]::UtcNow.ToString('u'))" `
                         -Severity info -Config $Config -Component guard -GuardHome $GuardHome -Sender $Sender
    $ok = ($r.ntfy -eq 'sent' -or $r.ntfy -eq 'off') -and ($r.webhook -eq 'sent' -or $r.webhook -eq 'off')
    return [pscustomobject]@{ ok = $ok; message = "phone (ntfy): $($r.ntfy); webhook: $($r.webhook)" }
}

function Publish-GuardConfigProblem {
    # A watchdog loads guard.json every minute: log, event and alert a settings problem ONCE per change,
    # and say once when the file is readable again. Never throws. The text is masked because a parse
    # error can quote the file around the topic.
    [CmdletBinding()] param([Parameter(Mandatory)][hashtable]$Config, [Parameter(Mandatory)][string]$Component, [string]$GuardHome)
    try {
        $f = Join-Path (Get-GuardPath state $GuardHome) "$Component.configerror"
        $was = $(if (Test-Path $f) { [IO.File]::ReadAllText($f) } else { '' })
        $now = Hide-GuardSecret -Text ([string]$Config.loadError) -Config $Config
        if ($now -eq $was) { return }
        if ($now) {
            [IO.File]::WriteAllText($f, $now)
            Write-GuardLog -Component $Component -Message "SETTINGS: $now" -GuardHome $GuardHome
            Write-GuardEvent -Component $Component -Kind 'guard.config' -Severity warn -Text $now -GuardHome $GuardHome
            Send-GuardAlert -Title 'NT8 Guard settings problem' -Body $now -Severity warn -Config $Config -Component $Component -GuardHome $GuardHome | Out-Null
        } else {
            Remove-Item $f -Force -ErrorAction SilentlyContinue
            Write-GuardLog -Component $Component -Message 'guard.json is readable again' -GuardHome $GuardHome
        }
    } catch { }
}

Export-ModuleMember -Function Enable-GuardTls12, Hide-GuardSecret, Send-GuardAlert, Test-GuardAlert, Publish-GuardConfigProblem
