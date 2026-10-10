# Render-Guide.ps1 - the setup guide as a PDF and the Quick Start as a 1920x1080 PNG (spec 8), with headless
# Microsoft Edge (or Chrome):
#
#   powershell -ExecutionPolicy Bypass -File guard\build\Render-Guide.ps1 -OutDir <dir> [-ShotsDir <dir>]
#
# The HTML in guard\docs\guide loads the panel's own fonts by a relative path; here each page is copied into
# a work folder with those fonts inlined and the screenshots beside it, so the result never depends on the
# network or on where the sources sit. Refuses (throws) when a page did not render, or rendered without its
# fonts.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutDir, [string]$ShotsDir, [string]$SourceDir)
$ErrorActionPreference = 'Stop'
if (-not $SourceDir) { $SourceDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\guide' }
if (-not $ShotsDir) { $ShotsDir = Join-Path $SourceDir 'shots' }
$guidePage = 'NT8 Guard - Setup Guide'
$quickPage = 'NT8 Guard - Quick Start'

$browser = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
             "$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") |
           Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $browser) { throw 'Rendering the guide needs Microsoft Edge or Google Chrome.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('nt8guard-guide-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$profile = Join-Path $work 'profile'
New-Item -ItemType Directory -Path (Join-Path $work 'shots'), $profile -Force | Out-Null
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
try {
    Copy-Item (Join-Path $ShotsDir '*.png') (Join-Path $work 'shots')
    foreach ($page in $guidePage, $quickPage) {
        $html = [IO.File]::ReadAllText((Join-Path $SourceDir "$page.html"))
        $html = [regex]::Replace($html, 'url\("([^"]+\.ttf)"\)', {
            param($m)
            $font = [IO.Path]::GetFullPath((Join-Path $SourceDir $m.Groups[1].Value))
            'url("data:font/ttf;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($font)) + '")'
        })
        [IO.File]::WriteAllText((Join-Path $work "$page.html"), $html, (New-Object Text.UTF8Encoding($false)))
    }
    $run = {
        param([string[]]$Arguments)
        $common = @('--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-extensions',
                    "--user-data-dir=`"$profile`"")
        $p = Start-Process -FilePath $browser -ArgumentList ($common + $Arguments) -Wait -PassThru -WindowStyle Hidden
        return $p.ExitCode
    }
    $pdf = Join-Path $OutDir "$guidePage.pdf"
    $png = Join-Path $OutDir "$quickPage.png"
    foreach ($f in $pdf, $png) { if (Test-Path $f) { [IO.File]::Delete($f) } }
    $uri = { param($n) ([Uri](Join-Path $work "$n.html")).AbsoluteUri }
    [void](& $run @('--no-pdf-header-footer', '--print-to-pdf-no-header', "--print-to-pdf=`"$pdf`"", "`"$(& $uri $guidePage)`""))
    [void](& $run @('--hide-scrollbars', '--force-device-scale-factor=1', '--window-size=1920,1080', "--screenshot=`"$png`"", "`"$(& $uri $quickPage)`""))

    if (-not (Test-Path $pdf)) { throw "the guide PDF was not written: $pdf" }
    $bytes = [IO.File]::ReadAllBytes($pdf)
    if ([Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne '%PDF') { throw "not a PDF: $pdf" }
    if ([Text.Encoding]::ASCII.GetString($bytes).IndexOf('IBMPlex') -lt 0) { throw "the guide PDF rendered without its fonts: $pdf" }
    if (-not (Test-Path $png)) { throw "the Quick Start PNG was not written: $png" }
    Add-Type -AssemblyName System.Drawing
    $img = [Drawing.Image]::FromFile($png)
    try { $size = "$($img.Width)x$($img.Height)" } finally { $img.Dispose() }
    if ($size -ne '1920x1080') { throw "the Quick Start PNG is $size, not 1920x1080" }
    # What these were rendered from, so a stale committed copy is caught (Test-GuardRelease).
    Import-Module (Join-Path $PSScriptRoot 'Guard.Release.psm1') -DisableNameChecking
    [IO.File]::WriteAllText((Join-Path $OutDir 'rendered-from.txt'), (Get-GuardGuideStamp -SourceDir $SourceDir -ShotsDir $ShotsDir) + "`n")
    "rendered $pdf ($([math]::Round($bytes.Length / 1KB)) KB)"
    "rendered $png ($size)"
} finally {
    for ($i = 0; $i -lt 10 -and (Test-Path $work); $i++) { try { [IO.Directory]::Delete($work, $true) } catch { Start-Sleep -Milliseconds 500 } }
}
