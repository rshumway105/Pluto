# Protocol test for the Pluto MCP server, no LLM involved: starts the server the way an MCP client does
# (through Start-PlutoMcp.ps1, or the exe with -Direct), sends initialize / initialized / tools/list /
# tools/call status [/ self_test], checks every reply, prints them. Exit 0 = all checks passed.
#   powershell -ExecutionPolicy Bypass -File Test-PlutoMcp.ps1 [-Direct] [-SelfTest]
param([switch]$Direct, [switch]$SelfTest)
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
    Check "$method reply is JSON-RPC with id $id" ($r.jsonrpc -eq "2.0" -and $r.id -eq $id)
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

    if ($SelfTest) {
        $r = Call 6 "tools/call" @{ name = "self_test"; arguments = @{} } 900
        $text = $r.result.content[0].text
        Write-Output "        $text"
        Check "self_test: passed" ((-not $r.result.isError) -and ($text | ConvertFrom-Json).passed -eq $true)
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
