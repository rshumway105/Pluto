// Analysis results of the model SAP2000 holds, as plain dictionaries / lists (JSON-ready): run with the
// analysis log's warnings, result rows at full precision (Results.*), top-N / envelope / per-case / per-group
// queries, base reactions against the applied load (equilibrium check), and per-run envelopes (TSV) that
// compare_runs reads. Used by the Pluto MCP server (scripts\mcp). Values are in the model's present units.
// Joint displacements and reactions are in joint local axes (global unless a joint's axes are rotated);
// frame, area and link forces in element local axes. C# 5 (see SapSession.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SAP2000v1;

public class SapResults
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] CaseStates = { "?", "not run", "could not start", "not finished", "finished" };

    public static readonly string[] ResultKinds = { "joint", "reaction", "frame", "area", "area_stress", "link" };
    static readonly string[] JointQ = { "U1", "U2", "U3", "R1", "R2", "R3", "U" };
    static readonly string[] ReactionQ = { "F1", "F2", "F3", "M1", "M2", "M3" };
    static readonly string[] ForceQ = { "P", "V2", "V3", "T", "M2", "M3" };
    static readonly string[] AreaQ = { "F11", "F22", "F12", "FMax", "FMin", "FVM", "M11", "M22", "M12", "MMax", "MMin", "V13", "V23", "VMax" };
    static readonly string[] AreaStressQ = { "S11Top", "S22Top", "S12Top", "SMaxTop", "SMinTop", "SVMTop", "S11Bot", "S22Bot", "S12Bot",
        "SMaxBot", "SMinBot", "SVMBot", "S13Avg", "S23Avg", "SMaxAvg" };

    readonly SapSession s;
    readonly SapRead read;

    public SapResults(SapSession session, SapRead reader) { s = session; read = reader; }
    cSapModel M { get { return s.Model; } }

    // Quantities of a result kind, in the order Fetch fills Row.V. U = |(U1, U2, U3)|.
    public static string[] Quantities(string kind)
    {
        switch (kind)
        {
            case "joint": return JointQ;
            case "reaction": return ReactionQ;
            case "frame": case "link": return ForceQ;
            case "area": return AreaQ;
            case "area_stress": return AreaStressQ;
        }
        throw new ArgumentException("result kind must be " + string.Join(", ", ResultKinds) + " (got '" + kind + "')");
    }

    // The object kind a result kind belongs to (for groups and selections).
    public static string ObjectKind(string kind) { return kind == "reaction" ? "joint" : kind == "area_stress" ? "area" : kind; }

    // ---------------------------------------------------------------- run

    // Runs the given cases (null: every case) on the file SAP holds; RunAnalysis saves it first. The initial
    // cases they continue from are run too. Returns seconds, each case's status, and the WARNING / ERROR lines of
    // SAP's analysis log beside the model.
    public Dictionary<string, object> Run(IList<string> cases)
    {
        List<string> flagged = new List<string>();
        if (cases == null) Check(M.Analyze.SetRunCaseFlag("", true, true), "SetRunCaseFlag");
        else
        {
            List<string> all = new List<string>(s.LoadCases());
            foreach (string c in cases)
            {
                if (!all.Contains(c)) throw new ArgumentException("no load case '" + c + "' (cases: " + string.Join(", ", all.ToArray()) + ")");
                for (string k = c; k.Length > 0 && !flagged.Contains(k); k = InitialCase(k)) flagged.Add(k);
            }
            Check(M.Analyze.SetRunCaseFlag("", false, true), "SetRunCaseFlag");
            foreach (string c in flagged) Check(M.Analyze.SetRunCaseFlag(c, true, false), "SetRunCaseFlag " + c);
        }
        Stopwatch sw = Stopwatch.StartNew();
        int ret = M.Analyze.RunAnalysis();
        double secs = sw.Elapsed.TotalSeconds;

        List<object> status = new List<object>();
        List<string> notFinished = new List<string>();
        foreach (KeyValuePair<string, int> kv in s.CaseStatus())
        {
            if (cases != null && !flagged.Contains(kv.Key)) continue;
            string st = kv.Value >= 1 && kv.Value <= 4 ? CaseStates[kv.Value] : kv.Value.ToString(Inv);
            status.Add(O("name", kv.Key, "status", st));
            if (kv.Value != 4) notFinished.Add(kv.Key);
        }
        Dictionary<string, object> r = O("seconds", Math.Round(secs, 1), "cases", status);
        if (ret != 0) r["runAnalysisReturned"] = ret;
        if (notFinished.Count > 0) r["notFinished"] = notFinished;
        string log = Path.ChangeExtension(s.ModelPath(), ".LOG");
        if (File.Exists(log)) { r["log"] = log; r["logMessages"] = LogMessages(log, 40); }
        return r;
    }

    // Initial case of a static case ("" = zero initial conditions, or not a static case).
    string InitialCase(string c)
    {
        eLoadCaseType t = eLoadCaseType.LinearStatic; int sub = 0; string init = "";
        M.LoadCases.GetTypeOAPI(c, ref t, ref sub);
        if (t == eLoadCaseType.LinearStatic) M.LoadCases.StaticLinear.GetInitialCase(c, ref init);
        else if (t == eLoadCaseType.NonlinearStatic) M.LoadCases.StaticNonlinear.GetInitialCase(c, ref init);
        return init ?? "";
    }

    // Distinct WARNING / ERROR / instability lines of an analysis log, first `cap`, with the total.
    static Dictionary<string, object> LogMessages(string path, int cap)
    {
        List<string> lines = new List<string>();
        int total = 0;
        string[] keys = { "WARNING", "ERROR", "UNSTABLE", "ILL-CONDITIONED", "LOST", "NEGATIVE" };
        foreach (string raw in File.ReadAllLines(path))
        {
            string l = raw.Trim(), up = l.ToUpperInvariant();
            bool hit = false;
            foreach (string k in keys) if (up.Contains(k)) { hit = true; break; }
            if (!hit || up.Replace(" ", "").Contains(",OK") || up.Contains("SHOULD BE ZERO") || up.StartsWith("NEGATIVE ITERATIONS")) continue;   // SAP's all-clear lines
            total++;
            if (lines.Count < cap && !lines.Contains(l)) lines.Add(l);
        }
        return O("total", total, "lines", lines);
    }

    // ---------------------------------------------------------------- cases for output

    public string[] Combos() { int n = 0; string[] a = null; M.RespCombo.GetNameList(ref n, ref a); return a ?? new string[0]; }

    // Names or * / ? patterns over cases and combos; null: every finished case and every combo.
    public List<string> ResolveCases(IList<string> patterns)
    {
        List<string> cases = new List<string>(), combos = new List<string>(Combos()), r = new List<string>();
        foreach (KeyValuePair<string, int> kv in s.CaseStatus()) if (patterns != null || kv.Value == 4) cases.Add(kv.Key);
        if (patterns == null) { r.AddRange(cases); r.AddRange(combos); return r; }
        foreach (string p in patterns)
        {
            int before = r.Count;
            foreach (string c in cases) if (SapRead.Like(c, p) && !r.Contains(c)) r.Add(c);
            foreach (string c in combos) if (SapRead.Like(c, p) && !r.Contains(c)) r.Add(c);
            if (r.Count == before) throw new ArgumentException("no load case or combination '" + p + "'");
        }
        return r;
    }

    // Selects cases / combos for output. Nonlinear and multi-step static cases give their last step;
    // time histories and multi-valued combos give envelopes (rows with step Max / Min).
    void Select(IList<string> names)
    {
        cAnalysisResultsSetup setup = M.Results.Setup;
        setup.DeselectAllCasesAndCombosForOutput();
        if (setup.SetOptionNLStatic(3) != 0) setup.SetOptionNLStatic(1);
        if (setup.SetOptionMultiStepStatic(3) != 0) setup.SetOptionMultiStepStatic(1);
        HashSet<string> combos = new HashSet<string>(Combos());
        foreach (string c in names)
        {
            if (combos.Contains(c)) setup.SetComboSelectedForOutput(c, true);
            else setup.SetCaseSelectedForOutput(c, true);
        }
    }

    // ---------------------------------------------------------------- rows

    // One result row: object, where on it (frame station, area / link joint), case, step, Quantities(kind).
    public sealed class Row
    {
        public string Obj, At, Case, Step;
        public double[] V;
    }

    static Row MakeRow(string obj, string at, string c, string[] st, double[] sn, int i, params double[] v)
    {
        Row r = new Row();
        r.Obj = obj; r.At = at; r.Case = c; r.V = v;
        string t = st == null || i >= st.Length ? "" : st[i] ?? "";
        r.Step = t.Length == 0 ? null : (t == "Max" || t == "Min") ? t : (t + " " + sn[i].ToString(Inv)).Trim();
        return r;
    }

    // Every row of a kind for the given cases / combos (selected for output here). An API call that finds no
    // results gives no rows.
    public List<Row> Fetch(string kind, IList<string> cases)
    {
        Quantities(kind);
        Select(cases);
        List<Row> rows = new List<Row>();
        eItemTypeElm all = eItemTypeElm.GroupElm;
        int n = 0; string[] obj = null, elm = null, pe = null, cas = null, st = null; double[] sn = null;
        switch (kind)
        {
            case "joint":
            {
                double[] u1 = null, u2 = null, u3 = null, r1 = null, r2 = null, r3 = null;
                if (M.Results.JointDispl("ALL", all, ref n, ref obj, ref elm, ref cas, ref st, ref sn, ref u1, ref u2, ref u3, ref r1, ref r2, ref r3) != 0) break;
                for (int i = 0; i < n; i++)
                    rows.Add(MakeRow(obj[i], null, cas[i], st, sn, i, u1[i], u2[i], u3[i], r1[i], r2[i], r3[i],
                        Math.Sqrt(u1[i] * u1[i] + u2[i] * u2[i] + u3[i] * u3[i])));
                break;
            }
            case "reaction":
            {
                double[] f1 = null, f2 = null, f3 = null, m1 = null, m2 = null, m3 = null;
                if (M.Results.JointReact("ALL", all, ref n, ref obj, ref elm, ref cas, ref st, ref sn, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3) != 0) break;
                for (int i = 0; i < n; i++) rows.Add(MakeRow(obj[i], null, cas[i], st, sn, i, f1[i], f2[i], f3[i], m1[i], m2[i], m3[i]));
                break;
            }
            case "frame":
            {
                double[] osta = null, esta = null, p = null, v2 = null, v3 = null, t = null, m2 = null, m3 = null;
                if (M.Results.FrameForce("ALL", all, ref n, ref obj, ref osta, ref elm, ref esta, ref cas, ref st, ref sn, ref p, ref v2, ref v3, ref t, ref m2, ref m3) != 0) break;
                for (int i = 0; i < n; i++) rows.Add(MakeRow(obj[i], "station " + osta[i].ToString("G6", Inv), cas[i], st, sn, i, p[i], v2[i], v3[i], t[i], m2[i], m3[i]));
                break;
            }
            case "area":
            {
                double[] f11 = null, f22 = null, f12 = null, fmax = null, fmin = null, fang = null, fvm = null,
                    m11 = null, m22 = null, m12 = null, mmax = null, mmin = null, mang = null, v13 = null, v23 = null, vmax = null, vang = null;
                if (M.Results.AreaForceShell("ALL", all, ref n, ref obj, ref elm, ref pe, ref cas, ref st, ref sn, ref f11, ref f22, ref f12,
                    ref fmax, ref fmin, ref fang, ref fvm, ref m11, ref m22, ref m12, ref mmax, ref mmin, ref mang, ref v13, ref v23, ref vmax, ref vang) != 0) break;
                for (int i = 0; i < n; i++)
                    rows.Add(MakeRow(obj[i], "joint " + pe[i], cas[i], st, sn, i, f11[i], f22[i], f12[i], fmax[i], fmin[i], fvm[i],
                        m11[i], m22[i], m12[i], mmax[i], mmin[i], v13[i], v23[i], vmax[i]));
                break;
            }
            case "area_stress":
            {
                double[] s11t = null, s22t = null, s12t = null, smaxt = null, smint = null, sangt = null, svmt = null,
                    s11b = null, s22b = null, s12b = null, smaxb = null, sminb = null, sangb = null, svmb = null, s13 = null, s23 = null, smaxa = null, sanga = null;
                if (M.Results.AreaStressShell("ALL", all, ref n, ref obj, ref elm, ref pe, ref cas, ref st, ref sn, ref s11t, ref s22t, ref s12t, ref smaxt,
                    ref smint, ref sangt, ref svmt, ref s11b, ref s22b, ref s12b, ref smaxb, ref sminb, ref sangb, ref svmb, ref s13, ref s23, ref smaxa, ref sanga) != 0) break;
                for (int i = 0; i < n; i++)
                    rows.Add(MakeRow(obj[i], "joint " + pe[i], cas[i], st, sn, i, s11t[i], s22t[i], s12t[i], smaxt[i], smint[i], svmt[i],
                        s11b[i], s22b[i], s12b[i], smaxb[i], sminb[i], svmb[i], s13[i], s23[i], smaxa[i]));
                break;
            }
            case "link":
            {
                double[] p = null, v2 = null, v3 = null, t = null, m2 = null, m3 = null;
                if (M.Results.LinkForce("ALL", all, ref n, ref obj, ref elm, ref pe, ref cas, ref st, ref sn, ref p, ref v2, ref v3, ref t, ref m2, ref m3) != 0) break;
                for (int i = 0; i < n; i++) rows.Add(MakeRow(obj[i], "joint " + pe[i], cas[i], st, sn, i, p[i], v2[i], v3[i], t[i], m2[i], m3[i]));
                break;
            }
        }
        return rows;
    }

    // ---------------------------------------------------------------- query

    public sealed class ResultQuery
    {
        public string Kind, Quantity, Sort = "abs";   // sort: abs (largest magnitude), max, min
        public IList<string> Cases;                   // null: every finished case and every combo
        public HashSet<string> Only;                  // object names (null: all)
        public int Top = 20;
        public bool Envelope = true;                  // true: one row per object (its controlling case / location)
        public IList<string> Groups;                  // per-group summaries
        public string ToFile;                         // write every matching row as TSV
    }

    public Dictionary<string, object> Query(ResultQuery q)
    {
        string[] qs = Quantities(q.Kind);
        int qi = QIndex(qs, q.Quantity);
        List<string> cases = ResolveCases(q.Cases);
        Dictionary<string, object> r = O("kind", q.Kind, "quantity", qs[qi], "units", s.Units(), "axes", Axes(q.Kind), "cases", cases);
        if (cases.Count == 0) { r["note"] = "no case has results: run_analysis first"; return r; }
        List<Row> rows = Fetch(q.Kind, cases);
        if (q.Only != null) rows = rows.FindAll(delegate(Row x) { return q.Only.Contains(x.Obj); });
        HashSet<string> objs = new HashSet<string>();
        foreach (Row x in rows) objs.Add(x.Obj);
        r["rows"] = rows.Count; r["objects"] = objs.Count;
        if (rows.Count == 0) { r["note"] = "no results for these cases and objects"; return r; }

        Func<Row, double> key = Key(q.Sort, qi);
        Row hi = null, lo = null;
        foreach (Row x in rows) { if (hi == null || x.V[qi] > hi.V[qi]) hi = x; if (lo == null || x.V[qi] < lo.V[qi]) lo = x; }
        r["max"] = Brief(hi, qi); r["min"] = Brief(lo, qi);

        List<Row> pick;
        if (q.Envelope)
        {
            Dictionary<string, Row> best = new Dictionary<string, Row>();
            foreach (Row x in rows) { Row b; if (!best.TryGetValue(x.Obj, out b) || key(x) > key(b)) best[x.Obj] = x; }
            pick = new List<Row>(best.Values);
        }
        else pick = new List<Row>(rows);
        pick.Sort(delegate(Row a, Row b) { return key(b).CompareTo(key(a)); });
        List<object> top = new List<object>();
        for (int i = 0; i < pick.Count && i < q.Top; i++) top.Add(Full(pick[i], qi, qs));
        r["top"] = top;
        r["topBy"] = (q.Envelope ? "objects, each at its controlling case and location, " : "rows, ") +
            (q.Sort == "max" ? "largest value first" : q.Sort == "min" ? "smallest value first" : "largest magnitude first");

        if (cases.Count > 1 && cases.Count <= 100)
        {
            List<object> byCase = new List<object>();
            foreach (string c in cases)
            {
                Row b = null;
                foreach (Row x in rows) if (x.Case == c && (b == null || key(x) > key(b))) b = x;
                if (b != null) byCase.Add(O("case", c, "object", b.Obj, "at", b.At, "step", b.Step, "value", b.V[qi]));
            }
            r["byCase"] = byCase;
        }
        if (q.Groups != null)
        {
            List<object> byGroup = new List<object>();
            string ok = ObjectKind(q.Kind);
            foreach (string g in q.Groups)
            {
                HashSet<string> members = new HashSet<string>(read.GroupMembers(g, ok));
                Row b = null, gh = null, gl = null; int n = 0;
                foreach (Row x in rows)
                {
                    if (!members.Contains(x.Obj)) continue;
                    n++;
                    if (b == null || key(x) > key(b)) b = x;
                    if (gh == null || x.V[qi] > gh.V[qi]) gh = x;
                    if (gl == null || x.V[qi] < gl.V[qi]) gl = x;
                }
                if (b == null) { byGroup.Add(O("group", g, "rows", 0)); continue; }
                byGroup.Add(O("group", g, "rows", n, "controlling", Brief(b, qi), "max", Brief(gh, qi), "min", Brief(gl, qi)));
            }
            r["byGroup"] = byGroup;
        }
        if (q.ToFile != null) { WriteRows(q.ToFile, rows, qs); r["file"] = q.ToFile; }
        return r;
    }

    static int QIndex(string[] qs, string quantity)
    {
        for (int i = 0; i < qs.Length; i++) if (string.Equals(qs[i], quantity, StringComparison.OrdinalIgnoreCase)) return i;
        throw new ArgumentException("quantity '" + quantity + "' is not one of " + string.Join(", ", qs));
    }

    static Func<Row, double> Key(string sort, int qi)
    {
        if (sort == "max") return delegate(Row x) { return x.V[qi]; };
        if (sort == "min") return delegate(Row x) { return -x.V[qi]; };
        return delegate(Row x) { return Math.Abs(x.V[qi]); };
    }

    static Dictionary<string, object> Brief(Row x, int qi)
    {
        return O("value", x.V[qi], "object", x.Obj, "at", x.At, "case", x.Case, "step", x.Step);
    }

    static Dictionary<string, object> Full(Row x, int qi, string[] qs)
    {
        Dictionary<string, object> d = Brief(x, qi);
        Dictionary<string, object> all = new Dictionary<string, object>();
        for (int k = 0; k < qs.Length; k++) all[qs[k]] = x.V[k];
        d["all"] = all;
        return d;
    }

    static string Axes(string kind)
    {
        switch (kind)
        {
            case "joint": return "joint local axes (global unless the joint's axes are rotated: get_object joint)";
            case "reaction": return "joint local axes; F = force the support applies to the structure";
            case "frame": return "frame local axes: P axial (+ tension), V2 / V3 shear, T torsion, M2 / M3 bending";
            case "link": return "link local axes, at each end joint";
            case "area": return "area local axes, per unit length, at each corner joint";
            case "area_stress": return "area local axes, top / bottom faces, at each corner joint";
        }
        return "";
    }

    static void WriteRows(string path, List<Row> rows, string[] qs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.NewLine = "\n";
            w.WriteLine("object\tat\tcase\tstep\t" + string.Join("\t", qs));
            foreach (Row x in rows)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(x.Obj).Append('\t').Append(x.At).Append('\t').Append(x.Case).Append('\t').Append(x.Step);
                foreach (double v in x.V) sb.Append('\t').Append(SapSession.R(v));
                w.WriteLine(sb.ToString());
            }
        }
    }

    // ---------------------------------------------------------------- reactions against the applied load

    // Per case / combo: base reactions (Results.BaseReact, about the base point) and the applied load summed from
    // the load assignments (self weight, joint, frame and area loads) and the case / combo definitions. A balanced
    // case has applied + reaction within tolerance (fraction of the larger of the two). What cannot be summed here
    // (pressure on area edges, auto lateral loads, acceleration loads, other case types) makes the check "incomplete" and
    // is listed.
    public Dictionary<string, object> Reactions(IList<string> casePatterns, double tolerance)
    {
        List<string> cases = ResolveCases(casePatterns);
        Dictionary<string, object> r = O("units", s.Units(), "tolerance", tolerance,
            "signs", "reaction = force the supports apply to the structure; balanced when applied + reaction is about 0");
        if (cases.Count == 0) { r["note"] = "no case has results: run_analysis first"; return r; }
        Select(cases);
        int n = 0; string[] cas = null, st = null; double[] sn = null, fx = null, fy = null, fz = null, mx = null, my = null, mz = null;
        double gx = 0, gy = 0, gz = 0;
        M.Results.BaseReact(ref n, ref cas, ref st, ref sn, ref fx, ref fy, ref fz, ref mx, ref my, ref mz, ref gx, ref gy, ref gz);
        r["basePoint"] = new double[] { gx, gy, gz };

        Applied loads = new Applied(this);
        List<object> list = new List<object>();
        for (int i = 0; i < n; i++)
        {
            string step = st == null || string.IsNullOrEmpty(st[i]) ? null : st[i];
            Dictionary<string, object> d = O("case", cas[i], "step", step,
                "reaction", O("FX", fx[i], "FY", fy[i], "FZ", fz[i], "MX", mx[i], "MY", my[i], "MZ", mz[i]));
            Load a = step == "Min" || step == "Max" ? Load.Unsupported("an envelope (" + step + ") is not one load state") : loads.ForCase(cas[i]);
            if (a.Why != null) d["check"] = "not checked: " + a.Why;
            else
            {
                double[] react = { fx[i], fy[i], fz[i] };
                double[] res = new double[3];
                double scale = 0;
                for (int k = 0; k < 3; k++) { res[k] = a.F[k] + react[k]; scale = Math.Max(scale, Math.Max(Math.Abs(a.F[k]), Math.Abs(react[k]))); }
                double worst = Math.Max(Math.Abs(res[0]), Math.Max(Math.Abs(res[1]), Math.Abs(res[2])));
                d["applied"] = O("FX", a.F[0], "FY", a.F[1], "FZ", a.F[2]);
                d["residual"] = O("FX", res[0], "FY", res[1], "FZ", res[2]);
                bool ok = worst <= tolerance * Math.Max(scale, 1e-12) || (scale == 0);
                if (a.Missing.Count > 0) { d["check"] = "incomplete"; d["notSummed"] = a.Missing; }
                else d["check"] = ok ? "balanced" : "OFF by " + (scale > 0 ? (100 * worst / scale).ToString("0.###", Inv) + " %" : "?");
                if (a.Missing.Count > 0 && !ok) d["note"] = "applied and reaction differ; part of the load was not summed (notSummed)";
            }
            list.Add(d);
        }
        r["cases"] = list;
        r["selfWeight"] = loads.WeightNote();
        return r;
    }

    // A force total (global X, Y, Z) and what could not be summed into it; Why != null: not computable at all.
    sealed class Load
    {
        public double[] F = new double[3];
        public List<string> Missing = new List<string>();
        public string Why;
        public static Load Unsupported(string why) { Load l = new Load(); l.Why = why; return l; }
        public void Add(Load o, double sf)
        {
            for (int k = 0; k < 3; k++) F[k] += sf * o.F[k];
            foreach (string m in o.Missing) if (!Missing.Contains(m)) Missing.Add(m);
        }
        public void Add(double[] dir, double v) { for (int k = 0; k < 3; k++) F[k] += v * dir[k]; }
        public void Miss(string m) { if (!Missing.Contains(m)) Missing.Add(m); }
    }

    // Applied load per pattern, case and combo, summed from the model's load assignments.
    sealed class Applied
    {
        readonly SapResults o;
        cSapModel M { get { return o.M; } }
        readonly SapRead.Index x;
        readonly Dictionary<string, Load> byPattern = new Dictionary<string, Load>();
        readonly Dictionary<string, Load> byCase = new Dictionary<string, Load>();
        readonly Dictionary<string, double> weight = new Dictionary<string, double>();   // "frame|name" -> self weight
        double totalWeight;
        readonly List<string> weightGaps = new List<string>();
        static readonly double[] Down = { 0, 0, -1 };

        public Applied(SapResults owner)
        {
            o = owner;
            x = owner.read.Objects();
            Weights();
            int n = 0; string[] pats = null;
            M.LoadPatterns.GetNameList(ref n, ref pats);
            foreach (string p in pats ?? new string[0])
            {
                Load l = new Load();
                double sw = 0;
                M.LoadPatterns.GetSelfWTMultiplier(p, ref sw);
                if (sw != 0)
                {
                    l.Add(Down, sw * totalWeight);
                    foreach (string g in weightGaps) l.Miss("self weight of " + g);
                }
                byPattern[p] = l;
            }
            JointForces(); FrameLoads(); AreaLoads();
            if (M.SolidObj.Count() > 0 || M.CableObj.Count() > 0 || M.TendonObj.Count() > 0)
                foreach (Load l in byPattern.Values) l.Miss("loads and weight of solids, cables and tendons");
        }

        public object WeightNote()
        {
            Dictionary<string, object> d = SapResults.O("total", totalWeight, "direction", "-Z",
                "basis", "frames: section A x length x material weight per volume x weight modifiers; shells: membrane thickness x area; links: property weight; surface pressure with a joint pattern: mean of the corners x area");
            if (weightGaps.Count > 0) d["notSummed"] = weightGaps;
            return d;
        }

        Load Pattern(string p)
        {
            Load l;
            if (!byPattern.TryGetValue(p, out l)) { l = new Load(); byPattern[p] = l; }
            return l;
        }

        public Load ForCase(string c) { return ForCase(c, 0); }

        Load ForCase(string c, int depth)
        {
            Load l;
            if (byCase.TryGetValue(c, out l)) return l;
            if (depth > 50) return Load.Unsupported("case / combo nesting too deep");
            l = Compute(c, depth);
            byCase[c] = l;
            return l;
        }

        Load Compute(string c, int depth)
        {
            if (Array.IndexOf(o.Combos(), c) >= 0)
            {
                int t = 0;
                M.RespCombo.GetTypeOAPI(c, ref t);
                if (t != 0) return Load.Unsupported("combination type is not linear additive");
                int n = 0; eCNameType[] it = null; string[] items = null; double[] sf = null;
                M.RespCombo.GetCaseList(c, ref n, ref it, ref items, ref sf);
                Load l = new Load();
                for (int k = 0; k < n; k++)
                {
                    Load part = ForCase(items[k], depth + 1);
                    if (part.Why != null) return Load.Unsupported(items[k] + ": " + part.Why);
                    l.Add(part, sf[k]);
                }
                return l;
            }
            eLoadCaseType type = eLoadCaseType.LinearStatic; int sub = 0;
            M.LoadCases.GetTypeOAPI(c, ref type, ref sub);
            int nl = 0; string[] lt = null, ln = null; double[] lsf = null; string init = "";
            if (type == eLoadCaseType.LinearStatic) M.LoadCases.StaticLinear.GetLoads(c, ref nl, ref lt, ref ln, ref lsf);
            else if (type == eLoadCaseType.NonlinearStatic)
            {
                M.LoadCases.StaticNonlinear.GetLoads(c, ref nl, ref lt, ref ln, ref lsf);
                M.LoadCases.StaticNonlinear.GetInitialCase(c, ref init);
            }
            else return Load.Unsupported(type + " cases are not summed (static cases and linear additive combinations only)");
            Load r = new Load();
            for (int k = 0; k < nl; k++)
            {
                if (lt[k] == "Load") r.Add(Pattern(ln[k]), lsf[k]);
                else r.Miss("acceleration load " + ln[k] + " in " + c);
            }
            if (!string.IsNullOrEmpty(init))
            {
                Load i = ForCase(init, depth + 1);   // a nonlinear case continues from its initial case's loads
                if (i.Why != null) return Load.Unsupported("initial case " + init + ": " + i.Why);
                r.Add(i, 1);
            }
            return r;
        }

        // ---- self weight per object

        void Weights()
        {
            Dictionary<string, double> secA = new Dictionary<string, double>(), secW = new Dictionary<string, double>();
            foreach (string f in x.Frames)
            {
                string sec = x.FrameSection[f];
                double a, w;
                if (!secA.TryGetValue(sec, out a))
                {
                    double A = 0, As2 = 0, As3 = 0, J = 0, I22 = 0, I33 = 0, S22 = 0, S33 = 0, Z22 = 0, Z33 = 0, r22 = 0, r33 = 0;
                    M.PropFrame.GetSectProps(sec, ref A, ref As2, ref As3, ref J, ref I22, ref I33, ref S22, ref S33, ref Z22, ref Z33, ref r22, ref r33);
                    string mat = "";
                    M.PropFrame.GetMaterial(sec, ref mat);
                    double[] mod = new double[8];
                    double sm = M.PropFrame.GetModifiers(sec, ref mod) == 0 && mod != null && mod.Length > 7 ? mod[7] : 1;
                    a = A * sm;
                    secA[sec] = a;
                    secW[sec] = MatWeight(mat);
                }
                w = secW[sec];
                double[] om = new double[8];
                double objMod = M.FrameObj.GetModifiers(f, ref om) == 0 && om != null && om.Length > 7 ? om[7] : 1;
                double wt = a * w * objMod * Length(x.FrameEnds[f]);
                weight["frame|" + f] = wt;
                totalWeight += wt;
            }
            Dictionary<string, double[]> secT = new Dictionary<string, double[]>();   // thickness, weight per volume, weight modifier; null = not summed
            foreach (string ar in x.Areas)
            {
                string sec = x.AreaSection[ar];
                double[] tw;
                if (!secT.TryGetValue(sec, out tw))
                {
                    tw = null;
                    int pt = 0;
                    M.PropArea.GetTypeOAPI(sec, ref pt);
                    int st = 0, color = 0; bool drill = false; string mat = "", notes = "", guid = ""; double ang = 0, th = 0, bend = 0;
                    if (pt == 1 && M.PropArea.GetShell_1(sec, ref st, ref drill, ref mat, ref ang, ref th, ref bend, ref color, ref notes, ref guid) == 0 && st != 6)
                    {
                        double[] mod = new double[10];
                        double sm = M.PropArea.GetModifiers(sec, ref mod) == 0 && mod != null && mod.Length > 9 ? mod[9] : 1;
                        tw = new double[] { th, MatWeight(mat), sm };
                    }
                    else Gap(pt == 1 ? "layered shells (" + sec + ")" : "plane / asolid areas (" + sec + ")");
                    secT[sec] = tw;
                }
                if (tw == null) continue;
                double t = tw[0];
                int tt = 0; string tp = ""; double tsf = 0; double[] tj = null;
                if (M.AreaObj.GetThickness(ar, ref tt, ref tp, ref tsf, ref tj) == 0 && tt == 2 && tj != null && tj.Length > 0)
                {
                    double sum = 0; foreach (double v in tj) sum += v; t = sum / tj.Length;   // thickness by joint: the mean
                }
                else if (tt == 1) Gap("areas with joint-pattern thickness overwrites");
                double[] om = new double[10];
                double objMod = M.AreaObj.GetModifiers(ar, ref om) == 0 && om != null && om.Length > 9 ? om[9] : 1;
                double[] nrm; double area = Polygon(x.AreaPoints[ar], out nrm);
                double wt = t * tw[1] * tw[2] * objMod * area;
                weight["area|" + ar] = wt;
                totalWeight += wt;
            }
            Dictionary<string, double> linkW = new Dictionary<string, double>();
            foreach (string l in x.Links)
            {
                string p = x.LinkProp[l];
                double w;
                if (!linkW.TryGetValue(p, out w))
                {
                    double ms = 0, r1 = 0, r2 = 0, r3 = 0; w = 0;
                    M.PropLink.GetWeightAndMass(p, ref w, ref ms, ref r1, ref r2, ref r3);
                    linkW[p] = w;
                }
                totalWeight += w;
            }
        }

        void Gap(string what) { if (!weightGaps.Contains(what)) weightGaps.Add(what); }

        double MatWeight(string mat)
        {
            double w = 0, m = 0;
            return M.PropMaterial.GetWeightAndMass(mat, ref w, ref m, 0) == 0 ? w : 0;
        }

        double Length(string[] ends)
        {
            double[] a, b;
            if (ends.Length < 2 || !x.Xyz.TryGetValue(ends[0], out a) || !x.Xyz.TryGetValue(ends[1], out b)) return 0;
            return Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]) + (b[2] - a[2]) * (b[2] - a[2]));
        }

        // Area of a planar polygon (Newell) and its unit normal.
        double Polygon(string[] pts, out double[] normal)
        {
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                double[] p = x.Xyz[pts[i]], q = x.Xyz[pts[(i + 1) % pts.Length]];
                nx += (p[1] - q[1]) * (p[2] + q[2]);
                ny += (p[2] - q[2]) * (p[0] + q[0]);
                nz += (p[0] - q[0]) * (p[1] + q[1]);
            }
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            normal = len > 0 ? new double[] { nx / len, ny / len, nz / len } : new double[] { 0, 0, 1 };
            return len / 2;
        }

        double[] FrameAxis(string f)
        {
            string[] e = x.FrameEnds[f];
            double[] a = x.Xyz[e[0]], b = x.Xyz[e[1]];
            double[] d = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            double len = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            return len > 0 ? new double[] { d[0] / len, d[1] / len, d[2] / len } : d;
        }

        // Unit vector of a frame / area load direction (SapRead.LoadDir codes); null: not summed (other csys).
        static double[] DirVec(int dir, string csys, double[] tm)
        {
            if (dir >= 1 && dir <= 3) return tm == null ? null : new double[] { tm[dir - 1], tm[3 + dir - 1], tm[6 + dir - 1] };
            if (dir >= 4 && dir <= 9)
            {
                if (!string.Equals(csys, "GLOBAL", StringComparison.OrdinalIgnoreCase)) return null;
                int k = (dir - 4) % 3;
                double[] v = new double[3]; v[k] = 1; return v;
            }
            if (dir == 10 || dir == 11) return Down;
            return null;
        }

        static bool Projected(int dir) { return (dir >= 7 && dir <= 9) || dir == 11; }
        static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

        double[] Matrix(string kind, string name)
        {
            double[] m = new double[9];
            int ret = kind == "frame" ? M.FrameObj.GetTransformationMatrix(name, ref m, true) : M.AreaObj.GetTransformationMatrix(name, ref m, true);
            return ret == 0 ? m : null;
        }

        // ---- load assignments

        void JointForces()
        {
            int n = 0; string[] pn = null, lp = null, cs = null; int[] step = null;
            double[] f1 = null, f2 = null, f3 = null, m1 = null, m2 = null, m3 = null;
            if (M.PointObj.GetLoadForce("ALL", ref n, ref pn, ref lp, ref step, ref cs, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3, eItemType.Group) != 0) return;
            for (int k = 0; k < n; k++)
            {
                if (!string.Equals(cs[k], "GLOBAL", StringComparison.OrdinalIgnoreCase)) { Pattern(lp[k]).Miss("joint forces in csys " + cs[k]); continue; }
                Pattern(lp[k]).Add(new double[] { f1[k], f2[k], f3[k] }, 1);
            }
        }

        void FrameLoads()
        {
            int n = 0; string[] fn = null, lp = null, cs = null; int[] type = null, dir = null;
            double[] rd1 = null, rd2 = null, d1 = null, d2 = null, v1 = null, v2 = null;
            if (M.FrameObj.GetLoadDistributed("ALL", ref n, ref fn, ref lp, ref type, ref cs, ref dir, ref rd1, ref rd2, ref d1, ref d2, ref v1, ref v2, eItemType.Group) == 0)
                for (int k = 0; k < n; k++)
                {
                    if (type[k] != 1) continue;   // distributed moments: no net force
                    double[] dv = DirVec(dir[k], cs[k], dir[k] <= 3 ? Matrix("frame", fn[k]) : null);
                    if (dv == null) { Pattern(lp[k]).Miss("frame distributed loads in csys " + cs[k]); continue; }
                    double total = 0.5 * (v1[k] + v2[k]) * (d2[k] - d1[k]);
                    if (Projected(dir[k])) { double c = Dot(FrameAxis(fn[k]), dv); total *= Math.Sqrt(Math.Max(0, 1 - c * c)); }
                    Pattern(lp[k]).Add(dv, total);
                }
            n = 0; fn = null; lp = null; cs = null; type = null; dir = null;
            double[] rel = null, dist = null, val = null;
            if (M.FrameObj.GetLoadPoint("ALL", ref n, ref fn, ref lp, ref type, ref cs, ref dir, ref rel, ref dist, ref val, eItemType.Group) == 0)
                for (int k = 0; k < n; k++)
                {
                    if (type[k] != 1) continue;
                    double[] dv = DirVec(dir[k], cs[k], dir[k] <= 3 ? Matrix("frame", fn[k]) : null);
                    if (dv == null) { Pattern(lp[k]).Miss("frame point loads in csys " + cs[k]); continue; }
                    Pattern(lp[k]).Add(dv, val[k]);
                }
            n = 0; fn = null; lp = null; cs = null;
            double[] gx = null, gy = null, gz = null;
            if (M.FrameObj.GetLoadGravity("ALL", ref n, ref fn, ref lp, ref cs, ref gx, ref gy, ref gz, eItemType.Group) == 0)
                for (int k = 0; k < n; k++) Gravity(lp[k], cs[k], "frame|" + fn[k], gx[k], gy[k], gz[k]);
        }

        void AreaLoads()
        {
            int n = 0; string[] an = null, lp = null, cs = null; int[] dir = null; double[] val = null;
            if (M.AreaObj.GetLoadUniform("ALL", ref n, ref an, ref lp, ref cs, ref dir, ref val, eItemType.Group) == 0)
                for (int k = 0; k < n; k++) AreaForce(an[k], lp[k], cs[k], dir[k], val[k], "area uniform loads");
            n = 0; an = null; lp = null; cs = null; dir = null; val = null; int[] dt = null;
            if (M.AreaObj.GetLoadUniformToFrame("ALL", ref n, ref an, ref lp, ref cs, ref dir, ref val, ref dt, eItemType.Group) == 0)
                for (int k = 0; k < n; k++) AreaForce(an[k], lp[k], cs[k], dir[k], val[k], "area uniform-to-frame loads");
            n = 0; an = null; lp = null; int[] face = null; double[] v = null; string[] jp = null;
            if (M.AreaObj.GetLoadSurfacePressure("ALL", ref n, ref an, ref lp, ref face, ref v, ref jp, eItemType.Group) == 0)
                for (int k = 0; k < n; k++) SurfacePressure(an[k], lp[k], face[k], v[k], jp[k]);
            n = 0; an = null; lp = null; cs = null;
            double[] gx = null, gy = null, gz = null;
            if (M.AreaObj.GetLoadGravity("ALL", ref n, ref an, ref lp, ref cs, ref gx, ref gy, ref gz, eItemType.Group) == 0)
                for (int k = 0; k < n; k++) Gravity(lp[k], cs[k], "area|" + an[k], gx[k], gy[k], gz[k]);
        }

        void AreaForce(string ar, string pat, string cs, int dir, double value, string what)
        {
            double[] dv = DirVec(dir, cs, dir <= 3 ? Matrix("area", ar) : null);
            if (dv == null) { Pattern(pat).Miss(what + " in csys " + cs); return; }
            double[] nrm; double area = Polygon(x.AreaPoints[ar], out nrm);
            double total = value * area;
            if (Projected(dir)) total *= Math.Abs(Dot(nrm, dv));
            Pattern(pat).Add(dv, total);
        }

        // Pressure on the top (-2) or bottom (-1) face pushes into it: along -local 3 on the top, +local 3 on the bottom.
        // With a joint pattern the pressure is value x pattern, taken as the mean of the corners (exact for a linear
        // pattern on a parallelogram). Edge faces are not summed.
        void SurfacePressure(string ar, string pat, int face, double value, string jointPattern)
        {
            if (face != -1 && face != -2) { Pattern(pat).Miss("surface pressure on area edges"); return; }
            double[] tm = Matrix("area", ar);
            if (tm == null) { Pattern(pat).Miss("surface pressure on areas without local axes"); return; }
            string[] pts = x.AreaPoints[ar];
            double mean = 1;
            if (!string.IsNullOrEmpty(jointPattern))
            {
                double sum = 0;
                foreach (string p in pts) sum += JointPattern(p, jointPattern);
                mean = pts.Length > 0 ? sum / pts.Length : 0;
            }
            double[] nrm; double area = Polygon(pts, out nrm);
            Pattern(pat).Add(new double[] { tm[2], tm[5], tm[8] }, (face == -2 ? -1 : 1) * value * mean * area);
        }

        readonly Dictionary<string, double> patternValues = new Dictionary<string, double>();
        double JointPattern(string joint, string pattern)
        {
            string key = pattern + "|" + joint;
            double v;
            if (patternValues.TryGetValue(key, out v)) return v;
            v = 0;
            if (M.PointObj.GetPatternValue(joint, pattern, ref v) != 0) v = 0;
            patternValues[key] = v;
            return v;
        }

        // Gravity load: the object's self weight times the multipliers, in csys (global only).
        void Gravity(string pat, string cs, string obj, double gx, double gy, double gz)
        {
            if (!string.Equals(cs, "GLOBAL", StringComparison.OrdinalIgnoreCase)) { Pattern(pat).Miss("gravity loads in csys " + cs); return; }
            double w;
            if (!weight.TryGetValue(obj, out w)) { Pattern(pat).Miss("gravity loads on objects whose weight is not summed"); return; }
            Pattern(pat).Add(new double[] { gx, gy, gz }, w);
        }
    }

    // ---------------------------------------------------------------- per-run envelopes (compare_runs)

    // For every result kind with objects, per object and case: the signed value of largest magnitude of each
    // quantity over stations, joints and steps, written to <dir>\<kind>.tsv. Returns, per kind and quantity, the
    // largest magnitude with its object and case.
    public Dictionary<string, object> SaveEnvelopes(string dir, IList<string> casePatterns)
    {
        Directory.CreateDirectory(dir);
        List<string> cases = ResolveCases(casePatterns);
        Dictionary<string, object> maxima = new Dictionary<string, object>();
        if (cases.Count == 0) return maxima;
        int[] c = s.Counts();
        foreach (string kind in ResultKinds)
        {
            int objects = kind == "joint" || kind == "reaction" ? c[0] : kind == "frame" ? c[2] : kind == "link" ? c[3] : c[1];
            if (objects == 0) continue;
            string[] qs = Quantities(kind);
            Dictionary<string, double[]> env = new Dictionary<string, double[]>();
            List<string> order = new List<string>();
            foreach (Row x in Fetch(kind, cases))
            {
                string key = x.Obj + "\t" + x.Case;
                double[] e;
                if (!env.TryGetValue(key, out e)) { e = (double[])x.V.Clone(); env[key] = e; order.Add(key); continue; }
                for (int k = 0; k < e.Length; k++) if (Math.Abs(x.V[k]) > Math.Abs(e[k])) e[k] = x.V[k];
            }
            if (env.Count == 0) continue;
            StringBuilder sb = new StringBuilder("object\tcase\t" + string.Join("\t", qs) + "\n");
            double[] best = new double[qs.Length]; string[] bestKey = new string[qs.Length];
            foreach (string key in order)
            {
                double[] e = env[key];
                sb.Append(key);
                for (int k = 0; k < e.Length; k++)
                {
                    sb.Append('\t').Append(SapSession.R(e[k]));
                    if (bestKey[k] == null || Math.Abs(e[k]) > Math.Abs(best[k])) { best[k] = e[k]; bestKey[k] = key; }
                }
                sb.Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, kind + ".tsv"), sb.ToString(), new UTF8Encoding(false));
            Dictionary<string, object> m = new Dictionary<string, object>();
            for (int k = 0; k < qs.Length; k++)
            {
                string[] oc = bestKey[k].Split('\t');
                m[qs[k]] = O("value", best[k], "object", oc[0], "case", oc[1]);
            }
            maxima[kind] = m;
        }
        return maxima;
    }

    // An envelope file: "object\tcase" -> values, and the quantity names.
    public static Dictionary<string, double[]> LoadEnvelope(string path, out string[] quantities)
    {
        Dictionary<string, double[]> d = new Dictionary<string, double[]>();
        string[] lines = File.ReadAllLines(path);
        string[] head = lines[0].Split('\t');
        quantities = new string[head.Length - 2];
        Array.Copy(head, 2, quantities, 0, quantities.Length);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] f = lines[i].Split('\t');
            if (f.Length < head.Length) continue;
            double[] v = new double[quantities.Length];
            for (int k = 0; k < v.Length; k++) v[k] = double.Parse(f[k + 2], NumberStyles.Float, Inv);
            d[f[0] + "\t" + f[1]] = v;
        }
        return d;
    }

    // Biggest changes of one quantity between two runs' envelopes (b - a), per object and case.
    // only: object names (null: all); cases: names or patterns (null: all).
    public static Dictionary<string, object> Compare(string fileA, string fileB, string quantity, HashSet<string> only, IList<string> cases, int top)
    {
        string[] qa, qb;
        Dictionary<string, double[]> a = LoadEnvelope(fileA, out qa), b = LoadEnvelope(fileB, out qb);
        int ia = QIndex(qa, quantity), ib = QIndex(qb, quantity);
        List<object[]> changes = new List<object[]>();   // object, case, a, b, change
        int onlyA = 0, onlyB = 0;
        double maxA = 0, maxB = 0;
        foreach (KeyValuePair<string, double[]> kv in a)
        {
            string[] oc = kv.Key.Split('\t');
            if (!Keep(oc, only, cases)) continue;
            maxA = Math.Max(maxA, Math.Abs(kv.Value[ia]));
            double[] vb;
            if (!b.TryGetValue(kv.Key, out vb)) { onlyA++; continue; }
            changes.Add(new object[] { oc[0], oc[1], kv.Value[ia], vb[ib], vb[ib] - kv.Value[ia] });
        }
        foreach (KeyValuePair<string, double[]> kv in b)
        {
            string[] oc = kv.Key.Split('\t');
            if (!Keep(oc, only, cases)) continue;
            maxB = Math.Max(maxB, Math.Abs(kv.Value[ib]));
            if (!a.ContainsKey(kv.Key)) onlyB++;
        }
        changes.Sort(delegate(object[] x, object[] y) { return Math.Abs((double)y[4]).CompareTo(Math.Abs((double)x[4])); });
        List<object> list = new List<object>();
        for (int i = 0; i < changes.Count && i < top; i++)
        {
            object[] ch = changes[i];
            double va = (double)ch[2], vb = (double)ch[3];
            list.Add(O("object", ch[0], "case", ch[1], "a", va, "b", vb, "change", vb - va,
                "relative", va != 0 ? (object)Math.Round((vb - va) / Math.Abs(va), 6) : null));
        }
        // Run-to-run noise of a nonlinear solution is ~1e-14 of the values: changes below 1e-9 of the largest
        // magnitude do not count as changed.
        double noise = 1e-9 * Math.Max(maxA, maxB);
        int changed = 0;
        foreach (object[] ch in changes) if (Math.Abs((double)ch[4]) > noise) changed++;
        return O("quantity", qa[ia], "compared", changes.Count, "changed", changed, "changedAbove", noise, "onlyInA", onlyA, "onlyInB", onlyB,
            "maxAbsA", maxA, "maxAbsB", maxB, "top", list);
    }

    static bool Keep(string[] oc, HashSet<string> only, IList<string> cases)
    {
        if (only != null && !only.Contains(oc[0])) return false;
        if (cases == null) return true;
        foreach (string p in cases) if (SapRead.Like(oc[1], p)) return true;
        return false;
    }

    // ---------------------------------------------------------------- helpers

    static Dictionary<string, object> O(params object[] kv) { return SapRead.O(kv); }
    static void Check(int ret, string what) { if (ret != 0) throw new Exception(what + " returned " + ret); }
}
