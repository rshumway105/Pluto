# The SAP2000 runner: everything outside the duct / survey workflows that drives SAP goes through here,
# including every Python tool (pythonTools\sap\sap_cli.py calls this script; Python never touches the OAPI).
# Windows PowerShell 5.1. Steps run in this order, each only when its switch is given:
#
#   attach   the running SAP2000, or start one (-NewInstance: always a second SAP, closed at the end;
#            -AttachPid <pid>: that SAP2000 process, e.g. the MCP server's own instance
#            unless -KeepOpen; a SAP this script started is closed at the end unless -KeepOpen)
#   open     -Open <.s2k|.sdb>: OpenFile; an imported .s2k is saved beside it as .sdb (-NoSaveSdb: not)
#   axes     -AreaAxes <tsv>: local 1 of every area object, global direction cosines
#   run      -Run: RunAnalysis on the file SAP holds (it saves it). -RunOnCopy <sdb>: save AS that copy
#            first and run every case there (the opened file is never written; "auto" = <export base>.sdb).
#            -RunIfNeeded: only when a case has no results (alone: on the held file; with -RunOnCopy: on the copy)
#   tables   -Tables "<name>;<name>" -TablesOut <s2k>: input tables read back (display precision), to echo
#            what an import produced
#   describe -Describe <json>: SapRead.Describe, the model overview the MCP server's describe_model gives
#            (units, counts, extent, supports, properties with use counts, groups, patterns, cases, combos)
#   results  -Results <s2k>: result tables at full precision, objects under their SAP names
#   export   -Export <base> (or an existing folder: <folder>\<model name>): analysis mesh -> <base>.model.s2k + <base>.labels.csv, results ->
#            <base>.results.s2k (-NoResults: geometry only; -FinalOnly: staged cases' last step only),
#            then SapToPluto -> <base>.bin + <base>.features.json (-NoViewerFile: stop at the .s2k;
#            -Cylindrical: add Translation R / T)
#   test     -SelfTest <dir>: 20 ft beam, 10 kip at midspan -> V 5 kip, M 50 kip-ft
#
# Every line of progress starts with "[step]"; the last line is "RESULT {json}" (machine-readable).
#   powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\sap\Run-Sap.ps1 -Open X.s2k -Run -Results results.s2k
param(
    [string]$Open,
    [switch]$NoSaveSdb,
    [switch]$NewInstance,
    [int]$AttachPid,
    [switch]$KeepOpen,
    [string]$AreaAxes,
    [switch]$Run,
    [string]$RunOnCopy,
    [switch]$RunIfNeeded,
    [string]$Tables,
    [string]$TablesOut,
    [string]$Describe,
    [string]$Results,
    [string]$Export,
    [switch]$NoResults,
    [switch]$FinalOnly,
    [switch]$NoViewerFile,
    [switch]$Cylindrical,
    [string]$ModelId,
    [string]$SelfTest,
    [string]$SapDir
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Import-SapApi.ps1")
# The viewer lib (SapToPluto) compiles before SAP2000 is touched: once SAP's cHelper has run (-AttachPid, -NewInstance)
# its .NET Standard assemblies are loaded and the lib's Add-Type cannot load System.Console (2026-10-12).
if ($Export -and -not $NoViewerFile) { . (Join-Path (Split-Path $PSScriptRoot) "lib\Config.ps1") }
$out = [ordered]@{}
function Say([string]$step, [string]$text) { Write-Output ("[{0}] {1}" -f $step.PadRight(7), $text) }
function Full([string]$p) { [IO.Path]::GetFullPath($p) }

if ($AttachPid) { $sap = [SapSession]::AttachProcess($AttachPid) }
elseif ($NewInstance) { $sap = [SapSession]::NewInstance($SapExe) } else { $sap = [SapSession]::AttachOrStart($SapExe, $false) }
$out.version = $sap.Version()
$out.started = $sap.Started
Say "sap" ("SAP2000 {0} ({1})" -f $out.version, $(if ($sap.Started) { "started $SapExe" } else { "attached to the running instance" }))
try {
    if ($SelfTest) {
        $null = New-Item -ItemType Directory -Force $SelfTest
        $s2k = Join-Path (Full $SelfTest) "beam.s2k"
        [SapSession]::WriteTestBeamS2k($s2k, $out.version, 20.0, 10.0)
        $Open = $s2k; $Run = $true; $Results = Join-Path (Full $SelfTest) "results.s2k"
    }
    if ($Open) {
        $out.held = $sap.Open((Full $Open), -not $NoSaveSdb)
        $c = $sap.Counts()
        $out.counts = @{ joints = $c[0]; areas = $c[1]; frames = $c[2]; links = $c[3] }
        $out.groups = @($sap.Groups())
        Say "open" ("{0}  ({1} joints, {2} areas, {3} frames, {4} links; groups {5})  ({6:0}s)" -f $out.held, $c[0], $c[1], $c[2], $c[3], ($out.groups -join ", "), $sap.OpenSeconds)
    }
    $held = $sap.ModelPath()
    if (-not $held) { throw "SAP holds no model: pass -Open <file>." }
    $out.units = $sap.Units()
    $base = $null
    if ($Export) {
        $base = Full $Export
        if (Test-Path $base -PathType Container) { $base = Join-Path $base ([IO.Path]::GetFileNameWithoutExtension($held) -replace ' ', '_') }
        $null = New-Item -ItemType Directory -Force (Split-Path $base)
    }
    if ($RunOnCopy -eq "auto") {
        if (-not $base) { throw "-RunOnCopy auto needs -Export." }
        $RunOnCopy = "$base.sdb"
    }
    if ($AreaAxes) {
        $n = $sap.WriteAreaLocal1((Full $AreaAxes))
        Say "axes" "$n areas -> $AreaAxes"
    }
    if ($RunIfNeeded) { $needRun = -not $sap.AllCasesFinished() } else { $needRun = $Run -or [bool]$RunOnCopy }
    if ($needRun) {
        if ($RunOnCopy) { Say "run" $sap.RunOnCopy((Full $RunOnCopy)) }
        else { $secs = $sap.Run(); Say "run" ("cases {0}  ({1:0.0}s)" -f ($sap.LoadCases() -join ", "), $secs) }
    }
    $out.cases = @($sap.LoadCases())
    if ($Tables) {
        if (-not $TablesOut) { throw "-Tables needs -TablesOut <s2k>." }
        $counts = $sap.WriteTablesS2k((Full $TablesOut), ($Tables -split ";"))
        Say "tables" ("{0}  ({1})" -f $TablesOut, (($counts.Keys | ForEach-Object { "$_ $($counts[$_])" }) -join ", "))
    }
    if ($Describe) {
        $d = (New-Object SapRead $sap).Describe(0)
        [IO.File]::WriteAllText((Full $Describe), (ConvertTo-Json $d -Depth 8), (New-Object Text.UTF8Encoding $false))
        Say "describe" ("{0}  ({1:0.0}s index)" -f $Describe, $d["indexSeconds"])
        $out.describe = Full $Describe
    }
    if ($Results) {
        $counts = $sap.WriteResultsS2k((Full $Results), $null, [bool]$FinalOnly, "Run-Sap.ps1")
        Say "results" ("{0}  ({1})" -f $Results, (($counts.Keys | ForEach-Object { "$_ $($counts[$_])" }) -join ", "))
        $out.results = Full $Results
    }
    if ($Export) {
        $mesh = [SapExport]::BuildMesh($sap)
        Say "mesh" $mesh.Summary()
        $modelS2k = "$base.model.s2k"
        [IO.File]::WriteAllText($modelS2k, [SapExport]::ModelS2k($sap, $mesh))
        [IO.File]::WriteAllText("$base.labels.csv", [SapExport]::LabelsCsv($mesh))
        $resS2k = $null
        if (-not $NoResults) {
            $resS2k = "$base.results.s2k"
            $counts = $sap.WriteResultsS2k($resS2k, $mesh, [bool]$FinalOnly, "Run-Sap.ps1 -Export")
            Say "results" ((($counts.Keys | ForEach-Object { "$_ $($counts[$_])" }) -join ", ") + ("  ({0} MB)" -f [int]((Get-Item $resS2k).Length / 1MB)))
        }
        $out.model = $modelS2k; $out.resultsS2k = $resS2k
        if (-not $NoViewerFile) {
            if (-not $ModelId) { $ModelId = "sap/" + ([IO.Path]::GetFileNameWithoutExtension($held) -replace ' ', '_') }
            $r = [SapToPluto]::Export($modelS2k, $resS2k, $base, $ModelId, [bool]$Cylindrical)
            foreach ($line in ($r.Summary() -split "`r?`n")) { if ($line.Trim()) { Say "export" $line } }
            $out.bin = "$base.bin"; $out.features = "$base.features.json"
        }
    }
    if ($SelfTest) {
        $v = 0.0; $m = 0.0
        foreach ($l in (Get-Content $Results)) {
            if ($l -match "^\s+Frame=\S+ .*\sV2=(\S+)\s.*\sM3=(\S+)") {
                $v = [Math]::Max($v, [Math]::Abs([double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)))
                $m = [Math]::Max($m, [Math]::Abs([double]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture)))
            }
        }
        $out.selfTest = ([Math]::Abs($v - 5) -lt 1e-6) -and ([Math]::Abs($m - 50) -lt 1e-6)
        Say "test" ("max |V2| {0} kip, max |M3| {1} kip-ft (expected 5, 50): {2}" -f $v, $m, $(if ($out.selfTest) { "OK" } else { "FAILED" }))
        if (-not $out.selfTest) { throw "self test failed" }
    }
    foreach ($w in $sap.Warnings) { Say "warn" $w }
} finally {
    if (-not $KeepOpen) { $sap.Close() }
}
Write-Output ("RESULT " + (ConvertTo-Json $out -Compress -Depth 4))
