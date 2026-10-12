// Read-only views of the model SAP2000 holds, as plain dictionaries / lists (JSON-ready): describe, list,
// one object in full, sections, materials, cases, combos, selection by geometry and attributes, levels,
// input tables. Used by the Pluto MCP server (scripts\mcp) and Run-Sap.ps1 -Describe. Never edits, runs or
// saves the model; show-in-SAP selection changes only the GUI selection.
// Values are in the model's present units (Units()), coordinates global. C# 5 (see SapSession.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SAP2000v1;

public class SapRead
{
    public const int DefaultLimit = 200;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Dof = { "U1", "U2", "U3", "R1", "R2", "R3" };
    static readonly string[] GlobalDof = { "UX", "UY", "UZ", "RX", "RY", "RZ" };
    static readonly string[] ComboTypes = { "LinearAdditive", "Envelope", "AbsoluteAdditive", "SRSS", "RangeAdditive" };
    static readonly string[] CaseStates = { "?", "not run", "could not start", "not finished", "finished" };
    static readonly string[] AreaTypes = { "?", "Shell", "Plane", "Asolid" };
    static readonly string[] ShellTypes = { "?", "Shell-Thin", "Shell-Thick", "Plate-Thin", "Plate-Thick", "Membrane", "Shell-Layered" };
    static readonly string[] Kinds = { "joint", "frame", "area", "link" };

    readonly SapSession s;
    Index index;

    public SapRead(SapSession session) { s = session; }
    cSapModel M { get { return s.Model; } }

    // ---------------------------------------------------------------- object index (cached)

    // Every joint, frame, area and link with its connectivity and property, built once per model state
    // (path, counts, units). Edit tools must call Invalidate(): an edit that keeps the counts is not seen.
    public sealed class Index
    {
        public string Stamp, Units;
        public double Seconds;
        public List<string> Joints = new List<string>(), Frames = new List<string>(), Areas = new List<string>(), Links = new List<string>();
        public Dictionary<string, double[]> Xyz = new Dictionary<string, double[]>();
        public Dictionary<string, string[]> FrameEnds = new Dictionary<string, string[]>(), AreaPoints = new Dictionary<string, string[]>(), LinkEnds = new Dictionary<string, string[]>();
        public Dictionary<string, string> FrameSection = new Dictionary<string, string>(), AreaSection = new Dictionary<string, string>(), LinkProp = new Dictionary<string, string>();

        public List<string> Names(string kind)
        {
            switch (kind) { case "joint": return Joints; case "frame": return Frames; case "area": return Areas; case "link": return Links; }
            throw new ArgumentException("kind must be joint, frame, area or link (got '" + kind + "')");
        }

        public string[] PointsOf(string kind, string name)
        {
            string[] p;
            switch (kind)
            {
                case "joint": return new string[] { name };
                case "frame": return FrameEnds.TryGetValue(name, out p) ? p : new string[0];
                case "area": return AreaPoints.TryGetValue(name, out p) ? p : new string[0];
                case "link": return LinkEnds.TryGetValue(name, out p) ? p : new string[0];
            }
            return new string[0];
        }

        public string PropOf(string kind, string name)
        {
            string p;
            switch (kind)
            {
                case "frame": return FrameSection.TryGetValue(name, out p) ? p : null;
                case "area": return AreaSection.TryGetValue(name, out p) ? p : null;
                case "link": return LinkProp.TryGetValue(name, out p) ? p : null;
            }
            return null;
        }
    }

    string StampNow()
    {
        int[] c = s.Counts();
        return s.ModelPath() + "|" + string.Join(",", Array.ConvertAll(c, delegate(int x) { return x.ToString(Inv); })) + "|" + s.Units();
    }

    public void Invalidate() { index = null; }

    public string ModelPath() { return s.ModelPath(); }

    // Largest side of the joints' bounding box (0 without joints): the scale for default tolerances.
    public double LargestExtent()
    {
        Index x = Objects();
        Dictionary<string, object> e = Extent(x, x.Joints);
        if (e == null) return 0;
        double[] lo = (double[])e["min"], hi = (double[])e["max"];
        return Math.Max(hi[0] - lo[0], Math.Max(hi[1] - lo[1], hi[2] - lo[2]));
    }

    public Index Objects()
    {
        string stamp = StampNow();
        if (index != null && index.Stamp == stamp) return index;
        Stopwatch sw = Stopwatch.StartNew();
        Index x = new Index();
        x.Stamp = stamp; x.Units = s.Units();
        x.Joints.AddRange(NameList(M.PointObj.GetNameList));
        foreach (string p in x.Joints)
        {
            double a = 0, b = 0, c = 0;
            M.PointObj.GetCoordCartesian(p, ref a, ref b, ref c, "Global");
            x.Xyz[p] = new double[] { a, b, c };
        }
        x.Frames.AddRange(NameList(M.FrameObj.GetNameList));
        foreach (string f in x.Frames)
        {
            string i = "", j = "", sec = "", auto = "";
            M.FrameObj.GetPoints(f, ref i, ref j);
            M.FrameObj.GetSection(f, ref sec, ref auto);
            x.FrameEnds[f] = new string[] { i, j };
            x.FrameSection[f] = sec;
        }
        x.Areas.AddRange(NameList(M.AreaObj.GetNameList));
        foreach (string ar in x.Areas)
        {
            int n = 0; string[] pts = null; string prop = "";
            M.AreaObj.GetPoints(ar, ref n, ref pts);
            M.AreaObj.GetProperty(ar, ref prop);
            x.AreaPoints[ar] = pts ?? new string[0];
            x.AreaSection[ar] = prop;
        }
        x.Links.AddRange(NameList(M.LinkObj.GetNameList));
        foreach (string l in x.Links)
        {
            string i = "", j = "", prop = "";
            M.LinkObj.GetPoints(l, ref i, ref j);
            M.LinkObj.GetProperty(l, ref prop);
            x.LinkEnds[l] = new string[] { i, j };
            x.LinkProp[l] = prop;
        }
        x.Seconds = sw.Elapsed.TotalSeconds;
        index = x;
        return x;
    }

    delegate int NameListFn(ref int n, ref string[] names);
    static string[] NameList(NameListFn fn) { int n = 0; string[] a = null; fn(ref n, ref a); return a ?? new string[0]; }

    // ---------------------------------------------------------------- describe

    // The whole model in one reply: units, counts, extent, supports, properties with use counts, groups,
    // patterns, cases with status, combos. Each list is capped at `cap` (its full length is given).
    public Dictionary<string, object> Describe(int cap)
    {
        Index x = Objects();
        Dictionary<string, object> r = O(
            "program", "SAP2000", "version", s.Version(), "model", s.ModelPath(), "units", x.Units,
            "locked", M.GetModelIsLocked());
        r["objects"] = O("joints", x.Joints.Count, "frames", x.Frames.Count, "areas", x.Areas.Count, "links", x.Links.Count,
            "solids", M.SolidObj.Count(), "cables", M.CableObj.Count(), "tendons", M.TendonObj.Count());
        r["extent"] = Extent(x, x.Joints);

        // supports
        Dictionary<string, int> byRestraint = new Dictionary<string, int>();
        int springs = 0, constrained = 0;
        foreach (string p in x.Joints)
        {
            bool[] rr = new bool[6];
            M.PointObj.GetRestraint(p, ref rr);
            string k = Flags(rr, GlobalDof);
            if (k != "-") Bump(byRestraint, k);
            double[] kk = new double[6];
            if (M.PointObj.GetSpring(p, ref kk) == 0 && kk != null) foreach (double v in kk) if (v != 0) { springs++; break; }
            int nc = 0; string[] pn = null, cn = null;
            if (M.PointObj.GetConstraint(p, ref nc, ref pn, ref cn, eItemType.Objects) == 0 && nc > 0) constrained++;
        }
        r["supports"] = O("restrainedJoints", Sum(byRestraint), "byRestraint", byRestraint, "jointsWithSprings", springs, "jointsInConstraints", constrained,
            "constraints", NameList(M.ConstraintDef.GetNameList));
        r["coordinateSystems"] = NameList(M.CoordSys.GetNameList);

        r["materials"] = Cap(r, "materials", MaterialRows(null, false), cap);
        r["frameSections"] = Cap(r, "frameSections", FrameSectionRows(null, false, x), cap);
        r["areaSections"] = Cap(r, "areaSections", AreaSectionRows(null, x), cap);
        r["linkProperties"] = Cap(r, "linkProperties", LinkPropRows(null, x), cap);
        r["groups"] = Cap(r, "groups", GroupRows(), cap);
        r["loadPatterns"] = Cap(r, "loadPatterns", PatternRows(), cap);
        r["loadCases"] = Cap(r, "loadCases", CaseRows(null, false), cap);
        r["combos"] = Cap(r, "combos", ComboRows(null, false), cap);

        List<string> done = new List<string>(), notDone = new List<string>();
        foreach (KeyValuePair<string, int> kv in s.CaseStatus()) (kv.Value == 4 ? done : notDone).Add(kv.Key);
        r["results"] = O("available", M.GetModelIsLocked() && done.Count > 0, "casesFinished", done, "casesWithoutResults", notDone);
        r["indexSeconds"] = Math.Round(x.Seconds, 2);
        return r;
    }

    Dictionary<string, object> Extent(Index x, IEnumerable<string> joints)
    {
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        int n = 0;
        foreach (string p in joints)
        {
            double[] c;
            if (!x.Xyz.TryGetValue(p, out c)) continue;
            n++;
            for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], c[k]); hi[k] = Math.Max(hi[k], c[k]); }
        }
        if (n == 0) return null;
        return O("min", lo, "max", hi);
    }

    // ---------------------------------------------------------------- properties

    public List<Dictionary<string, object>> MaterialRows(string pattern, bool full)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        int n = 0; string[] names = null;
        M.PropMaterial.GetNameList(ref n, ref names, 0);   // 0: every type
        foreach (string m in names ?? new string[0])
        {
            if (!Like(m, pattern)) continue;
            eMatType type = eMatType.Steel; int color = 0; string notes = "", guid = "";
            M.PropMaterial.GetMaterial(m, ref type, ref color, ref notes, ref guid);
            Dictionary<string, object> d = O("name", m, "type", type.ToString());
            if (full)
            {
                double e = 0, u = 0, a = 0, g = 0, w = 0, ms = 0;
                if (M.PropMaterial.GetMPIsotropic(m, ref e, ref u, ref a, ref g, 0) == 0) { d["E"] = e; d["nu"] = u; d["alpha"] = a; d["G"] = g; }
                else d["isotropic"] = false;
                if (M.PropMaterial.GetWeightAndMass(m, ref w, ref ms, 0) == 0) { d["weightPerVolume"] = w; d["massPerVolume"] = ms; }
                if (type == eMatType.Steel)
                {
                    double fy = 0, fu = 0, efy = 0, efu = 0, h = 0, sm = 0, sr = 0, fs = 0; int sst = 0, hys = 0;
                    if (M.PropMaterial.GetOSteel_1(m, ref fy, ref fu, ref efy, ref efu, ref sst, ref hys, ref h, ref sm, ref sr, ref fs, 0) == 0)
                    { d["Fy"] = fy; d["Fu"] = fu; d["expectedFy"] = efy; d["expectedFu"] = efu; }
                }
                else if (type == eMatType.Concrete)
                {
                    double fc = 0, fcs = 0, ec = 0, eu = 0, fsl = 0, fr = 0, di = 0; bool lw = false; int sst = 0, hys = 0;
                    if (M.PropMaterial.GetOConcrete_1(m, ref fc, ref lw, ref fcs, ref sst, ref hys, ref ec, ref eu, ref fsl, ref fr, ref di, 0) == 0)
                    { d["fc"] = fc; d["lightweight"] = lw; }
                }
            }
            rows.Add(d);
        }
        return rows;
    }

    // full: dimensions, section properties and modifiers. x: use counts (null = none).
    public List<Dictionary<string, object>> FrameSectionRows(string pattern, bool full, Index x)
    {
        Dictionary<string, int> used = x == null ? null : CountValues(x.FrameSection);
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        int n = 0; string[] names = null;
        M.PropFrame.GetNameList(ref n, ref names, 0);
        foreach (string p in names ?? new string[0])
        {
            if (!Like(p, pattern)) continue;
            eFramePropType type = eFramePropType.I;
            M.PropFrame.GetTypeOAPI(p, ref type);
            string mat = "";
            M.PropFrame.GetMaterial(p, ref mat);
            Dictionary<string, object> d = O("name", p, "type", type.ToString(), "material", mat);
            if (used != null) d["frames"] = used.ContainsKey(p) ? used[p] : 0;
            if (full)
            {
                Dictionary<string, object> dims = FrameDims(p, type);
                if (dims != null) d["dimensions"] = dims;
                else d["dimensions"] = "not read for this type: read_table \"Frame Section Properties 01 - General\"";
                double A = 0, As2 = 0, As3 = 0, J = 0, I22 = 0, I33 = 0, S22 = 0, S33 = 0, Z22 = 0, Z33 = 0, r22 = 0, r33 = 0;
                if (M.PropFrame.GetSectProps(p, ref A, ref As2, ref As3, ref J, ref I22, ref I33, ref S22, ref S33, ref Z22, ref Z33, ref r22, ref r33) == 0)
                    d["properties"] = O("A", A, "As2", As2, "As3", As3, "J", J, "I22", I22, "I33", I33, "S22", S22, "S33", S33, "Z22", Z22, "Z33", Z33, "r22", r22, "r33", r33);
                double[] mod = new double[8];
                if (M.PropFrame.GetModifiers(p, ref mod) == 0 && !AllOnes(mod))
                    d["modifiers"] = O("A", mod[0], "As2", mod[1], "As3", mod[2], "J", mod[3], "I22", mod[4], "I33", mod[5], "mass", mod[6], "weight", mod[7]);
            }
            rows.Add(d);
        }
        return rows;
    }

    Dictionary<string, object> FrameDims(string p, eFramePropType type)
    {
        string file = "", m = "", notes = "", guid = ""; int color = 0;
        double t3 = 0, t2 = 0, tf = 0, tw = 0, t2b = 0, tfb = 0;
        switch (type)
        {
            case eFramePropType.I:
                if (M.PropFrame.GetISection(p, ref file, ref m, ref t3, ref t2, ref tf, ref tw, ref t2b, ref tfb, ref color, ref notes, ref guid) == 0)
                    return O("t3", t3, "t2", t2, "tf", tf, "tw", tw, "t2b", t2b, "tfb", tfb);
                break;
            case eFramePropType.Box:
                if (M.PropFrame.GetTube(p, ref file, ref m, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid) == 0)
                    return O("t3", t3, "t2", t2, "tf", tf, "tw", tw);
                break;
            case eFramePropType.Pipe:
                if (M.PropFrame.GetPipe(p, ref file, ref m, ref t3, ref tw, ref color, ref notes, ref guid) == 0)
                    return O("t3", t3, "tw", tw);
                break;
            case eFramePropType.Rectangular:
                if (M.PropFrame.GetRectangle(p, ref file, ref m, ref t3, ref t2, ref color, ref notes, ref guid) == 0)
                    return O("t3", t3, "t2", t2);
                break;
            case eFramePropType.Circle:
                if (M.PropFrame.GetCircle(p, ref file, ref m, ref t3, ref color, ref notes, ref guid) == 0)
                    return O("t3", t3);
                break;
            case eFramePropType.Angle:
                if (M.PropFrame.GetAngle(p, ref file, ref m, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid) == 0)
                    return O("t3", t3, "t2", t2, "tf", tf, "tw", tw);
                break;
            case eFramePropType.Channel:
                if (M.PropFrame.GetChannel(p, ref file, ref m, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid) == 0)
                    return O("t3", t3, "t2", t2, "tf", tf, "tw", tw);
                break;
        }
        return null;
    }

    public List<Dictionary<string, object>> AreaSectionRows(string pattern, Index x)
    {
        Dictionary<string, int> used = x == null ? null : CountValues(x.AreaSection);
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        int n = 0; string[] names = null;
        M.PropArea.GetNameList(ref n, ref names, 0);
        foreach (string p in names ?? new string[0])
        {
            if (!Like(p, pattern)) continue;
            int pt = 0;
            M.PropArea.GetTypeOAPI(p, ref pt);
            Dictionary<string, object> d = O("name", p, "type", pt >= 0 && pt < AreaTypes.Length ? AreaTypes[pt] : pt.ToString(Inv));
            if (used != null) d["areas"] = used.ContainsKey(p) ? used[p] : 0;
            if (pt == 1)
            {
                int st = 0, color = 0; bool drill = false; string mat = "", notes = "", guid = ""; double ang = 0, th = 0, bend = 0;
                if (M.PropArea.GetShell_1(p, ref st, ref drill, ref mat, ref ang, ref th, ref bend, ref color, ref notes, ref guid) == 0)
                {
                    d["shellType"] = st >= 0 && st < ShellTypes.Length ? ShellTypes[st] : st.ToString(Inv);
                    d["material"] = mat; d["materialAngle"] = ang; d["membraneThickness"] = th; d["bendingThickness"] = bend; d["drillingDof"] = drill;
                }
            }
            double[] mod = new double[10];
            if (M.PropArea.GetModifiers(p, ref mod) == 0 && !AllOnes(mod))
                d["modifiers"] = O("f11", mod[0], "f22", mod[1], "f12", mod[2], "m11", mod[3], "m22", mod[4], "m12", mod[5], "v13", mod[6], "v23", mod[7], "mass", mod[8], "weight", mod[9]);
            rows.Add(d);
        }
        return rows;
    }

    public List<Dictionary<string, object>> LinkPropRows(string pattern, Index x)
    {
        Dictionary<string, int> used = x == null ? null : CountValues(x.LinkProp);
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        int n = 0; string[] names = null;
        M.PropLink.GetNameList(ref n, ref names, 0);
        foreach (string p in names ?? new string[0])
        {
            if (!Like(p, pattern)) continue;
            eLinkPropType t = eLinkPropType.Linear;
            M.PropLink.GetTypeOAPI(p, ref t);
            Dictionary<string, object> d = O("name", p, "type", t.ToString());
            if (used != null) d["links"] = used.ContainsKey(p) ? used[p] : 0;
            rows.Add(d);
        }
        return rows;
    }

    // ---------------------------------------------------------------- groups, patterns, cases, combos

    // Object type codes of GroupDef.GetAssignments.
    static string GroupKind(int t)
    {
        switch (t) { case 1: return "joints"; case 2: return "frames"; case 3: return "cables"; case 4: return "tendons"; case 5: return "areas"; case 6: return "solids"; case 7: return "links"; }
        return "other";
    }

    public List<Dictionary<string, object>> GroupRows()
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (string g in s.Groups())
        {
            int m = 0; int[] types = null; string[] objs = null;
            M.GroupDef.GetAssignments(g, ref m, ref types, ref objs);
            Dictionary<string, object> d = O("name", g);
            Dictionary<string, int> c = new Dictionary<string, int>();
            for (int k = 0; k < m; k++) Bump(c, GroupKind(types[k]));
            foreach (KeyValuePair<string, int> kv in c) d[kv.Key] = kv.Value;
            rows.Add(d);
        }
        return rows;
    }

    // Members of a group by kind ("joint", "frame", "area", "link"), in assignment order.
    public List<string> GroupMembers(string group, string kind)
    {
        int m = 0; int[] types = null; string[] objs = null;
        if (M.GroupDef.GetAssignments(group, ref m, ref types, ref objs) != 0) throw new ArgumentException("no group '" + group + "'");
        List<string> r = new List<string>();
        string want = kind + "s";
        for (int k = 0; k < m; k++) if (GroupKind(types[k]) == want) r.Add(objs[k]);
        return r;
    }

    public bool GroupExists(string group) { return Array.IndexOf(s.Groups(), group) >= 0; }

    public List<Dictionary<string, object>> PatternRows()
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (string p in NameList(M.LoadPatterns.GetNameList))
        {
            eLoadPatternType t = eLoadPatternType.Dead; double sw = 0;
            M.LoadPatterns.GetLoadType(p, ref t);
            M.LoadPatterns.GetSelfWTMultiplier(p, ref sw);
            rows.Add(O("name", p, "type", t.ToString(), "selfWeightMultiplier", sw));
        }
        return rows;
    }

    // full: the loads applied (static cases) and the initial case (nonlinear).
    public List<Dictionary<string, object>> CaseRows(string pattern, bool full)
    {
        Dictionary<string, int> status = new Dictionary<string, int>();
        foreach (KeyValuePair<string, int> kv in s.CaseStatus()) status[kv.Key] = kv.Value;
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (string c in s.LoadCases())
        {
            if (!Like(c, pattern)) continue;
            eLoadCaseType t = eLoadCaseType.LinearStatic; int sub = 0;
            M.LoadCases.GetTypeOAPI(c, ref t, ref sub);
            int st;
            Dictionary<string, object> d = O("name", c, "type", t.ToString(),
                "status", status.TryGetValue(c, out st) && st >= 1 && st <= 4 ? CaseStates[st] : "?");
            if (full)
            {
                int n = 0; string[] lt = null, ln = null; double[] sf = null; string init = "";
                bool got = false;
                if (t == eLoadCaseType.LinearStatic)
                {
                    got = M.LoadCases.StaticLinear.GetLoads(c, ref n, ref lt, ref ln, ref sf) == 0;
                    if (M.LoadCases.StaticLinear.GetInitialCase(c, ref init) == 0) d["initialCase"] = init.Length == 0 ? "(zero)" : init;
                }
                else if (t == eLoadCaseType.NonlinearStatic)
                {
                    got = M.LoadCases.StaticNonlinear.GetLoads(c, ref n, ref lt, ref ln, ref sf) == 0;
                    if (M.LoadCases.StaticNonlinear.GetInitialCase(c, ref init) == 0) d["initialCase"] = init.Length == 0 ? "(zero)" : init;
                }
                if (got)
                {
                    List<object> loads = new List<object>();
                    for (int k = 0; k < n; k++) loads.Add(O("type", lt[k], "name", ln[k], "scale", sf[k]));
                    d["loads"] = loads;
                }
                else d["definition"] = "not read for this type: read_table \"Load Case Definitions\" and the \"Case - ...\" tables";
            }
            rows.Add(d);
        }
        return rows;
    }

    public List<Dictionary<string, object>> ComboRows(string pattern, bool full)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (string c in NameList(M.RespCombo.GetNameList))
        {
            if (!Like(c, pattern)) continue;
            int t = 0;
            M.RespCombo.GetTypeOAPI(c, ref t);
            int n = 0; eCNameType[] it = null; string[] items = null; double[] sf = null;
            M.RespCombo.GetCaseList(c, ref n, ref it, ref items, ref sf);
            Dictionary<string, object> d = O("name", c, "type", t >= 0 && t < ComboTypes.Length ? ComboTypes[t] : t.ToString(Inv), "items", n);
            if (full)
            {
                List<object> list = new List<object>();
                for (int k = 0; k < n; k++) list.Add(O("kind", it[k] == eCNameType.LoadCombo ? "combo" : "case", "name", items[k], "scale", sf[k]));
                d["items"] = list;
            }
            rows.Add(d);
        }
        return rows;
    }

    // ---------------------------------------------------------------- objects

    // One page of objects of a kind (optionally only a group's / a given name list), with a short summary each.
    public Dictionary<string, object> ListObjects(string kind, List<string> only, int offset, int limit)
    {
        Index x = Objects();
        List<string> names = only ?? x.Names(kind);
        List<object> rows = new List<object>();
        for (int i = Math.Max(0, offset); i < names.Count && rows.Count < limit; i++) rows.Add(Brief(x, kind, names[i]));
        Dictionary<string, object> r = O("kind", kind, "units", x.Units, "total", names.Count, "offset", offset, "returned", rows.Count, "rows", rows);
        if (offset + rows.Count < names.Count) r["nextOffset"] = offset + rows.Count;
        return r;
    }

    Dictionary<string, object> Brief(Index x, string kind, string name)
    {
        double[] c;
        switch (kind)
        {
            case "joint":
                return O("name", name, "xyz", x.Xyz.TryGetValue(name, out c) ? c : null);
            case "frame":
                {
                    string[] e = x.PointsOf(kind, name);
                    return O("name", name, "i", e.Length > 0 ? e[0] : null, "j", e.Length > 1 ? e[1] : null, "section", x.PropOf(kind, name), "length", Length(x, e));
                }
            case "area":
                return O("name", name, "points", x.PointsOf(kind, name), "section", x.PropOf(kind, name));
            case "link":
                {
                    string[] e = x.PointsOf(kind, name);
                    return O("name", name, "i", e.Length > 0 ? e[0] : null, "j", e.Length > 1 ? e[1] : null, "property", x.PropOf(kind, name), "length", Length(x, e));
                }
        }
        return O("name", name);
    }

    static object Length(Index x, string[] e)
    {
        double[] a, b;
        if (e.Length < 2 || !x.Xyz.TryGetValue(e[0], out a) || !x.Xyz.TryGetValue(e[1], out b)) return null;
        return Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]) + (b[2] - a[2]) * (b[2] - a[2]));
    }

    // One object in full: geometry, property, local axes, releases / restraints, groups, loads, what connects to it.
    public Dictionary<string, object> GetObject(string kind, string name)
    {
        Index x = Objects();
        if (x.Names(kind).IndexOf(name) < 0) throw new ArgumentException("no " + kind + " named '" + name + "'");
        Dictionary<string, object> r = O("kind", kind, "name", name, "units", x.Units);
        if (kind == "joint")
        {
            r["xyz"] = x.Xyz[name];
            bool[] rr = new bool[6];
            M.PointObj.GetRestraint(name, ref rr);
            r["restraint"] = Flags(rr, GlobalDof);
            double[] k = new double[6];
            if (M.PointObj.GetSpring(name, ref k) == 0 && k != null && !AllZero(k)) r["spring"] = O("U1", k[0], "U2", k[1], "U3", k[2], "R1", k[3], "R2", k[4], "R3", k[5]);
            int nc = 0; string[] pn = null, cn = null;
            if (M.PointObj.GetConstraint(name, ref nc, ref pn, ref cn, eItemType.Objects) == 0 && nc > 0) r["constraints"] = cn;
            double a = 0, b = 0, cc = 0; bool adv = false;
            M.PointObj.GetLocalAxes(name, ref a, ref b, ref cc, ref adv);
            if (a != 0 || b != 0 || cc != 0 || adv) r["localAxes"] = O("a", a, "b", b, "c", cc, "advanced", adv, "note", "joint results are reported in these local axes");
            r["connected"] = Connected(x, name);
            r["loads"] = JointLoads(name);
        }
        else if (kind == "frame")
        {
            string[] e = x.FrameEnds[name];
            r["i"] = O("joint", e[0], "xyz", x.Xyz[e[0]]);
            r["j"] = O("joint", e[1], "xyz", x.Xyz[e[1]]);
            r["length"] = Length(x, e);
            string sec = "", auto = "";
            M.FrameObj.GetSection(name, ref sec, ref auto);
            r["section"] = sec;
            if (auto.Length > 0) r["autoSelectList"] = auto;
            double ang = 0; bool adv = false;
            M.FrameObj.GetLocalAxes(name, ref ang, ref adv);
            r["localAxes"] = Axes(ang, adv, Matrix(delegate(ref double[] m) { return M.FrameObj.GetTransformationMatrix(name, ref m, true); }));
            bool[] ii = new bool[6], jj = new bool[6]; double[] si = new double[6], sj = new double[6];
            M.FrameObj.GetReleases(name, ref ii, ref jj, ref si, ref sj);
            r["releases"] = O("i", Flags(ii, ReleaseDof), "j", Flags(jj, ReleaseDof));
            if (!AllZero(si) || !AllZero(sj)) r["releaseSprings"] = O("i", si, "j", sj);
            int cp = 0; bool mir = false, stiff = false; double[] o1 = new double[3], o2 = new double[3]; string cs = "";
            if (M.FrameObj.GetInsertionPoint(name, ref cp, ref mir, ref stiff, ref o1, ref o2, ref cs) == 0 && (cp != 10 || mir || !AllZero(o1) || !AllZero(o2)))
                r["insertionPoint"] = O("cardinalPoint", cp, "mirror2", mir, "stiffnessTransform", stiff, "offsetI", o1, "offsetJ", o2, "csys", cs);
            bool autoOff = false; double l1 = 0, l2 = 0, rz = 0;
            if (M.FrameObj.GetEndLengthOffset(name, ref autoOff, ref l1, ref l2, ref rz) == 0 && (autoOff || l1 != 0 || l2 != 0))
                r["endLengthOffset"] = O("auto", autoOff, "i", l1, "j", l2, "rigidZoneFactor", rz);
            double[] mod = new double[8];
            if (M.FrameObj.GetModifiers(name, ref mod) == 0 && !AllOnes(mod))
                r["modifiers"] = O("A", mod[0], "As2", mod[1], "As3", mod[2], "J", mod[3], "I22", mod[4], "I33", mod[5], "mass", mod[6], "weight", mod[7]);
            r["loads"] = FrameLoads(name);
        }
        else if (kind == "area")
        {
            string[] pts = x.AreaPoints[name];
            List<object> corners = new List<object>();
            foreach (string p in pts) corners.Add(O("joint", p, "xyz", x.Xyz.ContainsKey(p) ? x.Xyz[p] : null));
            r["points"] = corners;
            r["section"] = x.AreaSection[name];
            double ang = 0; bool adv = false;
            M.AreaObj.GetLocalAxes(name, ref ang, ref adv);
            r["localAxes"] = Axes(ang, adv, Matrix(delegate(ref double[] m) { return M.AreaObj.GetTransformationMatrix(name, ref m, true); }));
            double[] mod = new double[10];
            if (M.AreaObj.GetModifiers(name, ref mod) == 0 && !AllOnes(mod)) r["modifiers"] = mod;
            r["loads"] = AreaLoads(name);
        }
        else
        {
            string[] e = x.LinkEnds[name];
            r["i"] = O("joint", e[0], "xyz", x.Xyz.ContainsKey(e[0]) ? x.Xyz[e[0]] : null);
            r["j"] = e[1] == e[0] || e[1].Length == 0 ? (object)"(one-joint link)" : O("joint", e[1], "xyz", x.Xyz.ContainsKey(e[1]) ? x.Xyz[e[1]] : null);
            r["length"] = Length(x, e);
            r["property"] = x.LinkProp[name];
            double ang = 0; bool adv = false;
            M.LinkObj.GetLocalAxes(name, ref ang, ref adv);
            r["localAxes"] = Axes(ang, adv, Matrix(delegate(ref double[] m) { return M.LinkObj.GetTransformationMatrix(name, ref m, true); }));
        }
        r["groups"] = ObjectGroups(kind, name);
        return r;
    }

    static readonly string[] ReleaseDof = { "P", "V2", "V3", "T", "M2", "M3" };

    delegate int MatrixFn(ref double[] m);
    static double[] Matrix(MatrixFn fn) { double[] m = new double[9]; return fn(ref m) == 0 ? m : null; }

    // Transformation matrix is row-major (global = T * local): its columns are local 1/2/3 in global.
    static Dictionary<string, object> Axes(double ang, bool adv, double[] tm)
    {
        Dictionary<string, object> d = O("angle", ang, "advanced", adv);
        if (tm != null)
        {
            d["local1"] = new double[] { tm[0], tm[3], tm[6] };
            d["local2"] = new double[] { tm[1], tm[4], tm[7] };
            d["local3"] = new double[] { tm[2], tm[5], tm[8] };
        }
        return d;
    }

    string[] ObjectGroups(string kind, string name)
    {
        int n = 0; string[] g = null;
        switch (kind)
        {
            case "joint": M.PointObj.GetGroupAssign(name, ref n, ref g); break;
            case "frame": M.FrameObj.GetGroupAssign(name, ref n, ref g); break;
            case "area": M.AreaObj.GetGroupAssign(name, ref n, ref g); break;
            case "link": M.LinkObj.GetGroupAssign(name, ref n, ref g); break;
        }
        return g ?? new string[0];
    }

    // Frames, areas and links that use a joint (from the index).
    static Dictionary<string, object> Connected(Index x, string joint)
    {
        List<string> f = new List<string>(), a = new List<string>(), l = new List<string>();
        foreach (KeyValuePair<string, string[]> kv in x.FrameEnds) if (Array.IndexOf(kv.Value, joint) >= 0) f.Add(kv.Key);
        foreach (KeyValuePair<string, string[]> kv in x.AreaPoints) if (Array.IndexOf(kv.Value, joint) >= 0) a.Add(kv.Key);
        foreach (KeyValuePair<string, string[]> kv in x.LinkEnds) if (Array.IndexOf(kv.Value, joint) >= 0) l.Add(kv.Key);
        return O("frames", f, "areas", a, "links", l);
    }

    List<object> JointLoads(string name)
    {
        List<object> r = new List<object>();
        int n = 0; string[] pn = null, lp = null, cs = null; int[] step = null;
        double[] f1 = null, f2 = null, f3 = null, m1 = null, m2 = null, m3 = null;
        if (M.PointObj.GetLoadForce(name, ref n, ref pn, ref lp, ref step, ref cs, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3, eItemType.Objects) == 0)
            for (int k = 0; k < n; k++) r.Add(O("load", "force", "pattern", lp[k], "csys", cs[k], "F1", f1[k], "F2", f2[k], "F3", f3[k], "M1", m1[k], "M2", m2[k], "M3", m3[k]));
        n = 0; pn = null; lp = null; cs = null; step = null;
        if (M.PointObj.GetLoadDispl(name, ref n, ref pn, ref lp, ref step, ref cs, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3, eItemType.Objects) == 0)
            for (int k = 0; k < n; k++) r.Add(O("load", "ground displacement", "pattern", lp[k], "csys", cs[k], "U1", f1[k], "U2", f2[k], "U3", f3[k], "R1", m1[k], "R2", m2[k], "R3", m3[k]));
        return r;
    }

    // Load direction codes of frame / area loads: 1-3 local, 4-6 global X/Y/Z, 7-9 projected X/Y/Z, 10 gravity, 11 projected gravity.
    static string LoadDir(int d)
    {
        string[] n = { "?", "local 1", "local 2", "local 3", "X", "Y", "Z", "X projected", "Y projected", "Z projected", "gravity", "gravity projected" };
        return d >= 0 && d < n.Length ? n[d] : d.ToString(Inv);
    }

    List<object> FrameLoads(string name)
    {
        List<object> r = new List<object>();
        int n = 0; string[] fn = null, lp = null, cs = null; int[] type = null, dir = null;
        double[] rd1 = null, rd2 = null, d1 = null, d2 = null, v1 = null, v2 = null;
        if (M.FrameObj.GetLoadDistributed(name, ref n, ref fn, ref lp, ref type, ref cs, ref dir, ref rd1, ref rd2, ref d1, ref d2, ref v1, ref v2, eItemType.Objects) == 0)
            for (int k = 0; k < n; k++)
                r.Add(O("load", type[k] == 2 ? "distributed moment" : "distributed force", "pattern", lp[k], "csys", cs[k], "dir", LoadDir(dir[k]),
                    "from", d1[k], "to", d2[k], "relFrom", rd1[k], "relTo", rd2[k], "valueFrom", v1[k], "valueTo", v2[k]));
        n = 0; fn = null; lp = null; cs = null; type = null; dir = null;
        double[] rel = null, dist = null, val = null;
        if (M.FrameObj.GetLoadPoint(name, ref n, ref fn, ref lp, ref type, ref cs, ref dir, ref rel, ref dist, ref val, eItemType.Objects) == 0)
            for (int k = 0; k < n; k++)
                r.Add(O("load", type[k] == 2 ? "point moment" : "point force", "pattern", lp[k], "csys", cs[k], "dir", LoadDir(dir[k]),
                    "at", dist[k], "relAt", rel[k], "value", val[k]));
        return r;
    }

    List<object> AreaLoads(string name)
    {
        List<object> r = new List<object>();
        int n = 0; string[] an = null, lp = null, cs = null; int[] dir = null; double[] val = null;
        if (M.AreaObj.GetLoadUniform(name, ref n, ref an, ref lp, ref cs, ref dir, ref val, eItemType.Objects) == 0)
            for (int k = 0; k < n; k++) r.Add(O("load", "uniform", "pattern", lp[k], "csys", cs[k], "dir", LoadDir(dir[k]), "value", val[k]));
        n = 0; an = null; lp = null;
        int[] face = null; double[] v = null; string[] jp = null;
        if (M.AreaObj.GetLoadSurfacePressure(name, ref n, ref an, ref lp, ref face, ref v, ref jp, eItemType.Objects) == 0)
            for (int k = 0; k < n; k++) r.Add(O("load", "surface pressure", "pattern", lp[k], "face", face[k], "value", v[k], "jointPattern", jp[k]));
        n = 0; an = null; lp = null; cs = null;
        double[] gx = null, gy = null, gz = null;
        if (M.AreaObj.GetLoadGravity(name, ref n, ref an, ref lp, ref cs, ref gx, ref gy, ref gz, eItemType.Objects) == 0)
            for (int k = 0; k < n; k++) r.Add(O("load", "gravity multiplier", "pattern", lp[k], "csys", cs[k], "x", gx[k], "y", gy[k], "z", gz[k]));
        return r;
    }

    // ---------------------------------------------------------------- selection

    public sealed class Filter
    {
        public string[] Kinds;            // null = all four
        public double? ZMin, ZMax;
        public double[] Box;              // xmin, ymin, zmin, xmax, ymax, zmax
        public string PlaneAxis;          // "x" | "y" | "z"
        public double PlaneValue, Tol;
        public string Property;           // section / property name, * wildcards
        public Dictionary<string, HashSet<string>> GroupKinds;   // per kind: only these names (group / saved selection)
        public string NameLike;
        public string Match = "all";      // "all" points inside, "any" point, "centroid"
    }

    // Objects passing every given criterion. Returns names per kind (all of them; the caller pages).
    public Dictionary<string, List<string>> Select(Filter f)
    {
        Index x = Objects();
        Dictionary<string, List<string>> r = new Dictionary<string, List<string>>();
        foreach (string kind in f.Kinds ?? Kinds)
        {
            List<string> hit = new List<string>();
            HashSet<string> inGroup = null;
            if (f.GroupKinds != null && !f.GroupKinds.TryGetValue(kind, out inGroup)) inGroup = new HashSet<string>();
            foreach (string name in x.Names(kind))
            {
                if (inGroup != null && !inGroup.Contains(name)) continue;
                if (f.NameLike != null && !Like(name, f.NameLike)) continue;
                if (f.Property != null && (kind == "joint" || !Like(x.PropOf(kind, name) ?? "", f.Property))) continue;
                if (!GeometryOk(x, f, x.PointsOf(kind, name))) continue;
                hit.Add(name);
            }
            r[kind] = hit;
        }
        return r;
    }

    static bool GeometryOk(Index x, Filter f, string[] pts)
    {
        if (f.ZMin == null && f.ZMax == null && f.Box == null && f.PlaneAxis == null) return true;
        List<double[]> cs = new List<double[]>();
        foreach (string p in pts) { double[] c; if (x.Xyz.TryGetValue(p, out c)) cs.Add(c); }
        if (cs.Count == 0) return false;
        if (f.Match == "centroid")
        {
            double[] m = new double[3];
            foreach (double[] c in cs) for (int k = 0; k < 3; k++) m[k] += c[k] / cs.Count;
            return Inside(f, m);
        }
        bool any = f.Match == "any";
        foreach (double[] c in cs)
        {
            bool inside = Inside(f, c);
            if (any && inside) return true;
            if (!any && !inside) return false;
        }
        return !any;
    }

    static bool Inside(Filter f, double[] c)
    {
        double t = f.Tol;
        if (f.ZMin != null && c[2] < f.ZMin.Value - t) return false;
        if (f.ZMax != null && c[2] > f.ZMax.Value + t) return false;
        if (f.Box != null)
            for (int k = 0; k < 3; k++) if (c[k] < f.Box[k] - t || c[k] > f.Box[k + 3] + t) return false;
        if (f.PlaneAxis != null)
        {
            int k = f.PlaneAxis == "x" ? 0 : f.PlaneAxis == "y" ? 1 : 2;
            if (Math.Abs(c[k] - f.PlaneValue) > t) return false;
        }
        return true;
    }

    // Summary of a selection: count, extent and property use per kind.
    public Dictionary<string, object> SelectionSummary(Dictionary<string, List<string>> sel)
    {
        Index x = Objects();
        Dictionary<string, object> r = new Dictionary<string, object>();
        foreach (KeyValuePair<string, List<string>> kv in sel)
        {
            HashSet<string> joints = new HashSet<string>();
            Dictionary<string, int> props = new Dictionary<string, int>();
            foreach (string n in kv.Value)
            {
                foreach (string p in x.PointsOf(kv.Key, n)) joints.Add(p);
                string pr = x.PropOf(kv.Key, n);
                if (pr != null) Bump(props, pr);
            }
            Dictionary<string, object> d = O("count", kv.Value.Count);
            if (kv.Value.Count > 0) d["extent"] = Extent(x, joints);
            if (props.Count > 0) d["byProperty"] = props;
            r[kv.Key] = d;
        }
        return r;
    }

    // Per-object GUI selection costs ~30 ms an object (SAP 26.3, 2026-10-12: 505 joints in 15 s); a whole
    // group goes through SelectObj.Group in one call.
    public const int ShowCap = 2000;

    // Selects the objects in the SAP2000 window (GUI selection only; the model is not changed). wholeGroup:
    // the selection is exactly that group. Returns the number selected, or -total when over ShowCap (nothing done).
    public int ShowSelection(Dictionary<string, List<string>> sel, string wholeGroup)
    {
        int total = 0;
        foreach (List<string> v in sel.Values) total += v.Count;
        M.SelectObj.ClearSelection();
        if (wholeGroup != null)
        {
            M.SelectObj.Group(wholeGroup, false);
            M.View.RefreshView(0, false);
            return total;
        }
        if (total > ShowCap) return -total;
        int n = 0;
        foreach (KeyValuePair<string, List<string>> kv in sel)
            foreach (string name in kv.Value)
            {
                int ret = 1;
                switch (kv.Key)
                {
                    case "joint": ret = M.PointObj.SetSelected(name, true, eItemType.Objects); break;
                    case "frame": ret = M.FrameObj.SetSelected(name, true, eItemType.Objects); break;
                    case "area": ret = M.AreaObj.SetSelected(name, true, eItemType.Objects); break;
                    case "link": ret = M.LinkObj.SetSelected(name, true, eItemType.Objects); break;
                }
                if (ret == 0) n++;
            }
        M.View.RefreshView(0, false);
        return n;
    }

    // ---------------------------------------------------------------- levels

    // Candidate floor elevations: clusters of joint Z (within tol) with the horizontal frames / areas lying
    // on them, grid Z lines, and groups whose names look like levels with their Z range.
    public Dictionary<string, object> Levels(double tol, int cap)
    {
        Index x = Objects();
        Dictionary<string, object> ext = Extent(x, x.Joints);
        if (ext == null) return O("units", x.Units, "message", "no joints");
        double zlo = ((double[])ext["min"])[2], zhi = ((double[])ext["max"])[2];
        if (tol <= 0) tol = Math.Max(1e-6, 0.002 * (zhi - zlo));

        List<double> zs = new List<double>();
        foreach (double[] c in x.Xyz.Values) zs.Add(c[2]);
        zs.Sort();
        List<double[]> clusters = new List<double[]>();   // zmin, zmax, sum, count
        foreach (double z in zs)
        {
            double[] last = clusters.Count > 0 ? clusters[clusters.Count - 1] : null;
            if (last != null && z - last[1] <= tol) { last[1] = z; last[2] += z; last[3]++; }
            else clusters.Add(new double[] { z, z, z, 1 });
        }
        List<Dictionary<string, object>> levels = new List<Dictionary<string, object>>();
        int horizTotal = 0;
        foreach (double[] cl in clusters)
        {
            double zc = cl[2] / cl[3];
            int hf = Horizontal(x, "frame", zc, tol), ha = Horizontal(x, "area", zc, tol);
            horizTotal += hf + ha;
            levels.Add(O("z", zc, "zRange", new double[] { cl[0], cl[1] }, "joints", (int)cl[3], "horizontalFrames", hf, "horizontalAreas", ha));
        }
        int floorMin = Math.Max(3, horizTotal / 50);
        foreach (Dictionary<string, object> l in levels) l["floorLike"] = (int)l["horizontalFrames"] + (int)l["horizontalAreas"] >= floorMin;

        List<object> grid = new List<object>();
        foreach (Dictionary<string, string> row in s.TableRows("Grid Lines"))
        {
            string dir, id, coord;
            if (row.TryGetValue("AxisDir", out dir) && dir.Trim().ToUpperInvariant() == "Z")
            {
                row.TryGetValue("GridID", out id);
                if (!row.TryGetValue("XRYZCoord", out coord)) row.TryGetValue("Coord", out coord);
                grid.Add(O("id", id, "z", coord, "csys", row.ContainsKey("CoordSys") ? row["CoordSys"] : null));
            }
        }

        List<object> named = new List<object>();
        Regex levelName = new Regex(@"(level|floor|story|storey|roof|deck|mezz|^l\d|^el\.?\s*\d|^fl\d)", RegexOptions.IgnoreCase);
        foreach (string g in s.Groups())
        {
            if (!levelName.IsMatch(g)) continue;
            HashSet<string> joints = new HashSet<string>();
            foreach (string kind in Kinds) foreach (string n in GroupMembers(g, kind)) foreach (string p in x.PointsOf(kind, n)) joints.Add(p);
            Dictionary<string, object> e = Extent(x, joints);
            named.Add(O("group", g, "joints", joints.Count, "zRange", e == null ? null : new double[] { ((double[])e["min"])[2], ((double[])e["max"])[2] }));
        }

        Dictionary<string, object> r = O("units", x.Units, "tolerance", tol, "zExtent", new double[] { zlo, zhi },
            "clusters", levels.Count, "floorLikeRule", "horizontal frames + areas >= " + floorMin);
        List<Dictionary<string, object>> floors = levels.FindAll(delegate(Dictionary<string, object> l) { return (bool)l["floorLike"]; });
        r["floorLike"] = Cap(r, "floorLike", floors, cap);
        r["allClusters"] = Cap(r, "allClusters", levels, cap);
        r["gridZLines"] = grid;
        r["levelNamedGroups"] = named;
        return r;
    }

    static int Horizontal(Index x, string kind, double z, double tol)
    {
        int n = 0;
        foreach (string name in x.Names(kind))
        {
            string[] pts = x.PointsOf(kind, name);
            if (pts.Length < 2) continue;
            bool flat = true;
            foreach (string p in pts) { double[] c; if (!x.Xyz.TryGetValue(p, out c) || Math.Abs(c[2] - z) > tol) { flat = false; break; } }
            if (flat) n++;
        }
        return n;
    }

    // ---------------------------------------------------------------- tables

    // DatabaseTables input tables: the list (search: substring of key or name) or one table's rows
    // (display precision; filter: field -> value, * wildcards, all must match).
    public Dictionary<string, object> Tables(string search, int offset, int limit)
    {
        List<string[]> all = s.AvailableTables();
        if (all == null) return O("available", false, "message", "this SAP2000 API has no DatabaseTables");
        List<object> rows = new List<object>();
        int total = 0;
        foreach (string[] t in all)
        {
            if (search != null && t[0].IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 && t[1].IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (total++ < offset || rows.Count >= limit) continue;
            rows.Add(t[0]);
        }
        Dictionary<string, object> r = O("tablesWithData", total, "offset", offset, "returned", rows.Count, "tables", rows,
            "note", "tables that hold data in this model; pass one as name to read its rows");
        if (offset + rows.Count < total) r["nextOffset"] = offset + rows.Count;
        return r;
    }

    public List<Dictionary<string, string>> TableRows(string table, string group, Dictionary<string, string> filter)
    {
        List<Dictionary<string, string>> rows = s.TableRows(table, group ?? "");
        if (filter == null || filter.Count == 0) return rows;
        return rows.FindAll(delegate(Dictionary<string, string> row)
        {
            foreach (KeyValuePair<string, string> kv in filter)
            {
                string v;
                if (!row.TryGetValue(kv.Key, out v) || !Like(v, kv.Value)) return false;
            }
            return true;
        });
    }

    // ---------------------------------------------------------------- helpers

    static Dictionary<string, object> O(params object[] kv)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }

    // First `cap` rows; when cut, r[key + "Total"] gives the full count.
    static List<Dictionary<string, object>> Cap(Dictionary<string, object> r, string key, List<Dictionary<string, object>> rows, int cap)
    {
        if (cap <= 0 || rows.Count <= cap) return rows;
        r[key + "Total"] = rows.Count;
        return rows.GetRange(0, cap);
    }

    // Case-insensitive match with * and ? wildcards; null pattern matches everything.
    public static bool Like(string value, string pattern)
    {
        if (pattern == null) return true;
        if (value == null) return false;
        if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0) return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
        string re = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, re, RegexOptions.IgnoreCase);
    }

    static string Flags(bool[] b, string[] names)
    {
        List<string> on = new List<string>();
        for (int k = 0; k < 6 && b != null && k < b.Length; k++) if (b[k]) on.Add(names[k]);
        return on.Count == 0 ? "-" : string.Join("+", on.ToArray());
    }

    static void Bump(Dictionary<string, int> d, string k) { int v; d[k] = (d.TryGetValue(k, out v) ? v : 0) + 1; }
    static int Sum(Dictionary<string, int> d) { int n = 0; foreach (int v in d.Values) n += v; return n; }

    static Dictionary<string, int> CountValues(Dictionary<string, string> d)
    {
        Dictionary<string, int> c = new Dictionary<string, int>();
        foreach (string v in d.Values) Bump(c, v ?? "");
        return c;
    }

    static bool AllOnes(double[] a) { if (a == null) return true; foreach (double v in a) if (v != 1) return false; return true; }
    static bool AllZero(double[] a) { if (a == null) return true; foreach (double v in a) if (v != 0) return false; return true; }
}
