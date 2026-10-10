# The release build's pieces (spec 8), kept out of Build-Release.ps1 so each is tested on its own
# (guard\app\tests\Test-GuardRelease.ps1):
#   Get-GuardPythonRuntime   the embeddable CPython zip: downloaded once into a cache, refused unless it
#                            matches its pinned SHA-256
#   Expand-GuardPython       unpack it, refuse a python.exe the Python Software Foundation did not sign,
#                            add the backup tools (never their tests or caches)

function Get-GuardPythonRuntime {
    # The pinned python-<Version>-embed-amd64.zip, from the cache or downloaded into it. Throws on a
    # download that does not match the pin, and leaves no such file behind.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$CacheDir, [Parameter(Mandatory)][string]$Version,
          [Parameter(Mandatory)][string]$Sha256, [string]$Url)
    if (-not $Url) { $Url = "https://www.python.org/ftp/python/$Version/python-$Version-embed-amd64.zip" }
    if (-not (Test-Path $CacheDir)) { New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null }
    $zip = Join-Path $CacheDir "python-$Version-embed-amd64.zip"
    if ((Test-Path $zip) -and (Get-FileHash $zip -Algorithm SHA256).Hash -eq $Sha256.ToUpperInvariant()) { return $zip }
    if (Test-Path $zip) { [IO.File]::Delete($zip) }
    $tmp = "$zip.download"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        (New-Object Net.WebClient).DownloadFile($Url, $tmp)
        $got = (Get-FileHash $tmp -Algorithm SHA256).Hash
        if ($got -ne $Sha256.ToUpperInvariant()) { throw "the Python download does not match its pinned SHA-256 (got $got, pinned $Sha256): $Url" }
        [IO.File]::Move($tmp, $zip)
    } finally { if (Test-Path $tmp) { [IO.File]::Delete($tmp) } }
    return $zip
}

function Expand-GuardPython {
    # Unpack the runtime into -Dest (which must not exist yet) and copy the backup tools beside python.exe:
    # the embeddable runtime's ._pth puts its own folder on sys.path, so the tools import from there.
    # -Signature { param($exe) -> @{ Status; Signer } } is a test seam; the default is Authenticode.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Zip, [Parameter(Mandatory)][string]$Dest, [Parameter(Mandatory)][string]$ToolsSource,
          [scriptblock]$Signature)
    if (-not $Signature) {
        $Signature = { param($exe) $s = Get-AuthenticodeSignature -FilePath $exe
                       [pscustomobject]@{ Status = [string]$s.Status; Signer = $(if ($s.SignerCertificate) { $s.SignerCertificate.Subject } else { '' }) } }
    }
    if (Test-Path $Dest) { throw "$Dest already exists" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        [IO.Compression.ZipFile]::ExtractToDirectory($Zip, $Dest)
        $exe = Join-Path $Dest 'python.exe'
        if (-not (Test-Path $exe)) { throw "the runtime zip holds no python.exe: $Zip" }
        $sig = & $Signature $exe
        if ($sig.Status -ne 'Valid') { throw "python.exe is not validly signed ($($sig.Status)): $Zip" }
        if ([string]$sig.Signer -notmatch 'Python Software Foundation') { throw "python.exe is signed by $($sig.Signer), not the Python Software Foundation: $Zip" }
        Copy-GuardTree -From $ToolsSource -To $Dest -Skip @('tests', '__pycache__', 'README.md', '.pytest_cache')
    } catch {
        if (Test-Path $Dest) { [IO.Directory]::Delete($Dest, $true) }
        throw
    }
    return $Dest
}

function Copy-GuardTree {
    # Copy a folder's files and subfolders, skipping any file or folder named in -Skip and every .pyc.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$From, [Parameter(Mandatory)][string]$To, [string[]]$Skip = @())
    if (-not (Test-Path $To)) { New-Item -ItemType Directory -Path $To -Force | Out-Null }
    foreach ($item in Get-ChildItem -LiteralPath $From -Force) {
        if ($Skip -contains $item.Name -or $item.Extension -eq '.pyc') { continue }
        $target = Join-Path $To $item.Name
        if ($item.PSIsContainer) { Copy-GuardTree -From $item.FullName -To $target -Skip $Skip }
        else { Copy-Item -LiteralPath $item.FullName -Destination $target -Force }
    }
}

function Get-GuardAppFiles {
    # The app\ files that ship, relative to -GuardRoot: what git tracks when the root is a checkout (the
    # personal-strings scan's contract - a working tree also holds hook state and caches), else every file
    # but tests, hook state and caches. Never app\tests\.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$GuardRoot)
    $skip = { param($rel) $rel -match '(^|\\)(tests|\.omc|__pycache__|\.pytest_cache)(\\|$)' -or $rel -like '*.pyc' }
    $tracked = $null
    try {
        $inside = (& git -C $GuardRoot rev-parse --is-inside-work-tree 2>$null)
        if ($LASTEXITCODE -eq 0 -and "$inside".Trim() -eq 'true') { $tracked = @(& git -C $GuardRoot ls-files -- app 2>$null | ForEach-Object { $_.Replace('/', '\') }) }
    } catch { }
    if ($null -eq $tracked) {
        $root = [IO.Path]::GetFullPath($GuardRoot).TrimEnd('\')
        $tracked = @(Get-ChildItem -LiteralPath (Join-Path $root 'app') -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($root.Length + 1) })
    }
    return @($tracked | Where-Object { -not (& $skip $_) })
}

function New-GuardStage {
    # Lay out the release in -Dest exactly as the zip holds it: Install.cmd, README.txt, LICENSE,
    # THIRD-PARTY-NOTICES.txt, NT8Guard-AddOns.zip, the guide PDF and Quick Start PNG, app\, python\
    # (the pinned runtime + the backup tools). Returns @{ Dest; RuntimeFiles } - RuntimeFiles are the
    # python\ paths that came from the runtime zip (pinned and signature-checked, so the personal-strings
    # scan skips them).
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$GuardRoot, [Parameter(Mandatory)][string]$Dest, [Parameter(Mandatory)][string]$PythonZip,
          [Parameter(Mandatory)][string]$AddOnsZip, [Parameter(Mandatory)][string]$GuideDir, [scriptblock]$Signature)
    if (Test-Path $Dest) { [IO.Directory]::Delete($Dest, $true) }
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    foreach ($rel in Get-GuardAppFiles -GuardRoot $GuardRoot) {
        $to = Join-Path $Dest $rel
        New-Item -ItemType Directory -Path (Split-Path $to) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $GuardRoot $rel) -Destination $to
    }
    $ex = @{ Zip = $PythonZip; Dest = (Join-Path $Dest 'python'); ToolsSource = (Join-Path $GuardRoot 'python') }
    if ($Signature) { $ex.Signature = $Signature }
    Expand-GuardPython @ex | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($PythonZip)
    try { $runtime = @($z.Entries | Where-Object { $_.Name } | ForEach-Object { 'python\' + $_.FullName.Replace('/', '\') }) } finally { $z.Dispose() }
    foreach ($f in 'Install.cmd', 'LICENSE', 'THIRD-PARTY-NOTICES.txt') { Copy-Item -LiteralPath (Join-Path $GuardRoot $f) -Destination (Join-Path $Dest $f) }
    Copy-Item -LiteralPath $AddOnsZip -Destination (Join-Path $Dest 'NT8Guard-AddOns.zip')
    foreach ($f in 'NT8 Guard - Setup Guide.pdf', 'NT8 Guard - Quick Start.png') { Copy-Item -LiteralPath (Join-Path $GuideDir $f) -Destination (Join-Path $Dest $f) }
    $version = ([IO.File]::ReadAllText((Join-Path $GuardRoot 'app\VERSION'))).Trim()
    $readme = @(
        "NT8 Guard $version - keeps NinjaTrader 8 running."
        ''
        '1. Extract this whole zip (right-click it, Extract All...).'
        '2. Double-click Install.cmd in the extracted folder. Windows may ask whether to run it, because it'
        '   came from the internet: choose Run (or More info, then Run anyway).'
        '3. The setup wizard opens. NT8 Guard - Setup Guide.pdf walks through every step.'
        ''
        'Updating: extract the new zip and run its Install.cmd - settings, the saved password and the'
        'strategy backups are kept.'
        ''
        'MIT licence (LICENSE). Third-party parts: THIRD-PARTY-NOTICES.txt.'
    )
    [IO.File]::WriteAllText((Join-Path $Dest 'README.txt'), ($readme -join "`r`n") + "`r`n")
    return [pscustomobject]@{ Dest = $Dest; RuntimeFiles = $runtime }
}

function New-GuardScanTree {
    # What the personal-strings scan reads: the stage without the pinned runtime's files (third-party,
    # verified by hash and signature instead) and without the guide's PDF and PNG (rendered from sources
    # the build checks), with the add-ons zip opened so its sources are read, not its compressed bytes.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Stage, [string[]]$RuntimeFiles = @(), [Parameter(Mandatory)][string]$Dest)
    if (Test-Path $Dest) { [IO.Directory]::Delete($Dest, $true) }
    $root = [IO.Path]::GetFullPath($Stage).TrimEnd('\')
    foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        $rel = $f.FullName.Substring($root.Length + 1)
        if ($RuntimeFiles -contains $rel -or $f.Extension -in '.pdf', '.png' -or $rel -eq 'NT8Guard-AddOns.zip') { continue }
        $to = Join-Path $Dest $rel
        New-Item -ItemType Directory -Path (Split-Path $to) -Force | Out-Null
        Copy-Item -LiteralPath $f.FullName -Destination $to
    }
    $addons = Join-Path $root 'NT8Guard-AddOns.zip'
    if (Test-Path $addons) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($addons, (Join-Path $Dest 'NT8Guard-AddOns'))
    }
    return $Dest
}

function Write-GuardReleaseZip {
    # Zip the stage with FORWARD-slash entry names (the .NET Framework helpers write backslashes).
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Stage, [Parameter(Mandatory)][string]$ZipPath)
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path $ZipPath) { [IO.File]::Delete($ZipPath) }
    $dir = Split-Path $ZipPath; if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $root = [IO.Path]::GetFullPath($Stage).TrimEnd('\')
    $zip = [IO.Compression.ZipFile]::Open($ZipPath, 'Create')
    try {
        foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName) {
            $name = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $f.LastWriteTime
            $out = $entry.Open()
            try { $in = [IO.File]::OpenRead($f.FullName); try { $in.CopyTo($out) } finally { $in.Dispose() } } finally { $out.Dispose() }
        }
    } finally { $zip.Dispose() }
    return $ZipPath
}

function Test-GuardReleaseZip {
    # @{ Ok; Problems }: every entry uses '/', and the pieces a person needs are all there.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ZipPath,
          [string[]]$Require = @('Install.cmd', 'README.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'NT8Guard-AddOns.zip', 'NT8 Guard - Setup Guide.pdf',
                                 'NT8 Guard - Quick Start.png', 'app/VERSION', 'app/Install-Guard.ps1', 'app/Guard-Panel.ps1', 'app/Guard-Run.vbs', 'python/python.exe'))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try { $names = @($z.Entries | ForEach-Object FullName) } finally { $z.Dispose() }
    $problems = New-Object System.Collections.Generic.List[string]
    $back = @($names | Where-Object { $_.Contains('\') })
    if ($back.Count) { $problems.Add("$($back.Count) entr$(if ($back.Count -eq 1) { 'y uses' } else { 'ies use' }) a backslash (NinjaTrader-style importers and some unzip tools break on it): $(@($back | Select-Object -First 3) -join ', ')") }
    $missing = @($Require | Where-Object { $names -notcontains $_ })
    if ($missing.Count) { $problems.Add("missing: $($missing -join ', ')") }
    return [pscustomobject]@{ Ok = (-not $problems.Count); Problems = $problems.ToArray() }
}

function Test-GuardInstalledTree {
    # Text files under -Root that name any -Forbidden string (the build machine's repository, its user
    # profile): an installed program must work from where it was installed, nowhere else.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string[]]$Forbidden)
    $root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object { $_.Extension -in '.ps1', '.psm1', '.xaml', '.vbs', '.cmd', '.json', '.txt', '.py', '.md', '.ini', '.cfg', '._pth' }) {
        $text = [IO.File]::ReadAllText($f.FullName)
        foreach ($s in $Forbidden) { if ($s -and $text.IndexOf($s, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $out.Add("$($f.FullName.Substring($root.Length + 1)): names $s") } }
    }
    return $out.ToArray()
}

function Invoke-GuardFreshInstall {
    # The release's last gate: install the finished zip the way a person does - extracted into an empty
    # folder, Install.cmd run with no Python on PATH - into a throwaway Guard home and Start menu, then
    # prove from the INSTALLED copy that the wizard sees the bundled Python, the bundled Python runs the
    # backup tools, the panel renders, and nothing names the build machine. @{ Ok; Problems; Log }.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$ZipPath, [string[]]$Forbidden = @())
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $work = Join-Path ([IO.Path]::GetTempPath()) ('nt8guard-fresh-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $x = Join-Path $work 'extracted'; $guardHome = Join-Path $work 'home'; $menu = Join-Path $work 'menu'; $shots = Join-Path $work 'shots'
    $problems = New-Object System.Collections.Generic.List[string]
    $log = New-Object System.Collections.Generic.List[string]
    $savedPath = $env:PATH; $savedHome = $env:NT8GUARD_HOME
    try {
        [IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $x)
        # A clean machine: Windows and PowerShell on PATH, no Python, no repository.
        $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot;$env:SystemRoot\System32\WindowsPowerShell\v1.0"
        $env:NT8GUARD_HOME = $guardHome
        $cmd = "`"`"$x\Install.cmd`" -GuardHome `"$guardHome`" -StartMenuDir `"$menu`" -NoLaunch < nul`""
        $out = @(& cmd.exe /s /c $cmd 2>&1 | ForEach-Object { "$_" }); $code = $LASTEXITCODE
        $log.AddRange([string[]]$out)
        if ($code -ne 0) { $problems.Add("Install.cmd exited $code") }
        foreach ($p in 'app\Guard-Panel.ps1', 'python\python.exe', 'NT8Guard-AddOns.zip', 'NT8 Guard - Setup Guide.pdf') {
            if (-not (Test-Path (Join-Path $guardHome $p))) { $problems.Add("not installed: $p") }
        }
        if (-not (Test-Path (Join-Path $menu 'NT8 Guard.lnk'))) { $problems.Add('no Start-menu shortcut') }
        if (-not $problems.Count) {
            $q = "Import-Module '$guardHome\app\lib\Guard.Setup.psm1'; " +
                 "(Get-GuardSetupCheck -Nt8Version ([version]'8.1.8.3') -AutoLogon { `$true } -ScalePercent { 100 } -OtherWatchdogs { @() } | Where-Object Key -eq python | ForEach-Object { `$_.State + ' ' + `$_.Text })"
            $py = (@(& powershell.exe -NoProfile -ExecutionPolicy Bypass -Command $q 2>&1) -join ' ').Trim()
            $log.Add("wizard python row: $py")
            if ($py -notmatch '^ok ' -or $py -notmatch [regex]::Escape("$guardHome\python\python.exe")) { $problems.Add("the installed wizard does not find the bundled Python: $py") }
            Push-Location $work
            try { $o = @(& "$guardHome\python\python.exe" "$guardHome\python\backup_export.py" --help 2>&1); $c = $LASTEXITCODE } finally { Pop-Location }
            $log.Add("backup_export --help: exit $c")
            if ($c -ne 0) { $problems.Add("the bundled Python cannot run backup_export.py: $(@($o) -join ' ')") }
            $o = @(& powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "$guardHome\app\Guard-Panel.ps1" -ScreenshotDir $shots 2>&1); $c = $LASTEXITCODE
            $n = @(Get-ChildItem $shots -Filter *.png -ErrorAction SilentlyContinue).Count
            $log.Add("installed panel: exit $c, $n screenshots")
            if ($c -ne 0 -or $n -lt 6) { $problems.Add("the installed panel did not render: exit $c, $n screenshots; $(@($o) -join ' ')") }
            foreach ($l in Test-GuardInstalledTree -Root $guardHome -Forbidden $Forbidden) { $problems.Add("installed file $l") }
        }
    } catch { $problems.Add("fresh install failed: $($_.Exception.Message)") }
    finally {
        $env:PATH = $savedPath; $env:NT8GUARD_HOME = $savedHome
        for ($i = 0; $i -lt 10 -and (Test-Path $work); $i++) { try { [IO.Directory]::Delete($work, $true) } catch { Start-Sleep -Milliseconds 500 } }
    }
    return [pscustomobject]@{ Ok = (-not $problems.Count); Problems = $problems.ToArray(); Log = $log.ToArray() }
}

Export-ModuleMember -Function Get-GuardPythonRuntime, Expand-GuardPython, Copy-GuardTree, Get-GuardAppFiles, New-GuardStage, New-GuardScanTree,
                              Write-GuardReleaseZip, Test-GuardReleaseZip, Test-GuardInstalledTree, Invoke-GuardFreshInstall
