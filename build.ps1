$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw   = "C:\Windows\Microsoft.NET\Framework\v4.0.30319"
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (!(Test-Path -LiteralPath $csc)) { $csc = Join-Path $fw "csc.exe" }

$src = Join-Path $here "Ss4Narrator.cs"
$out = Join-Path $here "Ss4Narrator.exe"

& $csc /nologo /noconfig /target:exe /platform:x64 /optimize+ /out:"$out" `
    /reference:"$fw\System.dll" `
    /reference:"$fw\System.Core.dll" `
    /reference:"$fw\System.Windows.Forms.dll" `
    /reference:"$fw\System.Drawing.dll" `
    "$src"

if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }

# Prism ships its native DLLs in the SDK subtree; flatten them next to the exe.
$prismBin = Join-Path $here "prism\prism-sdk-v0.18.3\windows\x64\dynamic\release\bin"
if (Test-Path -LiteralPath $prismBin) {
    Copy-Item (Join-Path $prismBin "*.*") -Destination $here -Force
}

Write-Host "built : $out (x64, Prism speech backend)"
