# Shared by the feed guard add-on tests in this folder (Test-FeedGuard*.ps1). Dot-source it:
#   . (Join-Path $PSScriptRoot 'FeedGuardTestKit.ps1')
#
# The tests cut REAL code out of addon\AutoReconnect.cs and compile it with the .NET Framework C#
# compiler. That compiler speaks C# 5, the oldest language NinjaTrader's C# 9 accepts, so whatever
# compiles here compiles inside NinjaTrader - and a test can never pass against a stale copy of the
# logic, because there is no copy.
$ErrorActionPreference = 'Stop'
$script:AddOnPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\addon\AutoReconnect.cs'))
$script:AddOnSource = [IO.File]::ReadAllText($script:AddOnPath)
$script:Csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$script:CSharpExit = 1

function Get-AddOnSlice([string]$Start, [string]$End) {
    # The source text from the first $Start up to (not including) the next $End.
    $i = $script:AddOnSource.IndexOf($Start)
    if ($i -lt 0) { throw "not found in AutoReconnect.cs: $Start" }
    $j = $script:AddOnSource.IndexOf($End, $i)
    if ($j -lt 0) { throw "not found in AutoReconnect.cs after '$Start': $End" }
    return $script:AddOnSource.Substring($i, $j - $i)
}

function Get-AddOnBlock([string]$Name) {
    # A block kept between "// ===== BEGIN <Name> =====" and "// ===== END <Name> =====".
    return Get-AddOnSlice "// ===== BEGIN $Name =====" "// ===== END $Name ====="
}

function Get-NinjaTraderBin {
    # NinjaTrader's bin folder in the standard Program Files locations (its uninstall entry records no
    # install location), or $null when NinjaTrader is not installed - the caller then skips.
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }) {
        $bin = Join-Path $root 'NinjaTrader 8\bin'
        if (Test-Path (Join-Path $bin 'NinjaTrader.Core.dll')) { return $bin }
    }
    return $null
}

function Invoke-CSharpTest([string]$Name, [string]$Code, [string[]]$References = @(), [string[]]$Arguments = @()) {
    # Compile $Code into a console program, run it with $Arguments, and leave its exit code in
    # $script:CSharpExit. The program prints its own PASS/FAIL lines and a final "RESULT:" line.
    if (-not (Test-Path $script:Csc)) { throw "csc.exe not found at $script:Csc" }
    $dir = Join-Path $env:TEMP ("fgtest_{0}_{1}" -f $Name, [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    try {
        $cs = Join-Path $dir "$Name.cs"
        $exe = Join-Path $dir "$Name.exe"
        # with a BOM: csc reads a BOM-less file in the ANSI code page and garbles non-ASCII literals
        [IO.File]::WriteAllText($cs, $Code, (New-Object Text.UTF8Encoding($true)))
        $refArgs = @($References | ForEach-Object { "/r:$_" })
        $out = & $script:Csc /nologo /nowarn:1701,1702 "/out:$exe" @refArgs $cs 2>&1
        if (-not (Test-Path $exe)) {
            $out | ForEach-Object { "  $_" }
            'RESULT: the test program did not compile'
            $script:CSharpExit = 1
            return
        }
        & $exe @Arguments
        $script:CSharpExit = $LASTEXITCODE
    } finally {
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# The few lines every C# test program needs: Check(label, ok, detail) and the RESULT line.
$script:CSharpHarness = @'
    static int fail = 0;
    static void Check(string label, bool ok, string detail) {
        if (ok) Console.WriteLine("  PASS  " + label);
        else { fail++; Console.WriteLine("  FAIL  " + label + " -> " + detail); }
    }
    static int Done() {
        Console.WriteLine(fail == 0 ? "RESULT: all checks passed" : ("RESULT: " + fail + " FAILED"));
        return fail == 0 ? 0 : 1;
    }
'@
