# Launches the Pluto MCP server; this is the command an MCP client runs (.mcp.json, .vscode\mcp.json).
# Rebuilds first when bin\PlutoMcp.exe is missing, a source is newer than it, or the newest SAP2000 install's
# SAP2000v1.dll differs from the one it was built against (a SAP upgrade). Build output goes to the log,
# never stdout: stdout belongs to the protocol. Then runs the exe on this process's stdin / stdout.
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\mcp\Start-PlutoMcp.ps1
$ErrorActionPreference = "Stop"
$bin = Join-Path $PSScriptRoot "bin"
$exe = Join-Path $bin "PlutoMcp.exe"
$work = $env:PLUTO_WORK
if (-not $work) { $work = Join-Path $env:LOCALAPPDATA "Pluto\work" }
$null = New-Item -ItemType Directory -Force $work
$log = Join-Path $work "pluto-mcp-build.log"

function Why-Rebuild {
    if (-not (Test-Path $exe)) { return "no exe" }
    $built = (Get-Item $exe).LastWriteTimeUtc
    $src = @(Get-ChildItem $PSScriptRoot -Filter *.cs) + @(Get-ChildItem (Join-Path $PSScriptRoot "..\sap") -Filter "Sap*.cs")
    $newer = $src | Where-Object { $_.LastWriteTimeUtc -gt $built } | Select-Object -First 1
    if ($newer) { return "source changed: $($newer.Name)" }
    $infoPath = Join-Path $bin "build.json"
    if (-not (Test-Path $infoPath)) { return "no build.json" }
    $info = Get-Content $infoPath -Raw | ConvertFrom-Json
    $sapDir = $env:PLUTO_SAP_DIR
    if (-not $sapDir) {
        $sapDir = Get-ChildItem "C:\Program Files\Computers and Structures" -Directory -Filter "SAP2000 *" -ErrorAction SilentlyContinue |
            Sort-Object { [int]($_.Name -replace '\D', '') } | Select-Object -Last 1 -ExpandProperty FullName
    }
    $dll = if ($sapDir) { Join-Path $sapDir "SAP2000v1.dll" } else { $null }
    if ($dll -and (Test-Path $dll) -and (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant() -ne $info.sapDllSha256) {
        return "SAP2000 changed: $sapDir"
    }
    return $null
}

$why = Why-Rebuild
if ($why) {
    Add-Content -Encoding utf8 $log ("{0} rebuild ({1})" -f (Get-Date -Format s), $why)
    try { & (Join-Path $PSScriptRoot "Build-PlutoMcp.ps1") *>&1 | Out-File -Append -Encoding utf8 $log }
    catch { Add-Content -Encoding utf8 $log ("{0} BUILD FAILED: {1}" -f (Get-Date -Format s), $_); [Console]::Error.WriteLine("Pluto MCP build failed; see $log"); exit 1 }
}
& $exe
exit $LASTEXITCODE
