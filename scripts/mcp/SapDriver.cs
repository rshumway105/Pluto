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
            "open_model", true,
            "describe_model", true,
            "list_objects", true,
            "get_object", true,
            "get_sections", true,
            "get_materials", true,
            "get_load_cases", true,
            "get_combos", true,
            "select_objects", true,
            "select_in_gui", "select_objects show_in_sap: GUI selection only, the model is not changed",
            "describe_levels", "from joint Z clusters, grid Z lines and level-named groups (SAP2000 has no stories)",
            "read_table", HasTables() ? (object)true : "not available: this SAP2000 API has no DatabaseTables",
            "gui_display_control", "not available: the OAPI only refreshes the view; results are shown in the Pluto viewer (phase 4)",
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

    bool? hasTables;
    bool HasTables()
    {
        if (hasTables == null) hasTables = typeof(SAP2000v1.cSapModel).GetProperty("DatabaseTables") != null;
        return hasTables.Value;
    }

    // ---------------------------------------------------------------- read (phase 1)

    SapRead reader; SapSession readerFor;

    // A read on the model SAP holds, on the STA thread. Reads never change the model.
    public T Read<T>(string what, Func<SapRead, T> fn)
    {
        return sta.Invoke(what, ReadTimeout, delegate
        {
            SapSession s = Attached();
            if (string.IsNullOrEmpty(s.ModelPath())) throw new ToolError("SAP2000 holds no model: open one with open_model");
            if (reader == null || readerFor != s) { reader = new SapRead(s); readerFor = s; }
            return fn(reader);
        });
    }

    static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(5);

    // Opens path in the attached SAP2000 (replacing what it holds) and at once saves it as the working copy
    // <session dir>\r000.sdb, so every later save, run or edit writes the copy and never the original (plan § 3).
    // A .s2k is imported first; SAP imports nothing from a file stamped with a newer major version than its own.
    public Dictionary<string, object> OpenModel(string path, string sessionDir)
    {
        return sta.Invoke("open_model", Long, delegate
        {
            if (!File.Exists(path)) throw new ToolError("no such file: " + path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".sdb" && ext != ".s2k" && ext != ".$2k") throw new ToolError("open_model takes a .sdb, .s2k or .$2k file (got '" + ext + "')");
            SapSession s = Attached();
            string before = s.ModelPath();
            Stopwatch sw = Stopwatch.StartNew();
            s.Open(path, false);
            Directory.CreateDirectory(sessionDir);
            string copy = Path.Combine(sessionDir, "r000.sdb");
            s.SaveAs(copy);
            reader = null;
            int[] c = s.Counts();
            File.WriteAllText(Path.Combine(sessionDir, "session.json"), Json.Write(Json.O(
                "original", path, "originalBytes", new FileInfo(path).Length,
                "originalModifiedUtc", File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv),
                "openedUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv),
                "sapVersion", s.Version(), "workingCopy", copy)));
            log.Info("open_model " + path + " -> " + copy);
            Dictionary<string, object> r = Json.O(
                "original", path, "workingCopy", s.ModelPath(), "sessionDir", sessionDir,
                "replaced", string.IsNullOrEmpty(before) ? null : before,
                "units", s.Units(),
                "objects", Json.O("joints", c[0], "areas", c[1], "frames", c[2], "links", c[3]),
                "seconds", Math.Round(sw.Elapsed.TotalSeconds, 1),
                "note", "SAP2000 now holds the working copy; the original file is never written. Next: describe_model.");
            if (c[0] == 0 && ext != ".sdb")
                r["warning"] = "the import produced no joints: check the file's PROGRAM CONTROL Version (SAP2000 imports nothing from a newer major version) and SAP2000's import log";
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
