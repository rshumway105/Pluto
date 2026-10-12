# Protocol test for the Pluto MCP server, no LLM involved: starts the server the way an MCP client does
# (through Start-PlutoMcp.ps1, or the exe with -Direct), sends initialize / initialized / tools/list /
# tools/call status [/ self_test] [/ the read tools on -Model], checks every reply, prints them.
# Exit 0 = all checks passed. -Model opens that file in the running SAP2000 (replacing what it holds) as a
# working copy and runs every phase 1 tool on it; -Run adds the phase 2 tools (two runs, results, reactions,
# compare, viewer export), and with -SelfTest also opens the self-test beam and checks its hand values through
# them. Replies are printed cut to -Show characters.
#   powershell -ExecutionPolicy Bypass -File Test-PlutoMcp.ps1 [-Direct] [-SelfTest] [-Model <sdb|s2k> [-Run]] [-Show 600]
param([switch]$Direct, [switch]$SelfTest, [string]$Model, [switch]$Run, [int]$Show = 600)
$ErrorActionPreference = "Stop"
$psi = New-Object Diagnostics.ProcessStartInfo
if ($Direct) {
    $psi.FileName = Join-Path $PSScriptRoot "bin\PlutoMcp.exe"
} else {
    $psi.FileName = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
    $psi.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $PSScriptRoot "Start-PlutoMcp.ps1") + '"'
}
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = New-Object Text.UTF8Encoding $false
$p = [Diagnostics.Process]::Start($psi)
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

    if ($Model) {
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
