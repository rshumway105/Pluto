# Protocol test for the Pluto MCP server, no LLM involved: starts the server the way an MCP client does
# (through Start-PlutoMcp.ps1, or the exe with -Direct), sends initialize / initialized / tools/list /
# tools/call status [/ self_test] [/ the read tools on -Model], checks every reply, prints them.
# Exit 0 = all checks passed. -Model opens that file in the running SAP2000 (replacing what it holds) as a
# working copy and runs every phase 1 tool on it; -Run adds the phase 2 tools (two runs, results, reactions,
# compare, viewer export), and with -SelfTest also opens the self-test beam and checks its hand values through
# them. -Edit (with -SelfTest) edits the beam through the phase 3 tools, runs it and checks hand values, checkpoints,
# revert and the change log. -Viewer (with -Model -Run) drives the live viewer in a headless Edge through the viewer
# tools. -Tables (with -SelfTest) exports beam tables to Excel, edits the workbook in Excel and imports it back.
# -Isolated: the server starts its own SAP2000 (PLUTO_SAP_INSTANCE=new) and closes it, so the user's is not touched.
# Replies are printed cut to -Show characters.
#   powershell -ExecutionPolicy Bypass -File Test-PlutoMcp.ps1 [-Direct] [-Isolated] [-SelfTest [-Edit] [-Tables]] [-Model <sdb|s2k> [-Run [-Viewer]]] [-Show 600]
param([switch]$Direct, [switch]$Isolated, [switch]$SelfTest, [string]$Model, [switch]$Run, [switch]$Edit, [switch]$Viewer, [switch]$Tables, [int]$Show = 600)
$ErrorActionPreference = "Stop"
$psi = New-Object Diagnostics.ProcessStartInfo
if ($Direct) {
    $psi.FileName = Join-Path $PSScriptRoot "bin\PlutoMcp.exe"
} else {
    $psi.FileName = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
    $psi.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $PSScriptRoot "Start-PlutoMcp.ps1") + '"'
}
$psi.UseShellExecute = $false
# inherited, not through $psi.EnvironmentVariables: .NET Framework lowercases every variable name it passes on that way,
# and the server's own children (Run-Sap.ps1) then fail in Add-Type
if ($Isolated) { $env:PLUTO_SAP_INSTANCE = "new" }
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = New-Object Text.UTF8Encoding $false
$p = [Diagnostics.Process]::Start($psi)
if ($Isolated) { Remove-Item Env:PLUTO_SAP_INSTANCE }
$stdin = New-Object IO.StreamWriter($p.StandardInput.BaseStream, (New-Object Text.UTF8Encoding $false))
$stdin.NewLine = "`n"
$errTask = $p.StandardError.ReadToEndAsync()
$failed = 0

function Send($obj) { $stdin.WriteLine((ConvertTo-Json $obj -Compress -Depth 10)); $stdin.Flush() }
function Receive([int]$timeoutSec) {
    $t = $p.StandardOutput.ReadLineAsync()
    if (-not $t.Wait($timeoutSec * 1000)) { throw "no reply within $timeoutSec s" }
    if ($null -eq $t.Result) { throw "server closed stdout" }
    return $t.Result
}
function Check([string]$what, [bool]$ok) {
    Write-Output ("[{0}] {1}" -f $(if ($ok) { " ok " } else { "FAIL" }), $what)
    if (-not $ok) { $script:failed++ }
}
function Call([int]$id, [string]$method, $params, [int]$timeoutSec) {
    Send @{ jsonrpc = "2.0"; id = $id; method = $method; params = $params }
    $line = Receive $timeoutSec
    $r = $line | ConvertFrom-Json
    Check "$method reply is JSON-RPC with id $id" ($r.jsonrpc -eq "2.0" -and $r.id -eq $id) | Out-Host
    return $r
}

try {
    $r = Call 1 "initialize" @{ protocolVersion = "2025-06-18"; capabilities = @{}; clientInfo = @{ name = "Test-PlutoMcp"; version = "1" } } 120
    Check "initialize: server pluto, tools capability" ($r.result.serverInfo.name -eq "pluto" -and $null -ne $r.result.capabilities.tools)
    Send @{ jsonrpc = "2.0"; method = "notifications/initialized" }

    $r = Call 2 "tools/list" @{} 30
    $names = @($r.result.tools | ForEach-Object name)
    Check ("tools/list: " + ($names -join ", ")) (($names -contains "status") -and ($names -contains "self_test"))

    $r = Call 3 "tools/call" @{ name = "status"; arguments = @{} } 90
    Check "status: not an error" (-not $r.result.isError)
    $text = $r.result.content[0].text
    Write-Output "        $text"
    $status = $text | ConvertFrom-Json
    $sap = $status.programs[0]
    Check ("status: SAP2000 attached " + $sap.version) ($sap.attached -eq $true)

    $r = Call 4 "tools/call" @{ name = "no_such_tool"; arguments = @{} } 30
    Check "unknown tool: JSON-RPC error" ($null -ne $r.error)

    $r = Call 5 "nonexistent/method" @{} 30
    Check "unknown method: -32601" ($r.error.code -eq -32601)

    $beam = $null
    if ($SelfTest) {
        $r = Call 6 "tools/call" @{ name = "self_test"; arguments = @{} } 900
        $text = $r.result.content[0].text
        Write-Output "        $text"
        Check "self_test: passed" ((-not $r.result.isError) -and ($text | ConvertFrom-Json).passed -eq $true)
        if (-not $r.result.isError) { $beam = ($text | ConvertFrom-Json).model }
    }

    $script:id = 10
        function Tool([string]$name, $arguments, [int]$timeoutSec = 600, [switch]$ExpectError) {
            $script:id++
            $r = Call $script:id "tools/call" @{ name = $name; arguments = $arguments } $timeoutSec
            $text = $r.result.content[0].text
            $cut = if ($text.Length -gt $Show) { $text.Substring(0, $Show) + " ..." } else { $text }
            Write-Host "        $cut"
            if ($ExpectError) { Check "${name}: isError, as expected" ([bool]$r.result.isError) | Out-Host; return $null }
            Check "${name}: not an error" (-not $r.result.isError) | Out-Host
            if ($r.result.isError) { return $null }
            return ($text | ConvertFrom-Json)
        }
    if ($Model) {
        $o = Tool "open_model" @{ path = [IO.Path]::GetFullPath($Model) } 900
        Check "open_model: working copy under the work dir, original untouched" ($o -and $o.workingCopy -like "*\models\*\r000.sdb")
        $d = Tool "describe_model" @{ max_rows = 20 }
        Check "describe_model: counts match open_model" ($d -and $d.objects.joints -eq $o.objects.joints -and $d.objects.frames -eq $o.objects.frames)
        foreach ($kind in "joint", "frame", "area", "link") {
            $l = Tool "list_objects" @{ kind = $kind; limit = 3 }
            $n = $d.objects.($kind + "s")
            Check "list_objects ${kind}: total $n" ($l -and $l.total -eq $n)
            if ($l -and $l.rows.Count -gt 0) {
                $g = Tool "get_object" @{ kind = $kind; name = $l.rows[0].name }
                Check "get_object $kind $($l.rows[0].name)" ($g -and $g.name -eq $l.rows[0].name)
            }
        }
        $null = Tool "get_object" @{ kind = "frame"; name = "no such frame" } -ExpectError
        $null = Tool "list_objects" @{ kind = "solid" } -ExpectError
        $s = Tool "get_sections" @{ kind = "frame" }
        Check "get_sections frame: use counts add up to the frames" ($s -and (($s.sections | Measure-Object frames -Sum).Sum -eq $d.objects.frames))
        $null = Tool "get_sections" @{ kind = "area" }
        $null = Tool "get_materials" @{}
        $null = Tool "get_load_cases" @{}
        $null = Tool "get_combos" @{}
        $lv = Tool "describe_levels" @{}
        $zmin = $d.extent.min[2]; $zmax = $d.extent.max[2]; $zmid = ($zmin + $zmax) / 2
        $all = Tool "select_objects" @{ limit = 5 }
        Check "select_objects, no criteria: every joint" ($all -and $all.summary.joint.count -eq $d.objects.joints)
        $lo = Tool "select_objects" @{ kind = "joint"; z_max = $zmid; limit = 5 }
        $hi = Tool "select_objects" @{ kind = "joint"; z_min = $zmid; limit = 5; save_as = "test-upper"; show_in_sap = $true }
        $on = Tool "select_objects" @{ kind = "joint"; plane = @{ axis = "z"; value = $zmid }; limit = 5 }
        Check "select_objects: below + above - on the mid plane = all joints" ($lo -and $hi -and $on -and
            ($lo.summary.joint.count + $hi.summary.joint.count - $on.summary.joint.count) -eq $d.objects.joints)
        $ls = Tool "list_objects" @{ kind = "joint"; selection = "test-upper"; limit = 2 }
        Check "list_objects selection: same count as saved" ($ls -and $ls.total -eq $hi.summary.joint.count)
        if ($d.groups.Count -gt 0) {
            $grp = $d.groups[0].name
            $sg = Tool "select_objects" @{ group = $grp; limit = 3; show_in_sap = $true }
            Check "select_objects group + show_in_sap: whole group selected in SAP" ($sg -and $sg.selectedInSap -is [int])
            $null = Tool "list_objects" @{ kind = "joint"; group = $grp; limit = 3 }
        }
        $t = Tool "read_table" @{ search = "Joint" }
        Check "read_table list: tables with data" ($t -and $t.tablesWithData -gt 0)
        $jc = Tool "read_table" @{ name = "Joint Coordinates"; limit = 2; fields = @("Joint", "XorR", "Y", "Z") }
        Check "read_table Joint Coordinates: one row per joint" ($jc -and $jc.total -eq $d.objects.joints)
        $f1 = Tool "read_table" @{ name = "Joint Coordinates"; filter = @{ Joint = $jc.rows[0].Joint }; to_file = $true }
        Check "read_table filter: one row, written to a file" ($f1 -and $f1.total -eq 1 -and (Test-Path $f1.file))

        if ($Run) {
            $r1 = Tool "run_analysis" @{} 3600
            Check "run_analysis: a checkpoint rNNN.sdb, every case reported" ($r1 -and $r1.file -like "*\r0*.sdb" -and $r1.cases.Count -gt 0)
            $rs = Tool "reactions_summary" @{}
            $checks = @($rs.cases | ForEach-Object check)
            Check ("reactions_summary: " + (($rs.cases | ForEach-Object { "$($_.case) $($_.check)" }) -join "; ")) ($rs -and ($checks -contains "balanced") -and -not ($checks -like "OFF*"))
            $m3 = Tool "query_results" @{ kind = "frame"; quantity = "M3"; top_n = 3 }
            Check "query_results frame M3: top 1 = the larger of |max| and |min|" ($m3 -and [Math]::Abs($m3.top[0].value) -eq [Math]::Max([Math]::Abs($m3.max.value), [Math]::Abs($m3.min.value)))
            $u = Tool "query_results" @{ kind = "joint"; quantity = "U"; top_n = 3; to_file = $true }
            Check "query_results joint U: every joint, rows written to a file" ($u -and $u.objects -eq $d.objects.joints -and (Test-Path $u.file))
            $sv = Tool "query_results" @{ kind = "area_stress"; quantity = "SVMTop"; top_n = 3; by_group = @("*") }
            Check "query_results area_stress SVMTop by_group: one summary per group" ($sv -and $sv.byGroup.Count -eq $d.groups.Count)
            $null = Tool "query_results" @{ kind = "reaction"; quantity = "F3"; top_n = 3; envelope = $false; sort = "min" }
            $null = Tool "query_results" @{ kind = "frame"; quantity = "Q9" } -ExpectError
            $r2 = Tool "run_analysis" @{} 3600
            Check "run_analysis again: the next checkpoint" ($r2 -and $r2.checkpoint -ne $r1.checkpoint)
            $runs = Tool "compare_runs" @{}
            Check "compare_runs list: both runs" ($runs -and ($runs.runs -contains $r1.checkpoint) -and ($runs.runs -contains $r2.checkpoint))
            $cmp = Tool "compare_runs" @{ kind = "joint"; quantity = "U3"; top_n = 3 }
            Check "compare_runs joint U3: same model, nothing changed" ($cmp -and $cmp.compared -gt 0 -and $cmp.changed -eq 0)
            $ex = Tool "export_viewer" @{} 1800
            Check "export_viewer: .bin and .features.json written" ($ex -and (Test-Path $ex.bin) -and (Test-Path $ex.features))
            if ($Viewer) {
                # the live viewer in a headless Edge (software WebGL), driven through the tools
                $vo = Tool "viewer_open" @{ open_browser = $false }
                Check "viewer_open: hosted on localhost" ($vo -and $vo.url -like "http://localhost:*/viewer/index.html?live=1")
                $edgeExe = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
                $profile = Join-Path $env:TEMP ("pluto-live-" + [Guid]::NewGuid().ToString("N"))
                $edge = Start-Process $edgeExe -ArgumentList @("--headless=new", "--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--no-first-run", "--user-data-dir=$profile", $vo.url) -PassThru
                try {
                    $con = $null
                    for ($k = 0; $k -lt 30 -and -not ($con -and $con.connected); $k++) { Start-Sleep -Milliseconds 1000; $con = Tool "viewer_open" @{ open_browser = $false } }
                    Check "viewer connects over the WebSocket" ($con -and $con.connected)
                    $vl = Tool "viewer_load" @{} 900
                    Check ("viewer_load: model with components (" + (@($vl.state.components) -join ", ") + ")") ($vl -and $vl.state.loaded -and @($vl.state.components).Count -gt 0 -and @($vl.state.cases).Count -gt 0)
                    $comp = @($vl.state.components)[0]
                    $vs = Tool "viewer_show" @{ component = $comp; case = "HYDRO" }
                    Check "viewer_show: HYDRO, $comp" ($vs -and $vs.case -like "HYDRO*" -and $vs.component -eq $comp)
                    $null = Tool "viewer_show" @{ case = "NO_SUCH_CASE" } -ExpectError
                    $vh = Tool "viewer_highlight" @{ group = $d.groups[1].name }
                    Check ("viewer_highlight group " + $d.groups[1].name + ": painted, groups on") ($vh -and ($vh.highlighted.shells + $vh.highlighted.beams + $vh.highlighted.nodes) -gt 0 -and $vh.state.groupsOn -and (@($vh.state.groups) | Where-Object { $_.live -and $_.shown }))
                    $vu = Tool "viewer_highlight" @{ clear = $true }
                    Check "viewer_highlight clear: no live group" ($vu -and -not (@($vu.groups) | Where-Object live))
                    $vi = Tool "viewer_isolate" @{ group = $d.groups[1].name }
                    Check "viewer_isolate: some elements kept" ($vi -and $vi.isolated -and ($vi.isolated.shells + $vi.isolated.beams) -gt 0)
                    $vc = Tool "viewer_isolate" @{ clear = $true }
                    Check "viewer_isolate clear: everything shown" ($vc -and $null -eq $vc.isolated)
                    $vf = Tool "viewer_focus" @{ kind = "joint"; name = "1" }
                    Check "viewer_focus joint 1" ($vf -and $vf.status -like "Node*")
                    $vg = Tool "viewer_get_selection" @{}
                    Check "viewer_get_selection: state, nothing pinned" ($vg -and $vg.state.loaded -and $vg.state.pinned -eq $false)
                    $rr = Tool "run_analysis" @{} 3600
                    Check "run_analysis with a viewer connected: exported and reloaded" ($rr -and $rr.viewer.reloaded -like "*$($rr.checkpoint).bin")
                } finally {
                    if ($edge -and -not $edge.HasExited) { Stop-Process -Id $edge.Id -Force -ErrorAction SilentlyContinue }
                    Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" | Where-Object { $_.CommandLine -like "*$profile*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
                    Start-Sleep -Milliseconds 500
                    Remove-Item -Recurse -Force $profile -ErrorAction SilentlyContinue
                }
            }

            if ($beam) {
                # the self-test beam by hand: 20 ft, 10 kip at midspan -> reactions 5 + 5 kip up, M3 50 kip-ft
                $null = Tool "open_model" @{ path = $beam } 900
                $null = Tool "run_analysis" @{} 900
                $bm = Tool "query_results" @{ kind = "frame"; quantity = "M3"; top_n = 1 }
                Check "beam: max |M3| = 50 kip-ft" ($bm -and [Math]::Abs([Math]::Abs($bm.top[0].value) - 50) -lt 1e-6)
                $br = Tool "reactions_summary" @{}
                $c = $br.cases | Where-Object case -eq "LIVE"
                Check "beam: applied FZ -10, reaction FZ +10, balanced" ($c -and [Math]::Abs($c.applied.FZ + 10) -lt 1e-9 -and [Math]::Abs($c.reaction.FZ - 10) -lt 1e-6 -and $c.check -eq "balanced")
                $rq = Tool "query_results" @{ kind = "reaction"; quantity = "F3"; top_n = 2 }
                Check "beam: two supports at 5 kip" ($rq -and $rq.top.Count -eq 2 -and [Math]::Abs($rq.top[0].value - 5) -lt 1e-6 -and [Math]::Abs($rq.top[1].value - 5) -lt 1e-6)
            }
        }
    }
    if ($Edit) {
        if (-not $beam) { throw "-Edit needs -SelfTest (it edits the self-test beam)" }
        function Near($a, $b) { return ($null -ne $a) -and [Math]::Abs([double]$a - [double]$b) -lt 1e-6 }
        $o = Tool "open_model" @{ path = $beam } 900
        $e = Tool "set_frame_section" @{ names = @("1"); section = "BEAM"; dry_run = $true }
        Check "edit dry run, same section: unchanged" ($e -and $e.unchanged -eq 1 -and $e.wouldChange -eq 0)
        $null = Tool "set_frame_section" @{ names = @("1"); section = "NOPE" } -ExpectError
        $e = Tool "define_load_pattern" @{ name = "PT"; type = "Live" }
        Check "define_load_pattern: first edit starts r001 (r000 stays as opened)" ($e -and $e.checkpoint -eq "r001" -and $e.changed -eq 1)
        $e = Tool "assign_joint_load" @{ names = @("2"); pattern = "PT"; F3 = -20; dry_run = $true }
        Check "assign_joint_load dry run: would change joint 2" ($e -and $e.wouldChange -eq 1)
        $e = Tool "assign_joint_load" @{ names = @("2"); pattern = "PT"; F3 = -20 }
        Check "assign_joint_load: into r001, read back F3 -20" ($e -and $e.checkpoint -eq "r001" -and (Near $e.changes[0].after[0].F3 -20))
        $r1 = Tool "run_analysis" @{} 900
        Check "run_analysis: the edited r001 runs in place" ($r1 -and $r1.checkpoint -eq "r001" -and $r1.from -like "edited*")
        $q = Tool "query_results" @{ kind = "frame"; quantity = "M3"; cases = @("PT"); top_n = 1 }
        Check "PT 20 kip at midspan: M3 = 100 kip-ft" ($q -and (Near ([Math]::Abs($q.top[0].value)) 100))
        $e = Tool "set_restraint" @{ names = @("3"); fixed_dofs = @("U1", "U2", "U3", "R1"); reason = "bearing restrains torsion" }
        Check "set_restraint on a run checkpoint: new r002, r001 keeps its results" ($e -and $e.checkpoint -eq "r002" -and $e.note -like "*r001 keeps*")
        $e = Tool "assign_frame_load" @{ group = "ALL"; pattern = "PT"; type = "distributed"; dir = "gravity"; value = 1 }
        Check "assign_frame_load: both frames" ($e -and $e.changed -eq 2 -and $e.checkpoint -eq "r002")
        $e = Tool "define_combo" @{ name = "C1"; items = @(@{ name = "LIVE"; scale = 1 }, @{ name = "PT"; scale = 2 }) }
        Check "define_combo C1" ($e -and $e.changed -eq 1)
        $r2 = Tool "run_analysis" @{} 900
        $rs = Tool "reactions_summary" @{}
        $pt = $rs.cases | Where-Object case -eq "PT"; $c1 = $rs.cases | Where-Object case -eq "C1"
        Check "PT: applied -40 (20 + 1 kip/ft x 20 ft), balanced" ($pt -and (Near $pt.applied.FZ -40) -and $pt.check -eq "balanced")
        Check "C1 = LIVE + 2 PT: applied -90, balanced" ($c1 -and (Near $c1.applied.FZ -90) -and $c1.check -eq "balanced")
        $q = Tool "query_results" @{ kind = "frame"; quantity = "M3"; cases = @("PT", "C1"); top_n = 1 }
        Check "M3: PT 150 (PL/4 + wL^2/8), C1 350" ($q -and (Near ([Math]::Abs(($q.byCase | Where-Object case -eq "PT").value)) 150) -and (Near ([Math]::Abs(($q.byCase | Where-Object case -eq "C1").value)) 350))
        $e = Tool "move_joints" @{ names = @("2"); delta = @(0, 0, 0.5); dry_run = $true }
        Check "move_joints dry run" ($e -and $e.wouldChange -eq 1)
        $e = Tool "add_frame" @{ i = "1"; j = "3"; section = "BEAM"; name = "X1" }
        Check "add_frame X1: a new checkpoint r003 (r002 was run)" ($e -and $e.changed -eq 1 -and $e.checkpoint -eq "r003")
        $e = Tool "delete_objects" @{ kind = "frame"; names = @("X1") }
        Check "delete_objects X1" ($e -and $e.changed -eq 1)
        $e = Tool "set_frame_release" @{ names = @("1"); i_end = @("M3"); j_end = @() }
        Check "set_frame_release frame 1: I end M3" ($e -and $e.changed -eq 1 -and $e.changes[0].after -like "I M3*")
        $e = Tool "set_group" @{ group = "MID"; kind = "joint"; add = @("2") }
        Check "set_group: MID created with joint 2" ($e -and $e.created -eq "MID")
        $b = Tool "describe_basis" @{}
        Check "describe_basis: frame 1 released I M3" ($b -and $b.releases.releasedFrames -eq 1 -and $b.releases.byRelease."I M3, J -" -eq 1)
        Check "describe_basis: C1 = LIVE + 2 PT, PT case loads, a mass source" ((@(($b.combos | Where-Object name -eq "C1").items) | Where-Object { $_.name -eq "PT" -and $_.scale -eq 2 }) -and @(($b.loadCases | Where-Object name -eq "PT").loads).Count -ge 1 -and @($b.analysis.massSources).Count -ge 1 -and $b.analysis.activeDof)
        $rv = Tool "revert" @{ checkpoint = "r001" }
        Check "revert r001: its results are there" ($rv -and ($rv.casesWithResults -contains "PT"))
        $q = Tool "query_results" @{ kind = "frame"; quantity = "M3"; cases = @("PT"); top_n = 1 }
        Check "after revert: PT M3 = 100 again, the r001 results" ($q -and (Near ([Math]::Abs($q.top[0].value)) 100))
        $lc = Tool "list_checkpoints" @{}
        Check ("list_checkpoints: " + (($lc.checkpoints | ForEach-Object { $_.checkpoint + $(if ($_.held) { "*" } else { "" }) + " edits " + [int]$_.editCount }) -join ", ")) ($lc -and $lc.checkpoints.Count -eq 4 -and ($lc.checkpoints | Where-Object held).checkpoint -eq "r001")
        $cl = Tool "get_change_log" @{}
        Check "get_change_log: every applied edit logged" ($cl -and $cl.total -eq 9)
        Check "get_change_log: the edit's reason kept" (($cl.entries | Where-Object tool -eq "set_restraint").reason -eq "bearing restrains torsion")
        Check "list_checkpoints: r002 lists the reason" ((($lc.checkpoints | Where-Object checkpoint -eq "r002").edits -join "|") -like "*because bearing restrains torsion*")
        $e = Tool "set_restraint" @{ names = @("1"); fixed_dofs = @("U1", "U2", "U3", "R1", "R2", "R3") }
        Check "an edit after revert starts a new checkpoint (r004), r001 untouched" ($e -and $e.checkpoint -eq "r004")
        # design notes: the why beside the edits
        $n1 = Tool "log_note" @{ kind = "assumption"; topic = "supports"; text = "Left support fixed: the beam is cast into the wall."; basis = "engineering judgement"; names = @("1") }
        Check "log_note: n001 on r004" ($n1 -and $n1.logged.id -eq "n001" -and $n1.logged.checkpoint -eq "r004" -and $n1.logged.kind -eq "assumption")
        $n2 = Tool "log_note" @{ kind = "question"; topic = "loads"; text = "Is PT a live or a dead load?" }
        $n3 = Tool "log_note" @{ kind = "decision"; topic = "loads"; text = "PT is live (occupancy)."; basis = "supplied data"; supersedes = "n002" }
        Check "log_note: a decision supersedes the question" ($n2 -and $n3 -and $n3.logged.id -eq "n003" -and $n3.logged.supersedes -eq "n002")
        $null = Tool "log_note" @{ kind = "decision"; text = "again"; supersedes = "n002" } -ExpectError
        $null = Tool "log_note" @{ kind = "guess"; text = "x" } -ExpectError
        $ln = Tool "list_notes" @{}
        Check "list_notes: superseded left out" ($ln -and $ln.total -eq 3 -and @($ln.notes).Count -eq 2 -and -not ($ln.notes | Where-Object id -eq "n002"))
        $ln = Tool "list_notes" @{ topic = "loads"; superseded = $true }
        Check "list_notes topic loads with superseded: n002 marked by n003" ($ln -and @($ln.notes).Count -eq 2 -and ($ln.notes | Where-Object id -eq "n002").supersededBy -eq "n003")
        # the design record: written into a throwaway notes dir, hand text kept on a rewrite
        $nd = Join-Path ([IO.Path]::GetTempPath()) ("pluto-notes-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
        try {
            $dr = Tool "write_design_record" @{ notes_dir = $nd; purpose = "Beam edit test."; elements = @("BEAM-A") }
            $txt = if ($dr -and (Test-Path $dr.file)) { [IO.File]::ReadAllText($dr.file) } else { "" }
            Check "write_design_record: basis of r004, notes, edits with reasons, runs" ($dr -and $dr.basisFrom -like "r004*" -and $txt.Contains("[[BEAM-A]]") -and $txt -like "*Left support fixed*" -and $txt -like "*replaces question n002*" -and $txt -like "*bearing restrains torsion*" -and $txt -like "*### Run r002*")
            [IO.File]::WriteAllText($dr.file, $txt.Replace("<!-- pluto:notes checks -->`r`n", "<!-- pluto:notes checks -->`r`nChecked by hand.`r`n").Replace('status: "draft"', 'status: "reviewed"'))
            $dr2 = Tool "write_design_record" @{ notes_dir = $nd }
            $txt2 = [IO.File]::ReadAllText($dr2.file)
            Check "write_design_record again: hand text, status, purpose and elements kept" ($txt2 -like "*Checked by hand.*" -and $txt2 -like '*status: "reviewed"*' -and $txt2 -like "*Beam edit test.*" -and $txt2.Contains("[[BEAM-A]]"))
        } finally { Remove-Item -Recurse -Force $nd -ErrorAction SilentlyContinue }
    }
    if ($Tables) {
        if (-not $beam) { throw "-Tables needs -SelfTest (it edits the self-test beam)" }
        # edits the exported workbook the way a user does, in Excel
        function EditBook([string]$path, [scriptblock]$edit) {
            $xl = New-Object -ComObject Excel.Application
            $xl.DisplayAlerts = $false
            try { $wb = $xl.Workbooks.Open($path); & $edit $wb; $wb.Save(); $wb.Close($false) }
            finally { $xl.Quit(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($xl); [GC]::Collect() }
        }
        function SetCell($ws, [string]$keyField, [string]$key, [string]$field, [string]$value) {
            $cols = $ws.UsedRange.Columns.Count; $rows = $ws.UsedRange.Rows.Count; $kc = 0; $fc = 0
            for ($c = 1; $c -le $cols; $c++) { $h = [string]$ws.Cells.Item(1, $c).Value2; if ($h -eq $keyField) { $kc = $c }; if ($h -eq $field) { $fc = $c } }
            for ($r = 2; $r -le $rows; $r++) { if ([string]$ws.Cells.Item($r, $kc).Value2 -eq $key) { $ws.Cells.Item($r, $fc).Value2 = $value; return } }
            throw "no row $keyField=$key"
        }
        function AddRow($ws, [hashtable]$values) {
            $cols = $ws.UsedRange.Columns.Count; $r = $ws.UsedRange.Rows.Count + 1
            for ($c = 2; $c -le $cols; $c++) { $h = [string]$ws.Cells.Item(1, $c).Value2; if ($values.ContainsKey($h)) { $ws.Cells.Item($r, $c).Value2 = $values[$h] } }
        }
        $o = Tool "open_model" @{ path = $beam } 900
        $x = Tool "export_tables" @{ tables = @("Joint Coordinates", "Load Pattern Definitions"); open = $false }
        Check "export_tables: workbook with both tables (3 joints, 1 pattern)" ($x -and (Test-Path $x.workbook) -and ($x.tables | Where-Object { $_.table -eq "Joint Coordinates" }).rows -eq 3 -and ($x.tables | Where-Object { $_.table -eq "Load Pattern Definitions" }).rows -eq 1)
        EditBook $x.workbook { param($wb)
            SetCell $wb.Worksheets.Item("Joint Coordinates") "Joint" "3" "Z" "0.25"
            AddRow $wb.Worksheets.Item("Load Pattern Definitions") @{ LoadPat = "PT2"; DesignType = "Live"; SelfWtMult = "0" }
        }
        $dr = Tool "import_tables" @{}
        $jc = $dr.tables | Where-Object { $_.table -eq "Joint Coordinates" }; $lp = $dr.tables | Where-Object { $_.table -eq "Load Pattern Definitions" }
        Check "import_tables dry run (default): joint 3 changed, pattern PT2 added, nothing applied" ($dr -and $dr.dryRun -and $jc.changed -eq 1 -and $lp.added -eq 1)
        $j0 = Tool "get_object" @{ kind = "joint"; name = "3" }
        Check "dry run left the model alone: joint 3 Z = 0" ($j0 -and $j0.xyz[2] -eq 0)
        $ap = Tool "import_tables" @{ dry_run = $false } 600
        Check "import_tables applied into r001, SAP2000 import clean, rows as sent, tables still read" ($ap -and $ap.checkpoint -eq "r001" -and $ap.import.applied -and $ap.import.errors -eq 0 -and -not $ap.notAsSent -and $ap.import.tablesReadable)
        $j1 = Tool "get_object" @{ kind = "joint"; name = "3" }
        $dm = Tool "describe_model" @{}
        Check "the edits took: joint 3 Z = 0.25, pattern PT2 exists" ($j1 -and [Math]::Abs($j1.xyz[2] - 0.25) -lt 1e-9 -and ($dm.loadPatterns | Where-Object name -eq "PT2"))
        $null = Tool "run_analysis" @{} 900
        $x2 = Tool "export_tables" @{ tables = @("Joint Coordinates"); open = $false }
        EditBook $x2.workbook { param($wb) SetCell $wb.Worksheets.Item("Joint Coordinates") "Joint" "3" "Z" "0" }
        $ap2 = Tool "import_tables" @{ dry_run = $false } 600
        Check "import after a run: new checkpoint r002 (r001 keeps its results), applied" ($ap2 -and $ap2.checkpoint -eq "r002" -and $ap2.import.applied)
        $x3 = Tool "export_tables" @{ tables = @("Load Pattern Definitions"); open = $false }
        EditBook $x3.workbook { param($wb) SetCell $wb.Worksheets.Item("Load Pattern Definitions") "LoadPat" "PT2" "DesignType" "NOT_A_TYPE" }
        $bad = Tool "import_tables" @{ dry_run = $false } 600
        Check ("a bad value: SAP2000 reports it (" + (@($bad.import.messages) -join " / ") + "), the row is flagged not as sent, tables still read") ($bad -and @($bad.import.messages).Count -gt 0 -and $bad.notAsSent -and $bad.import.tablesReadable)
        $x4 = Tool "export_tables" @{ tables = @("Joint Coordinates"); group = "ALL"; open = $false }
        Check "export_tables by group: the group's rows" ($x4 -and ($x4.tables[0].rows -eq 3))
    }
} catch {
    Check ("exception: " + $_.Exception.Message) $false
} finally {
    $stdin.Close()
    if (-not $p.WaitForExit(15000)) { $p.Kill(); Check "server exits when stdin closes" $false } else { Check "server exits when stdin closes" $true }
    $err = $errTask.Result
    if ($err) { Write-Output "--- server stderr ---"; Write-Output $err.TrimEnd() }
}
Write-Output $(if ($failed) { "$failed check(s) FAILED" } else { "all checks passed" })
exit $failed
