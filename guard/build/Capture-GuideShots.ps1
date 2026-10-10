# Capture-GuideShots.ps1 - the setup guide's screenshots, taken from the real control panel (spec 8) against
# a generic demo state (New-GuardDemoState.ps1), never this PC's own Guard or NinjaTrader:
#
#   powershell -STA -ExecutionPolicy Bypass -File guard\build\Capture-GuideShots.ps1 -OutDir <dir>
#
# Writes <OutDir>\<Tab>.png for the six tabs and Wizard-1..7.png for the setup wizard's steps, and prints
# one line per Status card ("card <key> <state>"). The demo lives in a temp folder, so every path the panel
# shows is swapped for the one a user would see (C:\Users\someone\...) before each shot; a shot that still
# shows this PC's user name, its temp folder or the repository is refused (exit 1), so a guide can never
# carry them.
[CmdletBinding()] param([Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with powershell -STA (WPF).' }
$app = Join-Path (Split-Path -Parent $PSScriptRoot) 'app'
$repoGuard = Split-Path -Parent $app
foreach ($m in 'Guard.Paths', 'Guard.Log', 'Guard.Config', 'Guard.Events') { Import-Module (Join-Path $app "lib\$m.psm1") -DisableNameChecking }
. (Join-Path $PSScriptRoot 'New-GuardDemoState.ps1')

function Get-VisualText($Element, $List) {
    if ($Element -is [Windows.Controls.TextBlock] -or $Element -is [Windows.Controls.TextBox]) { $List.Add($Element) }
    $n = 0; try { $n = [Windows.Media.VisualTreeHelper]::GetChildrenCount($Element) } catch { }
    for ($i = 0; $i -lt $n; $i++) { Get-VisualText ([Windows.Media.VisualTreeHelper]::GetChild($Element, $i)) $List }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('nt8guard-demo-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$leaks = New-Object System.Collections.Generic.List[string]
try {
    $demo = New-GuardDemoState -Root $root
    . (Join-Path $app 'Guard-Panel.ps1') -ImportOnly -GuardHome $demo.Home -Nt8Dir $demo.Nt8
    $GuardHome = $demo.Home; $Nt8Dir = $demo.Nt8
    $script:SetupCheckArgs = $demo.SetupCheckArgs
    $script:AddonArgs = $demo.AddonArgs
    # What a user's own machine shows, longest first so a folder inside another is swapped whole.
    $userHome = 'C:\Users\someone\AppData\Local\NT8Guard'
    $swap = [ordered]@{}
    $swap[(Join-Path $repoGuard 'build\out\NT8Guard-AddOns.zip')] = "$userHome\NT8Guard-AddOns.zip"
    $swap[$demo.Nt8] = 'C:\Users\someone\Documents\NinjaTrader 8'
    $swap[$demo.Home] = $userHome
    $deny = @(@("\b$([regex]::Escape($env:USERNAME))\b") + @([IO.Path]::GetTempPath().TrimEnd('\'), (Split-Path -Parent $repoGuard) | ForEach-Object { [regex]::Escape($_) }))

    $shoot = {
        param([string]$Tab, [string]$File)
        Invoke-PanelOffscreen -Tabs @($Tab) -Action {
            param($t, $holder)
            $texts = New-Object System.Collections.Generic.List[object]
            Get-VisualText $holder $texts
            foreach ($e in $texts) {
                $s = [string]$e.Text
                foreach ($k in $swap.Keys) { if ($s.IndexOf($k, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $s = $s.Replace($k, $swap[$k]) } }
                if ($s -ne [string]$e.Text) { $e.Text = $s }
                foreach ($d in $deny) { if ($s -match $d) { $leaks.Add("$([IO.Path]::GetFileName($File)): '$s'") } }
            }
            $holder.UpdateLayout()
            $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap([int]$script:Win.Width, [int]$script:Win.Height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
            $rtb.Render($holder)
            $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
            $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
            $fs = [IO.File]::Create($File)
            try { $enc.Save($fs) } finally { $fs.Dispose() }
        }
        "saved $File"
    }

    Initialize-Panel -StatusArgs $demo.StatusArgs | Out-Null
    foreach ($c in @($script:LastCards)) { "card $($c.Key) $($c.State)" }
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
    Get-ChildItem $OutDir -Filter '*.png' | ForEach-Object { [IO.File]::Delete($_.FullName) }
    foreach ($t in 'Status', 'Protection', 'Alerts', 'Backups', 'Watch', 'Tools') { & $shoot $t (Join-Path $OutDir "$t.png") }
    # The setup wizard as a person sees it: step bar, the step's page, Back / Next.
    for ($i = 1; $i -le 7; $i++) {
        Enter-PanelWizard $i
        & $shoot $script:WizardSteps[$i - 1].Tab (Join-Path $OutDir "Wizard-$i.png")
    }
    Exit-PanelWizard
} finally {
    if (Test-Path $root) { try { [IO.Directory]::Delete($root, $true) } catch { } }
}
if ($leaks.Count) {
    $leaks | ForEach-Object { "LEAK $_" }
    Get-ChildItem $OutDir -Filter '*.png' | ForEach-Object { [IO.File]::Delete($_.FullName) }
    exit 1
}
exit 0
