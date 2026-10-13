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

    // The SAP2000 process of an own instance (PLUTO_SAP_INSTANCE=new), for child runners (Run-Sap.ps1 -AttachPid); else 0.
    public int OwnProcessId { get { return OwnInstance && session != null ? session.ProcessId : 0; } }

    public Dictionary<string, object> Capabilities()
    {
        return Json.O(
            "status", true,
            "self_test", true,
            "open_model", true,
            "describe_model", true,
            "describe_basis", true,
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
            "gui_display_control", "not available: the OAPI only refreshes the view; results are shown in the live Pluto viewer",
            "viewer", "live Pluto viewer on localhost: viewer_open, viewer_load, viewer_show, viewer_highlight, viewer_isolate, viewer_focus, viewer_get_selection; run_analysis reloads it",
            "run_analysis", "on a new checkpoint rNNN.sdb in the session dir; the opened file is never written",
            "query_results", "full precision (Results.*): joint, reaction, frame, area, area_stress, link; nonlinear / multi-step static: last step",
            "reactions_summary", "base reactions against the applied load summed from the load assignments (static cases, linear additive combos)",
            "compare_runs", "from the per-object envelopes each run_analysis saves",
            "export_viewer", "SapExport -> SapToPluto through Run-Sap.ps1 -Export: .bin + .features.json",
            "edit_model", "targeted tools (sections, properties, materials, restraints, releases, loads, patterns, cases, combos, groups, joint moves, frames, deletes), each into a checkpoint, logged, with dry_run",
            "tables_in_excel", "export_tables -> Excel -> import_tables (dry run first; into a checkpoint, unlocked, one import, its log)",
            "revert", "reopens any checkpoint of the session; list_checkpoints, get_change_log",
            "design_notes", "log_note / list_notes: assumptions, conditions, decisions, questions with their basis (notes.jsonl); every edit takes an optional reason",
            "design_record", "write_design_record: the session as an Obsidian note (basis, assumptions, changes with reasons, checks, open questions) in PLUTO_NOTES_DIR\\Records");
    }

    // PLUTO_SAP_INSTANCE=new: the server starts a SAP2000 of its own and closes it on exit (tests that must not touch the
    // user's SAP2000). Otherwise it attaches to the one the user has open and never starts one.
    static readonly bool OwnInstance = Environment.GetEnvironmentVariable("PLUTO_SAP_INSTANCE") == "new";

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
        session = OwnInstance ? SapSession.NewInstance(sapExe) : SapSession.AttachOrStart(sapExe, true);
        log.Info((OwnInstance ? "started an own SAP2000 " : "attached to SAP2000 ") + session.Version());
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
        return sta.Invoke(what, ReadTimeout, delegate { return fn(Reader(Held())); });
    }

    static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(5), ResultsTimeout = TimeSpan.FromMinutes(15), RunTimeout = TimeSpan.FromMinutes(60);

    SapSession Held()
    {
        SapSession s = Attached();
        if (string.IsNullOrEmpty(s.ModelPath())) throw new ToolError("SAP2000 holds no model: open one with open_model");
        return s;
    }

    SapRead Reader(SapSession s)
    {
        if (reader == null || readerFor != s) { reader = new SapRead(s); readerFor = s; }
        return reader;
    }

    // Session dir of a model file: the folder holding session.json (open_model or run_analysis made it), else null.
    public static string SessionDirOf(string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;
        string dir = Path.GetDirectoryName(modelPath);
        return dir != null && File.Exists(Path.Combine(dir, "session.json")) ? dir : null;
    }

    static void WriteSession(string dir, string original, string version, string copy, string note)
    {
        Dictionary<string, object> d = Json.O("original", original);
        if (File.Exists(original))
        {
            d["originalBytes"] = new FileInfo(original).Length;
            d["originalModifiedUtc"] = File.GetLastWriteTimeUtc(original).ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv);
        }
        d["openedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv);
        d["sapVersion"] = version; d["workingCopy"] = copy;
        if (note != null) d["note"] = note;
        File.WriteAllText(Path.Combine(dir, "session.json"), Json.Write(d));
    }

    // ---------------------------------------------------------------- run and results (phase 2)

    public const double DefaultTolerance = 0.005;

    // Results of the model SAP holds, on the STA thread (also reads).
    public T Results<T>(string what, Func<SapRead, SapResults, T> fn)
    {
        return sta.Invoke(what, ResultsTimeout, delegate
        {
            SapSession s = Held();
            SapRead r = Reader(s);
            try { return fn(r, new SapResults(s, r)); }
            catch (ArgumentException e) { throw new ToolError(e.Message); }
        });
    }

    // ---------------------------------------------------------------- checkpoints

    // One model state per checkpoint <session dir>\rNNN.sdb; r000 is the model as opened. Edits and runs go into the
    // latest checkpoint while it has not been run (no runs\rNNN); otherwise (r000, a run checkpoint, an older one
    // after revert) the model is first saved as the next number, so no earlier state or its results is written.
    // SAP2000 keeps a run's results with its own file name: saving as a new name copies the model only.
    // A model SAP held without open_model first gets a session of its own with r000.sdb as it was then.
    sealed class Checkpoint
    {
        public string Dir, Name, File, Adopted, From;
        public bool Started;   // a new number was saved
    }

    Checkpoint WorkingCheckpoint(SapSession s, Func<string, string> newSessionDir, string tool)
    {
        Checkpoint c = new Checkpoint();
        string path = s.ModelPath();
        c.Dir = SessionDirOf(path);
        if (c.Dir == null)
        {
            c.Dir = newSessionDir(path);
            Directory.CreateDirectory(c.Dir);
            string r0 = Path.Combine(c.Dir, "r000.sdb");
            s.SaveAs(r0);
            WriteSession(c.Dir, path, s.Version(), r0, "SAP2000 held this model when " + tool + " was called; saved here as r000.sdb, the file itself is not written");
            c.Adopted = path;
            path = r0;
        }
        c.From = Path.GetFileNameWithoutExtension(path);
        int n = CheckpointNumber(path), latest = NextCheckpoint(c.Dir) - 1;
        if (n >= 1 && n == latest && !Directory.Exists(Path.Combine(c.Dir, "runs", c.From)))
        {
            c.Name = c.From; c.File = path;
            return c;
        }
        c.Name = "r" + (latest + 1).ToString("000", Json.Inv);
        c.File = Path.Combine(c.Dir, c.Name + ".sdb");
        s.SaveAs(c.File);
        c.Started = true;
        return c;
    }

    static int CheckpointNumber(string path)
    {
        Match m = Regex.Match(Path.GetFileNameWithoutExtension(path ?? ""), @"^r(\d+)$");
        return m.Success ? int.Parse(m.Groups[1].Value, Json.Inv) : -1;
    }

    // Runs the analysis on the working checkpoint (cases: null = every case). summary: saves the per-object
    // envelopes (compare_runs) and base reactions to <session dir>\runs\rNNN.
    public Dictionary<string, object> RunAnalysis(string[] cases, bool summary, Func<string, string> newSessionDir)
    {
        return sta.Invoke("run_analysis", RunTimeout, delegate
        {
            SapSession s = Held();
            Checkpoint ck = WorkingCheckpoint(s, newSessionDir, "run_analysis");
            string dir = ck.Dir, name = ck.Name, cp = ck.File;
            SapRead rd = Reader(s);
            rd.Invalidate();
            SapResults res = new SapResults(s, rd);
            Dictionary<string, object> r = Json.O("checkpoint", name, "file", cp, "sessionDir", dir,
                "from", ck.Started ? "saved from " + ck.From : "edited state of " + name + ", run in place");
            if (ck.Adopted != null) r["adopted"] = "SAP2000 held " + ck.Adopted + " (not opened through open_model): saved as r000.sdb in a new session first; that file is not written";
            Dictionary<string, object> run;
            try { run = res.Run(cases); }
            catch (ArgumentException e) { throw new ToolError(e.Message); }
            foreach (KeyValuePair<string, object> kv in run) r[kv.Key] = kv.Value;
            log.Info("run_analysis " + cp + " (" + run["seconds"] + " s)");
            if (summary)
            {
                Stopwatch sw = Stopwatch.StartNew();
                string runDir = Path.Combine(dir, "runs", name);
                try
                {
                    Dictionary<string, object> maxima = res.SaveEnvelopes(runDir, null);
                    Dictionary<string, object> react = res.Reactions(null, DefaultTolerance);
                    string file = Path.Combine(runDir, "summary.json");
                    File.WriteAllText(file, Json.Write(Json.O("checkpoint", name, "file", cp, "utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv),
                        "units", s.Units(), "run", run, "maxima", maxima, "reactions", react)));
                    List<object> eq = new List<object>();
                    object cl;
                    if (react.TryGetValue("cases", out cl))
                        foreach (object o in (List<object>)cl)
                        {
                            Dictionary<string, object> d = (Dictionary<string, object>)o;
                            eq.Add(Json.O("case", d["case"], "step", d["step"], "check", d["check"]));
                        }
                    r["equilibrium"] = eq;
                    r["maxima"] = maxima;
                    r["summary"] = file;
                }
                catch (Exception e) { log.Error("run summary", e); r["summaryError"] = e.Message; }
                r["summarySeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
            }
            File.AppendAllText(Path.Combine(dir, "runs.jsonl"), Json.Write(Json.O("checkpoint", name, "utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv),
                "cases", cases, "seconds", run["seconds"], "notFinished", run.ContainsKey("notFinished") ? run["notFinished"] : null)) + "\n");
            r["units"] = s.Units();
            r["note"] = "SAP2000 now holds " + name + ".sdb with its results. Next: reactions_summary, query_results; compare_runs after another run.";
            return r;
        });
    }

    // ---------------------------------------------------------------- edits (phase 3)

    const int ReplyChanges = 50, LogChanges = 2000;

    // An edit of the model SAP holds, on the STA thread. Applied (not dryRun): into the working checkpoint (unlocked
    // first: SAP2000 refuses edits to a model with results), the SAP2000 view refreshed, the object index reset, and
    // a line appended to <session dir>\changes.jsonl (tool, arguments, changes). The reply lists at most 50 changes.
    public Dictionary<string, object> Edit(string tool, Dictionary<string, object> args, bool dryRun,
        Func<SapRead, SapEdit, Dictionary<string, object>> fn, Func<string, string> newSessionDir)
    {
        return sta.Invoke(tool, ResultsTimeout, delegate
        {
            SapSession s = Held();
            SapRead rd = Reader(s);
            Checkpoint ck = null;
            bool unlocked = false;
            if (!dryRun)
            {
                ck = WorkingCheckpoint(s, newSessionDir, tool);
                if (s.Model.GetModelIsLocked()) { s.Model.SetModelIsLocked(false); unlocked = true; }
                rd.Invalidate();
            }
            Dictionary<string, object> r;
            try { r = fn(rd, new SapEdit(s, rd)); }
            catch (ArgumentException e) { throw new ToolError(e.Message); }
            finally { if (!dryRun) rd.Invalidate(); }
            r["dryRun"] = dryRun;
            List<object> changes = r.ContainsKey("changes") ? r["changes"] as List<object> : null;
            bool refused = r.ContainsKey("applied") && false.Equals(r["applied"]);   // the edit's own checks stopped it
            if (refused) { r["checkpoint"] = ck.Name; r["dryRun"] = true; }
            else if (!dryRun)
            {
                s.Model.View.RefreshView(0, false);
                List<object> logged = changes == null || changes.Count <= LogChanges ? changes : changes.GetRange(0, LogChanges);
                Dictionary<string, object> line = Json.O("utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv), "checkpoint", ck.Name,
                    "tool", tool, "args", args, "edit", r.ContainsKey("edit") ? r["edit"] : null, "changed", changes == null ? 0 : changes.Count, "changes", logged);
                if (r.ContainsKey("failed")) line["failed"] = r["failed"];
                string reason = Json.Str(args, "reason");
                if (!string.IsNullOrEmpty(reason)) line["reason"] = reason.Trim();
                File.AppendAllText(Path.Combine(ck.Dir, "changes.jsonl"), Json.Write(line) + "\n");
                log.Info(tool + " -> " + ck.Name + " (" + (changes == null ? 0 : changes.Count) + " changes)");
                r["checkpoint"] = ck.Name;
                r["file"] = ck.File;
                string note = ck.Started ? "saved " + ck.From + " as " + ck.Name + " first; " + ck.From + " keeps its state and results. " : "";
                if (unlocked) note += "The model's results were cleared by the edit (the run checkpoints keep theirs). ";
                r["note"] = note + "run_analysis runs " + ck.Name + "; revert goes back.";
                if (ck.Adopted != null) r["adopted"] = "SAP2000 held " + ck.Adopted + " (not opened through open_model): saved as r000.sdb in a new session first";
            }
            else if (!refused) r["note"] = "dry run: nothing changed" + (s.Model.GetModelIsLocked() ? "; applying it clears the model's results (the run checkpoint keeps them)" : "");
            if (changes != null && changes.Count > ReplyChanges) { r["changes"] = changes.GetRange(0, ReplyChanges); r["changesListed"] = ReplyChanges; }
            return r;
        });
    }

    // Opens checkpoint rNNN of the current session (or of sessionDir) in SAP2000. Its results, if it was run, are
    // queryable again; the next edit or run starts a new checkpoint, so rNNN stays as it is.
    public Dictionary<string, object> Revert(string checkpoint, string sessionDir)
    {
        return sta.Invoke("revert", Long, delegate
        {
            SapSession s = Attached();
            string dir = sessionDir ?? SessionDirOf(s.ModelPath());
            if (dir == null) throw new ToolError("SAP2000 holds no Pluto session model: open_model first (or give session_dir)");
            string file = Path.Combine(dir, checkpoint + ".sdb");
            if (CheckpointNumber(file) < 0 || !File.Exists(file)) throw new ToolError("no checkpoint '" + checkpoint + "' in " + dir + " (list_checkpoints)");
            string before = s.ModelPath();
            s.Open(file, false);
            reader = null;
            List<string> done = new List<string>();
            foreach (KeyValuePair<string, int> kv in s.CaseStatus()) if (kv.Value == 4) done.Add(kv.Key);
            log.Info("revert -> " + file);
            return Json.O("checkpoint", checkpoint, "file", file, "replaced", before, "casesWithResults", done,
                "note", (done.Count > 0 ? "its results are available to query_results. " : "") + "The next edit or run_analysis saves a new checkpoint; " + checkpoint + " stays as it is.");
        });
    }

    // Next free checkpoint number in a session dir (r000 is the opened model).
    static int NextCheckpoint(string dir)
    {
        int next = 1;
        foreach (string f in Directory.GetFiles(dir, "r*.sdb"))
        {
            Match m = Regex.Match(Path.GetFileNameWithoutExtension(f), @"^r(\d+)$");
            if (m.Success) next = Math.Max(next, int.Parse(m.Groups[1].Value, Json.Inv) + 1);
        }
        return next;
    }

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
            Directory.CreateDirectory(sessionDir);
            string copy = Path.Combine(sessionDir, "r000.sdb");
            if (ext == ".sdb") { s.Open(path, false); s.SaveAs(copy); }
            else
            {
                // Importing a .s2k leaves that SAP2000 unable to import edited tables (ApplyEditedTables fails, then no
                // table reads) until it restarts (SAP 26.3, 2026-10-12). Import in a second SAP2000, save r000.sdb there,
                // close it, and open the .sdb here, so the user's SAP2000 never imports a text file.
                SapSession helper = SapSession.NewInstance(sapExe);
                try { helper.Open(path, false); helper.SaveAs(copy); }
                finally { helper.Close(); }
                s.Open(copy, false);
            }
            reader = null;
            int[] c = s.Counts();
            WriteSession(sessionDir, path, s.Version(), copy, null);
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

    // Hand-check fixtures (SapFixtures: plate and one-mass cantilever, built, edited, run and read back through the
    // tools' code) in a second SAP2000, always: building a model replaces what SAP holds. The result is also written to
    // file (fixtures.json in the work dir), which write_design_record cites as the tool verification.
    public Dictionary<string, object> Fixtures(string dir, string file)
    {
        return sta.Invoke("self_test fixtures", Long, delegate
        {
            Stopwatch sw = Stopwatch.StartNew();
            SapSession s = SapSession.NewInstance(sapExe);
            try
            {
                string version = s.Version();
                List<Dictionary<string, object>> checks = SapFixtures.Run(s, dir);
                int failed = checks.FindAll(delegate(Dictionary<string, object> c) { return !(bool)c["pass"]; }).Count;
                Dictionary<string, object> r = Json.O("passed", failed == 0, "program", "SAP2000", "version", version,
                    "utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv), "units", "kip, ft, s",
                    "checks", checks, "models", dir, "totalSeconds", Math.Round(sw.Elapsed.TotalSeconds, 1));
                File.WriteAllText(file, Json.Write(r));
                r["file"] = file;
                r["instance"] = "new (closed afterwards)";
                return r;
            }
            finally { s.Close(); }
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

    // Never closes the SAP2000 it attached to; an own instance (PLUTO_SAP_INSTANCE=new) is closed.
    public void Dispose()
    {
        if (OwnInstance && session != null) { try { sta.Invoke("close", Quick, delegate { session.Close(); return 0; }); } catch (Exception) { } }
        sta.Dispose();
    }
}
