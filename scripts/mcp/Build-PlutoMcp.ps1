# Builds PlutoMcp.exe, the Pluto MCP server (plan: vault/decisions/2026-10-12-llm-model-interface).
# No Visual Studio, no .NET SDK: the .NET Framework 4.x csc.exe that ships with Windows (C# 5), warnings as
# errors, against SAP2000v1.dll of the newest install (same lookup as scripts\sap\Import-SapApi.ps1) and
# the SAP controller sources scripts\sap\Sap*.cs. Writes bin\PlutoMcp.exe + bin\build.json (which
# SAP2000v1.dll it was built against: Start-PlutoMcp.ps1 rebuilds when that changes, e.g. SAP 27).
#   powershell -ExecutionPolicy Bypass -File Build-PlutoMcp.ps1 [-SapDir <install folder>]
param([string]$SapDir)
$ErrorActionPreference = "Stop"
if (-not $SapDir) { $SapDir = $env:PLUTO_SAP_DIR }
if (-not $SapDir) {
    $SapDir = Get-ChildItem "C:\Program Files\Computers and Structures" -Directory -Filter "SAP2000 *" -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.Name -replace '\D', '') } | Select-Object -Last 1 -ExpandProperty FullName
}
$sapDll = Join-Path $SapDir "SAP2000v1.dll"
if (-not $SapDir -or -not (Test-Path $sapDll)) { throw "No SAP2000 install found (SapDir '$SapDir'); pass -SapDir or set PLUTO_SAP_DIR." }

$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw "csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
$bin = Join-Path $PSScriptRoot "bin"
$null = New-Item -ItemType Directory -Force $bin
$exe = Join-Path $bin "PlutoMcp.exe"

$src = @(Get-ChildItem $PSScriptRoot -Filter *.cs | ForEach-Object FullName) +
       @(Get-ChildItem (Join-Path $PSScriptRoot "..\sap") -Filter "Sap*.cs" | ForEach-Object FullName)
# netstandard: SAP 26's dll targets it (the Framework ships the facade beside csc.exe)
$refs = @("/reference:$sapDll", "/reference:$(Join-Path $fw 'netstandard.dll')",
          "/reference:System.dll", "/reference:System.Core.dll", "/reference:System.Web.Extensions.dll", "/reference:Microsoft.CSharp.dll")
# A client still running the old exe (Claude Desktop, a second Claude Code) locks it against writing but not
# against renaming: move it aside, and delete the moved copies no process holds any more.
Get-ChildItem $bin -Filter "PlutoMcp.*.old" | ForEach-Object { try { Remove-Item $_.FullName -Force -ErrorAction Stop } catch { } }
if (Test-Path $exe) {
    try { Remove-Item $exe -Force -ErrorAction Stop }
    catch { Move-Item $exe (Join-Path $bin ("PlutoMcp.{0}.old" -f (Get-Date -Format "yyyyMMddHHmmssfff"))) }
}
Write-Output ("[build] SAP2000v1.dll from {0} (file {1})" -f $SapDir, (Get-Item $sapDll).VersionInfo.FileVersion)
& $csc /nologo /noconfig /target:exe /platform:x64 /optimize+ /warnaserror+ "/out:$exe" $refs $src
if ($LASTEXITCODE -ne 0) { throw "compile failed" }

$info = [ordered]@{
    sapDir       = $SapDir
    sapDll       = $sapDll
    sapDllSha256 = (Get-FileHash $sapDll -Algorithm SHA256).Hash.ToLowerInvariant()
    builtUtc     = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}
[IO.File]::WriteAllText((Join-Path $bin "build.json"), (ConvertTo-Json $info), (New-Object Text.UTF8Encoding $false))
Write-Output "[build] $exe"
