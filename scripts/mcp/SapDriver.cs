// Pluto MCP: the model-driver interface and the SAP2000 driver. Every OAPI call goes through SapSession
// (scripts\sap, the one SAP controller) on this driver's STA thread. C# 5 (see McpServer.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// One per analysis program. Tools are named for the task; a driver states what it can do so the LLM
// never assumes a capability the program lacks (plan § 0).
public interface IModelDriver : IDisposable
{
    string Program { get; }
    Dictionary<string, object> Capabilities();
    Dictionary<string, object> Status();
}

public sealed class SapDriver : IModelDriver
{
    static readonly TimeSpan Quick = TimeSpan.FromSeconds(60), Long = TimeSpan.FromMinutes(15);
    static readonly string[] CaseStates = { "?", "not run", "could not start", "not finished", "finished" };

    readonly StaWorker sta = new StaWorker("SAP2000 OAPI");
    readonly string sapExe;
    readonly Log log;
    SapSession session;   // the attached SAP2000; STA thread only

    public SapDriver(string sapExe, Log log) { this.sapExe = sapExe; this.log = log; }

    public string Program { get { return "SAP2000"; } }

    public Dictionary<string, object> Capabilities()
    {
        return Json.O(
            "status", true,
            "self_test", true,
            "select_in_gui", "planned (phase 1): the OAPI selects by group, property, coordinate range, plane",
            "gui_display_control", "not available: the OAPI only refreshes the view; results are shown in the Pluto viewer (phase 4)",
            "open_model", "planned (phase 1)",
            "describe_model", "planned (phase 1)",
            "run_analysis", "planned (phase 2)",
            "query_results", "planned (phase 2)",
            "edit_model", "planned (phase 3)");
    }

    // Attach to the SAP2000 the user has open; never starts one. Reattaches after SAP is closed and reopened.
    SapSession Attached()
    {
        if (session != null)
        {
            try { session.Version(); return session; }
            catch (COMException) { session = null; }
            catch (InvalidComObjectException) { session = null; }
            log.Info("SAP2000 connection lost: reattaching");
        }
        session = SapSession.AttachOrStart(sapExe, true);
        log.Info("attached to SAP2000 " + session.Version());
        return session;
    }

    public Dictionary<string, object> Status()
    {
        return sta.Invoke("status", Quick, delegate
        {
            SapSession s;
            try { s = Attached(); }
            catch (Exception e)
            {
                // Verified 2026-10-12 (SAP 26): an elevated process cannot see a normal SAP2000 in the running-object
                // table (MK_E_UNAVAILABLE); the same code attaches from a normal process.
                bool open = Process.GetProcessesByName("SAP2000").Length > 0;
                string why = !open ? e.Message
                    : Elevated() ? "SAP2000 is open, but this server runs as administrator and Windows hides a normal SAP2000 " +
                                   "from elevated programs. Restart the MCP client (Claude Code / VS Code) normally, not " +
                                   "'Run as administrator' (or run SAP2000 as administrator too)."
                    : "SAP2000 is open but did not answer the API: " + e.Message;
                return Json.O("program", Program, "running", open, "attached", false, "serverElevated", Elevated(),
                    "message", why + (open ? " self_test still works: it starts its own SAP2000." : ""));
            }

            Dictionary<string, object> r = Json.O("program", Program, "running", true, "attached", true, "version", s.Version());
            string path = s.ModelPath();
            if (string.IsNullOrEmpty(path))
            {
                r["model"] = null;
                r["message"] = "SAP2000 holds no model";
                return r;
            }
            r["model"] = path;
            r["units"] = s.Units();
            int[] c = s.Counts();
            r["objects"] = Json.O("joints", c[0], "areas", c[1], "frames", c[2], "links", c[3]);
            r["locked"] = s.Model.GetModelIsLocked();
            List<object> cases = new List<object>();
            foreach (KeyValuePair<string, int> kv in s.CaseStatus())
                cases.Add(Json.O("name", kv.Key, "status", kv.Value >= 1 && kv.Value <= 4 ? CaseStates[kv.Value] : kv.Value.ToString(Json.Inv)));
            r["loadCases"] = cases;
            r["groups"] = s.Groups();
            return r;
        });
    }

    // Simply supported 20 ft beam, 10 kip at midspan (SapSession.WriteTestBeamS2k): expects max |V2| = 5 kip,
    // max |M3| = 50 kip-ft. A second SAP2000 by default, closed afterwards; useRunning opens the beam in the
    // attached SAP instead, replacing the model it holds.
    public Dictionary<string, object> SelfTest(string dir, bool useRunning)
    {
        return sta.Invoke("self_test", Long, delegate
        {
            Directory.CreateDirectory(dir);
            Stopwatch sw = Stopwatch.StartNew();
            SapSession s = useRunning ? Attached() : SapSession.NewInstance(sapExe);
            try
            {
                string version = s.Version();
                string s2k = Path.Combine(dir, "beam.s2k");
                SapSession.WriteTestBeamS2k(s2k, version, 20.0, 10.0);
                string held = s.Open(s2k, true);
                double runSecs = s.Run();
                string results = Path.Combine(dir, "results.s2k");
                s.WriteResultsS2k(results, null, false, "PlutoMcp self_test");

                double v = 0, m = 0;
                Regex row = new Regex(@"^\s+Frame=\S+ .*\sV2=(\S+)\s.*\sM3=(\S+)");
                foreach (string line in File.ReadAllLines(results))
                {
                    Match x = row.Match(line);
                    if (!x.Success) continue;
                    v = Math.Max(v, Math.Abs(double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)));
                    m = Math.Max(m, Math.Abs(double.Parse(x.Groups[2].Value, CultureInfo.InvariantCulture)));
                }
                bool ok = Math.Abs(v - 5) < 1e-6 && Math.Abs(m - 50) < 1e-6;
                return Json.O(
                    "passed", ok,
                    "version", version,
                    "instance", useRunning ? "attached" : "new (closed afterwards)",
                    "maxV2_kip", v, "expectedV2_kip", 5.0,
                    "maxM3_kipft", m, "expectedM3_kipft", 50.0,
                    "model", held, "results", results,
                    "runSeconds", Math.Round(runSecs, 1), "totalSeconds", Math.Round(sw.Elapsed.TotalSeconds, 1));
            }
            finally { if (!useRunning) s.Close(); }
        });
    }

    static bool Elevated()
    {
        return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public void Dispose() { sta.Dispose(); }   // never closes the SAP2000 it attached to
}
