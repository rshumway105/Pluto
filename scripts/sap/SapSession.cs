// The one SAP2000 controller: attach or start, open, save, run, results. Every SAP OAPI call in
// Pluto goes through this class or a class built on it (SapSurvey, SapRelease, SapExport); Python
// never talks to SAP (pythonTools call scripts\sap\Run-Sap.ps1). Capabilities: vault/arms/sap-controller.
// C# 5, compiled by Add-Type under Windows PowerShell 5.1 against SAP2000v1.dll (Import-SapApi.ps1).
//
// Facts (SAP 22 production, SAP 25 / 26 development):
//  * SAP 26 imports NOTHING from a .s2k stamped Version=22.0.0 while OpenFile still returns 0:
//    stamp generated files with Version() of the attached SAP.
//  * RunAnalysis needs a saved model, and saves it: run a copy when the opened file must stay untouched.
//  * Results.* return full doubles; DatabaseTables.GetTableForDisplayArray rounds to the display
//    format: never use it for results.
//  * Results.JointDispl reports in the joint LOCAL axes (same as SAP's table export).
//  * Nonlinear static output option 2 (step-by-step): the default envelope rows have principals and
//    von Mises zeroed; step-by-step gives one row per saved step (verified 26.3.0, 2026-09-08).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SAP2000v1;

public class SapSession
{
    public const string ProgId = "CSI.SAP2000.API.SapObject";
    protected static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public cOAPI Sap;
    public cSapModel Model;
    public bool Started;                      // true = this process launched SAP (Close exits it)
    public int ProcessId;                     // the SAP2000 process when this session started it (else 0)
    public double OpenSeconds;
    public List<string> Warnings = new List<string>();

    // ---------------------------------------------------------------- lifecycle

    // Attach to the running SAP2000, else start the one at exePath (attachOnly: fail instead).
    public static SapSession AttachOrStart(string exePath, bool attachOnly)
    {
        SapSession s = new SapSession();
        s.Attach(exePath, attachOnly);
        return s;
    }

    // A second SAP2000 whatever is running (GetActiveObject only ever finds the first).
    public static SapSession NewInstance(string exePath)
    {
        SapSession s = new SapSession();
        s.Start(exePath);
        return s;
    }

    // Attach to one SAP2000 process (GetActiveObject only finds the first registered one).
    public static SapSession AttachProcess(int pid)
    {
        SapSession s = new SapSession();
        cHelper helper = new Helper();
        s.Sap = helper.GetObjectProcess(ProgId, pid);
        if (s.Sap == null) throw new Exception("no SAP2000 API object in process " + pid);
        s.Model = s.Sap.SapModel;
        return s;
    }

    protected void Attach(string exePath, bool attachOnly)
    {
        try { Sap = (cOAPI)Marshal.GetActiveObject(ProgId); Model = Sap.SapModel; }
        catch (COMException)
        {
            if (attachOnly) throw new Exception("No running SAP2000 to attach to: open the model in SAP first.");
            Start(exePath);
        }
    }

    protected void Start(string exePath)
    {
        HashSet<int> before = new HashSet<int>();
        foreach (Process p in Process.GetProcessesByName("SAP2000")) before.Add(p.Id);
        cHelper helper = new Helper();
        Sap = helper.CreateObject(exePath);
        Check(Sap.ApplicationStart(), "ApplicationStart");
        Sap.Visible();
        Started = true;
        Model = Sap.SapModel;
        foreach (Process p in Process.GetProcessesByName("SAP2000")) if (!before.Contains(p.Id)) ProcessId = p.Id;
    }

    // Exits SAP only if this process started it.
    public void Close() { if (Started) Sap.ApplicationExit(false); }

    public string Version() { string v = ""; double n = 0; Check(Model.GetVersion(ref v, ref n), "GetVersion"); return v; }

    // Present units as SAP's CurrUnits string ("Kip, ft, F"), as written in PROGRAM CONTROL.
    public string Units() { return UnitsName(Model.GetPresentUnits()); }

    public static string UnitsName(eUnits u)
    {
        switch ((int)u)
        {
            case 1: return "Lb, in, F"; case 2: return "Lb, ft, F"; case 3: return "Kip, in, F"; case 4: return "Kip, ft, F";
            case 5: return "KN, mm, C"; case 6: return "KN, m, C"; case 7: return "Kgf, mm, C"; case 8: return "Kgf, m, C";
            case 9: return "N, mm, C"; case 10: return "N, m, C"; case 11: return "Ton, mm, C"; case 12: return "Ton, m, C";
            case 13: return "KN, cm, C"; case 14: return "Kgf, cm, C"; case 15: return "N, cm, C"; case 16: return "Ton, cm, C";
        }
        return "Kip, ft, F";
    }

    public string ModelPath() { return Model.GetModelFilename(true); }

    // ---------------------------------------------------------------- model

    // Open a .sdb or import a .s2k. saveSdb: an imported .s2k is saved beside it as .sdb (RunAnalysis
    // needs a saved model). Returns the file SAP now holds.
    public string Open(string path, bool saveSdb)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Check(Model.File.OpenFile(path), "OpenFile " + path);
        string held = path;
        if (saveSdb && !path.EndsWith(".sdb", StringComparison.OrdinalIgnoreCase))
        {
            held = Path.ChangeExtension(path, ".sdb");
            Check(Model.File.Save(held), "Save " + held);
        }
        OpenSeconds = sw.Elapsed.TotalSeconds;
        return held;
    }

    public void SaveAs(string path) { Check(Model.File.Save(path), "Save " + path); }

    // Runs every case flagged to run. Returns seconds.
    public double Run()
    {
        Stopwatch sw = Stopwatch.StartNew();
        Check(Model.Analyze.RunAnalysis(), "RunAnalysis");
        return sw.Elapsed.TotalSeconds;
    }

    // Save AS copyPath (the opened file is never written), flag every case, run. Returns a report
    // naming the cases that did not finish (e.g. staged construction without the licence).
    public string RunOnCopy(string copyPath)
    {
        SaveAs(copyPath);
        Check(Model.Analyze.SetRunCaseFlag("", true, true), "SetRunCaseFlag");
        double secs = Run();
        List<string> done = new List<string>(), left = new List<string>();
        foreach (KeyValuePair<string, int> kv in CaseStatus()) (kv.Value == 4 ? done : left).Add(kv.Key);
        string r = F("working copy {0}; finished {1} ({2:0}s)", Path.GetFileName(copyPath), string.Join(", ", done.ToArray()), secs);
        if (left.Count > 0) r += "; NOT RUN: " + string.Join(", ", left.ToArray());
        return r;
    }

    // Case -> status (1 not run, 2 could not start, 3 not finished, 4 finished).
    public List<KeyValuePair<string, int>> CaseStatus()
    {
        int n = 0; string[] names = null; int[] st = null;
        Check(Model.Analyze.GetCaseStatus(ref n, ref names, ref st), "GetCaseStatus");
        List<KeyValuePair<string, int>> r = new List<KeyValuePair<string, int>>();
        for (int i = 0; i < n; i++) r.Add(new KeyValuePair<string, int>(names[i], st[i]));
        return r;
    }

    public bool AllCasesFinished() { foreach (KeyValuePair<string, int> kv in CaseStatus()) if (kv.Value != 4) return false; return true; }

    public string[] LoadCases() { int n = 0; string[] a = null; Model.LoadCases.GetNameList(ref n, ref a); return a ?? new string[0]; }
    public string[] Groups() { int n = 0; string[] a = null; Model.GroupDef.GetNameList(ref n, ref a); return a ?? new string[0]; }

    // Objects: points, areas, frames, links.
    public int[] Counts() { return new int[] { Model.PointObj.Count(), Model.AreaObj.Count(), Model.FrameObj.Count(), Model.LinkObj.Count() }; }

    // Direction cosines of an area's local 1 axis in global coordinates. GetTransformationMatrix is
    // column-major: local 1 = elements 0, 3, 6 (SAP 26, 2026-09-04: a wall shell rotated 90 deg
    // reads (0, 0, 1), a baseplate shell reads radial).
    public double[] AreaLocal1(string area)
    {
        double[] v = new double[9];
        Check(Model.AreaObj.GetTransformationMatrix(area, ref v, true), "AreaObj.GetTransformationMatrix " + area);
        return new double[] { v[0], v[3], v[6] };
    }

    // Area<TAB>L1x<TAB>L1y<TAB>L1z for every area object.
    public int WriteAreaLocal1(string path)
    {
        int n = 0; string[] names = null;
        Model.AreaObj.GetNameList(ref n, ref names);
        StringBuilder sb = new StringBuilder("Area\tL1x\tL1y\tL1z\r\n");
        for (int i = 0; i < n; i++)
        {
            double[] v = AreaLocal1(names[i]);
            sb.Append(names[i]).Append('\t').Append(R(v[0])).Append('\t').Append(R(v[1])).Append('\t').Append(R(v[2])).Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString());
        return n;
    }

    // DatabaseTables is bound late: the SAP 22 interop on the production machine has no
    // cSapModel.DatabaseTables, and one missing member stops every Sap*.cs from compiling (2026-10-02).
    // Without it, TableRows returns nothing and HasDatabaseTables says so.
    object dbTables; bool dbChecked;
    public bool HasDatabaseTables { get { return DbTables() != null; } }
    object DbTables()
    {
        if (dbChecked) return dbTables;
        dbChecked = true;
        System.Reflection.PropertyInfo p = typeof(cSapModel).GetProperty("DatabaseTables");
        if (p != null) dbTables = p.GetValue(Model, null);
        if (dbTables == null) Warn("this SAP2000 API has no DatabaseTables: input tables (section properties, insertion points, coordinate systems) are not read back");
        return dbTables;
    }

    // Rows of a DatabaseTables input table (rounded to the display format: input tables only, never
    // results). Empty tables come back with null field names (stale buffers), hence the guard.
    public List<Dictionary<string, string>> TableRows(string table) { return TableRows(table, ""); }

    // group: only the rows of that group's objects ("" = all).
    public List<Dictionary<string, string>> TableRows(string table, string group)
    {
        List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
        object db = DbTables();
        if (db == null) return rows;
        SelectAllForDisplay();
        Type it = typeof(cSapModel).Assembly.GetType("SAP2000v1.cDatabaseTables");
        System.Reflection.MethodInfo mi = it == null ? null : it.GetMethod("GetTableForDisplayArray");
        if (mi == null) return rows;
        object[] a = { table, new string[0], group ?? "", 0, null, 0, null };
        if ((int)mi.Invoke(db, a) != 0) return rows;
        string[] fields = (string[])a[4], data = (string[])a[6]; int nrec = (int)a[5];
        if (nrec == 0 || fields == null || fields.Length == 0) return rows;
        foreach (string f in fields) if (f == null) return rows;
        int nf = fields.Length;
        for (int r = 0; r < nrec; r++)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            for (int c = 0; c < nf; c++) { string v = data[r * nf + c]; if (v != null) d[fields[c]] = v; }
            rows.Add(d);
        }
        return rows;
    }

    // Load tables (pattern, case and combination definitions and the loads per pattern) list only the patterns, cases
    // and combinations selected for display, and through the API none is selected: "Load Pattern Definitions" read
    // back 0 rows while it held data (SAP 26.3, 2026-10-12). Selects all of them, for reading and editing tables.
    public void SelectAllForDisplay()
    {
        object db = DbTables();
        if (db == null) return;
        Type it = typeof(cSapModel).Assembly.GetType("SAP2000v1.cDatabaseTables");
        int n = 0; string[] pats = null, combos = null;
        Model.LoadPatterns.GetNameList(ref n, ref pats);
        n = 0; Model.RespCombo.GetNameList(ref n, ref combos);
        string[][] lists = { pats ?? new string[0], LoadCases(), combos ?? new string[0] };
        string[] setters = { "SetLoadPatternsSelectedForDisplay", "SetLoadCasesSelectedForDisplay", "SetLoadCombinationsSelectedForDisplay" };
        for (int k = 0; k < setters.Length; k++)
        {
            System.Reflection.MethodInfo mi = it == null ? null : it.GetMethod(setters[k]);
            if (mi != null && lists[k].Length > 0) mi.Invoke(db, new object[] { lists[k] });
        }
    }

    // Input tables that hold data in this model, as { key, name } (GetAllTables, IsEmpty false); null without
    // DatabaseTables.
    public List<string[]> AvailableTables()
    {
        object db = DbTables();
        if (db == null) return null;
        Type it = typeof(cSapModel).Assembly.GetType("SAP2000v1.cDatabaseTables");
        System.Reflection.MethodInfo mi = it == null ? null : it.GetMethod("GetAllTables");
        if (mi == null) return null;
        object[] a = { 0, null, null, null, null };
        List<string[]> r = new List<string[]>();
        if ((int)mi.Invoke(db, a) != 0) return r;
        string[] keys = (string[])a[1], names = (string[])a[2]; bool[] empty = (bool[])a[4];
        for (int i = 0; keys != null && i < keys.Length; i++)
            if (empty == null || !empty[i]) r.Add(new string[] { keys[i], names != null && i < names.Length ? names[i] : keys[i] });
        return r;
    }

    // Input tables read back as .s2k text (display-rounded: for echoing what an import produced, e.g.
    // "Frame Insertion Point Assignments", "Link Property Definitions 05 - Gap"). Returns rows per table;
    // a table that does not exist or is empty is written as a comment line.
    public Dictionary<string, int> WriteTablesS2k(string path, string[] tables)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>();
        StringBuilder sb = new StringBuilder();
        sb.Append("File generated by SapSession.WriteTablesS2k (SAP2000 input tables, display precision)\n\n");
        foreach (string t in tables)
        {
            List<Dictionary<string, string>> rows = TableRows(t.Trim());
            counts[t.Trim()] = rows.Count;
            if (rows.Count == 0) { sb.Append("$ no rows: ").Append(t.Trim()).Append("\n\n"); continue; }
            sb.Append("TABLE:  \"").Append(t.Trim().ToUpperInvariant()).Append("\"\n");
            foreach (Dictionary<string, string> r in rows)
            {
                foreach (KeyValuePair<string, string> kv in r) sb.Append("   ").Append(kv.Key).Append('=').Append(Q(kv.Value));
                sb.Append('\n');
            }
            sb.Append('\n');
        }
        sb.Append("END TABLE DATA\n");
        File.WriteAllText(path, sb.ToString());
        return counts;
    }

    // ---------------------------------------------------------------- results

    // Every load case selected for output, nonlinear static step-by-step (see the facts above).
    public string[] SelectAllCases()
    {
        cAnalysisResultsSetup setup = Model.Results.Setup;
        setup.DeselectAllCasesAndCombosForOutput();
        setup.SetOptionNLStatic(2);
        string[] cases = LoadCases();
        foreach (string c in cases) setup.SetCaseSelectedForOutput(c);
        return cases;
    }

    // Result tables as .s2k text at full precision, the tables SapToPluto reads plus ELEMENT FORCES -
    // LINKS. ids == null: objects keep their SAP names (generated models, where objects are the mesh).
    // ids != null: analysis-mesh element names mapped to 1..N (SapExport.Mesh). A case with several
    // saved steps becomes "<case>.<step>" per step, or only its last step with finalOnly.
    public Dictionary<string, int> WriteResultsS2k(string path, IResultIds ids, bool finalOnly, string source)
    {
        SelectAllCases();
        Dictionary<string, int> counts = new Dictionary<string, int>();
        using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.NewLine = "\n";
            w.WriteLine("File generated by " + source + " (SAP2000 OAPI results" + (ids == null ? "" : ", analysis mesh") + ")");
            w.WriteLine();
            w.WriteLine("TABLE:  \"PROGRAM CONTROL\"");
            w.WriteLine("   ProgramName=SAP2000   Version=" + Version() + "   CurrUnits=\"" + Units() + "\"");
            w.WriteLine();
            eItemTypeElm all = eItemTypeElm.GroupElm;
            {   // joints
                int n = 0; string[] obj = null, elm = null, cas = null, st = null; double[] sn = null, u1 = null, u2 = null, u3 = null, r1 = null, r2 = null, r3 = null;
                Check(Model.Results.JointDispl("ALL", all, ref n, ref obj, ref elm, ref cas, ref st, ref sn, ref u1, ref u2, ref u3, ref r1, ref r2, ref r3), "JointDispl");
                Emit(w, counts, "JOINT DISPLACEMENTS", n, cas, sn, finalOnly, delegate(int i, string c)
                {
                    string j = ids == null ? obj[i] : ids.Point(elm[i]);
                    if (j == null) return null;
                    return "Joint=" + j + "   OutputCase=" + Q(c) + "   CaseType=LinStatic" + Nums(i, new string[] { "U1", "U2", "U3", "R1", "R2", "R3" }, u1, u2, u3, r1, r2, r3);
                });
            }
            if (Model.AreaObj.Count() > 0)
            {
                int n = 0; string[] obj = null, elm = null, pe = null, cas = null, st = null;
                double[] sn = null, f11 = null, f22 = null, f12 = null, fmax = null, fmin = null, fang = null, fvm = null,
                    m11 = null, m22 = null, m12 = null, mmax = null, mmin = null, mang = null, v13 = null, v23 = null, vmax = null, vang = null;
                Check(Model.Results.AreaForceShell("ALL", all, ref n, ref obj, ref elm, ref pe, ref cas, ref st, ref sn, ref f11, ref f22, ref f12,
                    ref fmax, ref fmin, ref fang, ref fvm, ref m11, ref m22, ref m12, ref mmax, ref mmin, ref mang, ref v13, ref v23, ref vmax, ref vang), "AreaForceShell");
                Emit(w, counts, "ELEMENT FORCES - AREA SHELLS", n, cas, sn, finalOnly, delegate(int i, string c)
                {
                    string lead = ShellLead(ids, obj[i], elm[i], pe[i], c);
                    if (lead == null) return null;
                    return lead + Nums(i, new string[] { "F11", "F22", "F12", "FMax", "FMin", "FAngle", "FVM", "M11", "M22", "M12", "MMax", "MMin", "MAngle", "V13", "V23", "VMax", "VAngle" },
                        f11, f22, f12, fmax, fmin, fang, fvm, m11, m22, m12, mmax, mmin, mang, v13, v23, vmax, vang);
                });
                n = 0; obj = null; elm = null; pe = null; cas = null; st = null; sn = null;
                double[] s11t = null, s22t = null, s12t = null, smaxt = null, smint = null, sangt = null, svmt = null,
                    s11b = null, s22b = null, s12b = null, smaxb = null, sminb = null, sangb = null, svmb = null, s13 = null, s23 = null, smaxa = null, sanga = null;
                Check(Model.Results.AreaStressShell("ALL", all, ref n, ref obj, ref elm, ref pe, ref cas, ref st, ref sn, ref s11t, ref s22t, ref s12t, ref smaxt,
                    ref smint, ref sangt, ref svmt, ref s11b, ref s22b, ref s12b, ref smaxb, ref sminb, ref sangb, ref svmb, ref s13, ref s23, ref smaxa, ref sanga), "AreaStressShell");
                Emit(w, counts, "ELEMENT STRESSES - AREA SHELLS", n, cas, sn, finalOnly, delegate(int i, string c)
                {
                    string lead = ShellLead(ids, obj[i], elm[i], pe[i], c);
                    if (lead == null) return null;
                    return lead + Nums(i, new string[] { "S11Top", "S22Top", "S12Top", "SMaxTop", "SMinTop", "SAngleTop", "SVMTop", "S11Bot", "S22Bot", "S12Bot",
                        "SMaxBot", "SMinBot", "SAngleBot", "SVMBot", "S13Avg", "S23Avg", "SMaxAvg", "SAngleAvg" },
                        s11t, s22t, s12t, smaxt, smint, sangt, svmt, s11b, s22b, s12b, smaxb, sminb, sangb, svmb, s13, s23, smaxa, sanga);
                });
            }
            if (Model.FrameObj.Count() > 0)
            {
                int n = 0; string[] obj = null, elm = null, cas = null, st = null; double[] osta = null, esta = null, sn = null, p = null, v2 = null, v3 = null, t = null, m2 = null, m3 = null;
                Check(Model.Results.FrameForce("ALL", all, ref n, ref obj, ref osta, ref elm, ref esta, ref cas, ref st, ref sn, ref p, ref v2, ref v3, ref t, ref m2, ref m3), "FrameForce");
                string[] names = { "P", "V2", "V3", "T", "M2", "M3" };
                Emit(w, counts, "ELEMENT FORCES - FRAMES", n, cas, sn, finalOnly, delegate(int i, string c)
                {
                    if (ids == null)
                        return "Frame=" + obj[i] + "   Station=" + R(osta[i]) + "   OutputCase=" + Q(c) + "   CaseType=LinStatic" + (string.IsNullOrEmpty(st[i]) ? "" : "   StepType=" + Q(st[i]))
                            + Nums(i, names, p, v2, v3, t, m2, m3) + "   FrameElem=" + elm[i] + "   ElemStation=" + R(esta[i]);
                    string f = ids.Line(elm[i]);
                    if (f == null) return null;
                    return "Frame=" + f + "   Station=" + R(esta[i]) + "   OutputCase=" + Q(c) + "   CaseType=LinStatic" + Nums(i, names, p, v2, v3, t, m2, m3);
                });
            }
            if (Model.LinkObj.Count() > 0)
            {
                int n = 0; string[] obj = null, elm = null, pe = null, cas = null, st = null; double[] sn = null, p = null, v2 = null, v3 = null, t = null, m2 = null, m3 = null;
                Check(Model.Results.LinkForce("ALL", all, ref n, ref obj, ref elm, ref pe, ref cas, ref st, ref sn, ref p, ref v2, ref v3, ref t, ref m2, ref m3), "LinkForce");
                Emit(w, counts, "ELEMENT FORCES - LINKS", n, cas, sn, finalOnly, delegate(int i, string c)
                {
                    string l = ids == null ? obj[i] : ids.Link(elm[i]);
                    string j = ids == null ? pe[i] : ids.Point(pe[i]);
                    if (l == null || j == null) return null;
                    return "Link=" + l + "   Joint=" + j + "   OutputCase=" + Q(c) + "   CaseType=LinStatic" + Nums(i, new string[] { "P", "V2", "V3", "T", "M2", "M3" }, p, v2, v3, t, m2, m3);
                });
            }
            w.WriteLine("END TABLE DATA");
        }
        return counts;
    }

    static string ShellLead(IResultIds ids, string obj, string elm, string pointElm, string c)
    {
        string a = ids == null ? obj : ids.Area(elm), ae = ids == null ? elm : a, j = ids == null ? pointElm : ids.Point(pointElm);
        if (a == null || j == null) return null;
        return "Area=" + a + "   AreaElem=" + ae + "   ShellType=Shell-Thin   Joint=" + j + "   OutputCase=" + Q(c) + "   CaseType=LinStatic";
    }

    delegate string RowFn(int i, string caseName);

    // One table. Cases with several saved steps (staged construction) become "<case>.<step>" per
    // step, or only their last step under the bare name with finalOnly; single-step cases keep their name.
    static void Emit(StreamWriter w, Dictionary<string, int> counts, string title, int n, string[] cases, double[] steps, bool finalOnly, RowFn row)
    {
        if (n == 0) return;
        Dictionary<string, HashSet<double>> seen = new Dictionary<string, HashSet<double>>();
        for (int i = 0; i < n; i++)
        {
            HashSet<double> s;
            if (!seen.TryGetValue(cases[i], out s)) { s = new HashSet<double>(); seen[cases[i]] = s; }
            s.Add(steps[i]);
        }
        Dictionary<string, double> multi = new Dictionary<string, double>();
        foreach (KeyValuePair<string, HashSet<double>> kv in seen)
            if (kv.Value.Count > 1)
            {
                double mx = double.MinValue; foreach (double d in kv.Value) mx = Math.Max(mx, d);
                multi[kv.Key] = mx;
                counts["  " + kv.Key + " steps"] = kv.Value.Count;
            }
        List<string> lines = new List<string>();
        for (int i = 0; i < n; i++)
        {
            string c = cases[i];
            double last;
            if (multi.TryGetValue(c, out last))
            {
                if (finalOnly) { if (steps[i] != last) continue; }
                else c = c + "." + ((int)steps[i]).ToString(Inv);
            }
            string line = row(i, c);
            if (line == null) continue;
            lines.Add("   " + line);
        }
        if (lines.Count == 0) return;   // e.g. links on a mesh export: no viewer domain, no table
        counts[title] = lines.Count;
        w.WriteLine("TABLE:  \"" + title + "\"");
        foreach (string l in lines) w.WriteLine(l);
        w.WriteLine();
    }

    static string Nums(int i, string[] names, params double[][] cols)
    {
        StringBuilder sb = new StringBuilder();
        for (int k = 0; k < names.Length; k++) sb.Append("   ").Append(names[k]).Append('=').Append(R(cols[k][i]));
        return sb.ToString();
    }

    // ---------------------------------------------------------------- self test

    // Simply supported beam, kip-ft: span L, point load P at midspan (frames 1-2, load at joint 2).
    // Hand check: R = P/2 at each end, M = P L / 4 at joint 2. version: Version() of the SAP that reads it.
    public static void WriteTestBeamS2k(string path, string version, double L, double P)
    {
        string[] lines = {
            "File generated by SapSession.cs (Pluto self test)", "",
            "TABLE:  \"PROGRAM CONTROL\"",
            "   ProgramName=SAP2000   Version=" + version + "   CurrUnits=\"Kip, ft, F\"   MergeTol=0.001", "",
            "TABLE:  \"MATERIAL PROPERTIES 01 - GENERAL\"",
            "   Material=STEEL   Type=Steel   SymType=Isotropic   TempDepend=No   Color=Cyan", "",
            "TABLE:  \"MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES\"",
            "   Material=STEEL   UnitWeight=0.49   UnitMass=0.0152   E1=4176000   G12=1606154   U12=0.3   A1=6.5e-06", "",
            "TABLE:  \"FRAME SECTION PROPERTIES 01 - GENERAL\"",
            "   SectionName=BEAM   Material=STEEL   Shape=Rectangular   t3=2   t2=1   Color=Yellow", "",
            "TABLE:  \"JOINT COORDINATES\"",
            "   Joint=1   CoordSys=GLOBAL   CoordType=Cartesian   XorR=0   Y=0   Z=0",
            F("   Joint=2   CoordSys=GLOBAL   CoordType=Cartesian   XorR={0}   Y=0   Z=0", L / 2),
            F("   Joint=3   CoordSys=GLOBAL   CoordType=Cartesian   XorR={0}   Y=0   Z=0", L), "",
            "TABLE:  \"CONNECTIVITY - FRAME\"",
            "   Frame=1   JointI=1   JointJ=2   IsCurved=No",
            "   Frame=2   JointI=2   JointJ=3   IsCurved=No", "",
            "TABLE:  \"FRAME SECTION ASSIGNMENTS\"",
            "   Frame=1   SectionType=Rectangular   AutoSelect=N.A.   AnalSect=BEAM   DesignSect=BEAM   MatProp=Default",
            "   Frame=2   SectionType=Rectangular   AutoSelect=N.A.   AnalSect=BEAM   DesignSect=BEAM   MatProp=Default", "",
            "TABLE:  \"JOINT RESTRAINT ASSIGNMENTS\"",
            "   Joint=1   U1=Yes   U2=Yes   U3=Yes   R1=Yes   R2=No   R3=No",
            "   Joint=3   U1=No   U2=Yes   U3=Yes   R1=Yes   R2=No   R3=No", "",
            "TABLE:  \"LOAD PATTERN DEFINITIONS\"",
            "   LoadPat=LIVE   DesignType=Live   SelfWtMult=0", "",
            "TABLE:  \"JOINT LOADS - FORCE\"",
            F("   Joint=2   LoadPat=LIVE   CoordSys=GLOBAL   F1=0   F2=0   F3={0}   M1=0   M2=0   M3=0", -P), "",
            "TABLE:  \"LOAD CASE DEFINITIONS\"",
            "   Case=LIVE   Type=LinStatic   InitialCond=Zero", "",
            "TABLE:  \"CASE - STATIC 1 - LOAD ASSIGNMENTS\"",
            "   Case=LIVE   LoadType=\"Load pattern\"   LoadName=LIVE   LoadSF=1", "",
            "END TABLE DATA", ""
        };
        File.WriteAllText(path, string.Join("\r\n", lines));
    }

    // ---------------------------------------------------------------- helpers

    // Round-trip number text: "G17", always exact. .NET Framework's "R" drops the last digit of some
    // values, and its double.Parse is not correctly rounded either, so "R" cannot be checked in-process.
    public static string R(double v) { return v.ToString("G17", Inv); }
    // .s2k value quoting: spaces, commas and empty values are quoted.
    public static string Q(string v) { v = v ?? ""; return (v.Length == 0 || v.IndexOf(' ') >= 0 || v.IndexOf(',') >= 0) ? "\"" + v + "\"" : v; }
    protected static string F(string fmt, params object[] args) { return string.Format(Inv, fmt, args); }
    protected static void Check(int ret, string what) { if (ret != 0) throw new Exception(what + " returned " + ret); }
    protected void Warn(string w) { Warnings.Add(w); }
}

// Maps analysis-mesh element names to the 1..N ids written to the .s2k (null = not carried).
public interface IResultIds
{
    string Point(string pointElm);
    string Area(string areaElm);
    string Line(string lineElm);
    string Link(string linkElm);
}
