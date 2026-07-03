$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $root 'dist'
$src = Join-Path $root 'src\TaskBarHeroGuard\Program.cs'
$exe = Join-Path $outDir 'TaskBarHeroGuard.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

& $csc `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:x64 `
    /out:$exe `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    $src

Write-Host "Built $exe"
