// Edits to the model SAP2000 holds, each returning what it changed as before -> after (JSON-ready): sections and
// properties, materials, restraints, releases, joint / frame / area loads, load patterns, cases and combinations,
// groups, joint moves, new frames and deletes, through the object API (whole tables: SapTables). dryRun: nothing is
// applied, the reply says what would change. Used by the Pluto MCP server (scripts\mcp), which decides which file
// the edit goes into (a checkpoint, never the opened original) and logs it. A locked model (one with results)
// must be unlocked first; SapSession / the server do that. Values are in the model's present units. C# 5.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SAP2000v1;

public class SapEdit
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Dof = { "U1", "U2", "U3", "R1", "R2", "R3" };
    static readonly string[] ReleaseDof = { "P", "V2", "V3", "T", "M2", "M3" };
    public static readonly string[] ComboTypes = { "linear", "envelope", "absolute", "srss", "range" };

    readonly SapSession s;
    readonly SapRead read;

    public SapEdit(SapSession session, SapRead reader) { s = session; read = reader; }

    // Input tables (import_tables applies the edited ones through it).
    public SapTables Tables() { return new SapTables(s); }
    cSapModel M { get { return s.Model; } }

    // ---------------------------------------------------------------- reply

    // A list of changes plus counts. Unchanged objects (already as asked) are counted, not listed.
    sealed class Edits
    {
        public readonly List<object> Changes = new List<object>();
        public readonly List<object> Failed = new List<object>();
        public int Unchanged;
        public void Add(string kind, string name, object before, object after) { Changes.Add(O("kind", kind, "name", name, "before", before, "after", after)); }
        public void Fail(string name, string why) { Failed.Add(O("name", name, "error", why)); }
        public Dictionary<string, object> Reply(string what, bool dryRun)
        {
            Dictionary<string, object> r = O("edit", what, dryRun ? "wouldChange" : "changed", Changes.Count, "unchanged", Unchanged, "changes", Changes);
            if (Failed.Count > 0) r["failed"] = Failed;
            return r;
        }
    }

    // ---------------------------------------------------------------- sections and properties

    public Dictionary<string, object> SetFrameSection(IList<string> frames, string section, bool dryRun)
    {
        RequireName(PropNames("frame"), section, "frame section");
        SapRead.Index x = read.Objects();
        Edits e = new Edits();
        foreach (string f in frames)
        {
            string before = x.FrameSection[f];
            if (before == section) { e.Unchanged++; continue; }
            if (!dryRun && M.FrameObj.SetSection(f, section, eItemType.Objects, 0, 0) != 0) { e.Fail(f, "SetSection failed"); continue; }
            e.Add("frame", f, before, section);
        }
        return e.Reply("frame section -> " + section, dryRun);
    }

    public Dictionary<string, object> SetAreaSection(IList<string> areas, string section, bool dryRun)
    {
        RequireName(PropNames("area"), section, "area section");
        SapRead.Index x = read.Objects();
        Edits e = new Edits();
        foreach (string a in areas)
        {
            string before = x.AreaSection[a];
            if (before == section) { e.Unchanged++; continue; }
            if (!dryRun && M.AreaObj.SetProperty(a, section, eItemType.Objects) != 0) { e.Fail(a, "SetProperty failed"); continue; }
            e.Add("area", a, before, section);
        }
        return e.Reply("area section -> " + section, dryRun);
    }

    public Dictionary<string, object> SetLinkProperty(IList<string> links, string prop, bool dryRun)
    {
        RequireName(PropNames("link"), prop, "link property");
        SapRead.Index x = read.Objects();
        Edits e = new Edits();
        foreach (string l in links)
        {
            string before = x.LinkProp[l];
            if (before == prop) { e.Unchanged++; continue; }
            if (!dryRun && M.LinkObj.SetProperty(l, prop, eItemType.Objects) != 0) { e.Fail(l, "SetProperty failed"); continue; }
            e.Add("link", l, before, prop);
        }
        return e.Reply("link property -> " + prop, dryRun);
    }

    public string[] PropNames(string kind)
    {
        int n = 0; string[] a = null;
        if (kind == "frame") M.PropFrame.GetNameList(ref n, ref a, 0);
        else if (kind == "area") M.PropArea.GetNameList(ref n, ref a, 0);
        else if (kind == "link") M.PropLink.GetNameList(ref n, ref a, 0);
        else if (kind == "material") M.PropMaterial.GetNameList(ref n, ref a, 0);
        return a ?? new string[0];
    }

    // Material values (only those given change): E, nu, alpha (isotropic), weight per volume, Fy / Fu (steel), fc (concrete).
    public Dictionary<string, object> SetMaterial(string mat, Dictionary<string, double> values, bool dryRun)
    {
        RequireName(PropNames("material"), mat, "material");
        Dictionary<string, object> before = Material(mat);
        Dictionary<string, object> want = new Dictionary<string, object>(before);
        foreach (KeyValuePair<string, double> kv in values)
        {
            if (!before.ContainsKey(kv.Key)) throw new ArgumentException("material " + mat + " has no " + kv.Key + " (it has " + string.Join(", ", new List<string>(before.Keys).ToArray()) + ")");
            want[kv.Key] = kv.Value;
        }
        Edits e = new Edits();
        bool same = true;
        foreach (KeyValuePair<string, double> kv in values) if ((double)before[kv.Key] != kv.Value) same = false;
        if (same) { e.Unchanged++; return e.Reply("material " + mat, dryRun); }
        if (!dryRun)
        {
            if (values.ContainsKey("E") || values.ContainsKey("nu") || values.ContainsKey("alpha"))
                Check(M.PropMaterial.SetMPIsotropic(mat, (double)want["E"], (double)want["nu"], (double)want["alpha"], 0), "SetMPIsotropic " + mat);
            if (values.ContainsKey("weightPerVolume"))
                Check(M.PropMaterial.SetWeightAndMass(mat, 1, (double)want["weightPerVolume"], 0), "SetWeightAndMass " + mat);
            if (values.ContainsKey("Fy") || values.ContainsKey("Fu"))
            {
                double fy = 0, fu = 0, efy = 0, efu = 0, h = 0, sm = 0, sr = 0, fs = 0; int sst = 0, hys = 0;
                Check(M.PropMaterial.GetOSteel_1(mat, ref fy, ref fu, ref efy, ref efu, ref sst, ref hys, ref h, ref sm, ref sr, ref fs, 0), "GetOSteel_1 " + mat);
                Check(M.PropMaterial.SetOSteel_1(mat, (double)want["Fy"], (double)want["Fu"], efy, efu, sst, hys, h, sm, sr, fs, 0), "SetOSteel_1 " + mat);
            }
            if (values.ContainsKey("fc"))
            {
                double fc = 0, fcs = 0, ec = 0, eu = 0, fsl = 0, fr = 0, di = 0; bool lw = false; int sst = 0, hys = 0;
                Check(M.PropMaterial.GetOConcrete_1(mat, ref fc, ref lw, ref fcs, ref sst, ref hys, ref ec, ref eu, ref fsl, ref fr, ref di, 0), "GetOConcrete_1 " + mat);
                Check(M.PropMaterial.SetOConcrete_1(mat, (double)want["fc"], lw, fcs, sst, hys, ec, eu, fsl, fr, di, 0), "SetOConcrete_1 " + mat);
            }
        }
        e.Add("material", mat, before, dryRun ? want : Material(mat));
        return e.Reply("material " + mat, dryRun);
    }

    Dictionary<string, object> Material(string mat)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        double E = 0, u = 0, a = 0, g = 0, w = 0, ms = 0;
        if (M.PropMaterial.GetMPIsotropic(mat, ref E, ref u, ref a, ref g, 0) == 0) { d["E"] = E; d["nu"] = u; d["alpha"] = a; }
        if (M.PropMaterial.GetWeightAndMass(mat, ref w, ref ms, 0) == 0) d["weightPerVolume"] = w;
        double fy = 0, fu = 0, efy = 0, efu = 0, h = 0, sm = 0, sr = 0, fs = 0; int sst = 0, hys = 0;
        if (M.PropMaterial.GetOSteel_1(mat, ref fy, ref fu, ref efy, ref efu, ref sst, ref hys, ref h, ref sm, ref sr, ref fs, 0) == 0) { d["Fy"] = fy; d["Fu"] = fu; }
        double fc = 0, fcs = 0, ec = 0, eu = 0, fsl = 0, fr = 0, di = 0; bool lw = false;
        if (M.PropMaterial.GetOConcrete_1(mat, ref fc, ref lw, ref fcs, ref sst, ref hys, ref ec, ref eu, ref fsl, ref fr, ref di, 0) == 0) d["fc"] = fc;
        return d;
    }

    // ---------------------------------------------------------------- restraints and releases

    // restraint: U1 U2 U3 R1 R2 R3 (true = fixed); all false removes the support.
    public Dictionary<string, object> SetRestraint(IList<string> joints, bool[] restraint, bool dryRun)
    {
        Edits e = new Edits();
        string after = Flags(restraint, Dof);
        foreach (string j in joints)
        {
            bool[] r = new bool[6];
            M.PointObj.GetRestraint(j, ref r);
            string before = Flags(r, Dof);
            if (before == after) { e.Unchanged++; continue; }
            bool[] v = (bool[])restraint.Clone();
            if (!dryRun && M.PointObj.SetRestraint(j, ref v, eItemType.Objects) != 0) { e.Fail(j, "SetRestraint failed"); continue; }
            e.Add("joint", j, before, after);
        }
        return e.Reply("restraint -> " + after, dryRun);
    }

    // releases at the I and J ends: P V2 V3 T M2 M3 (true = released), no partial fixity. SAP2000 refuses unstable
    // combinations (e.g. the same force released at both ends).
    public Dictionary<string, object> SetFrameRelease(IList<string> frames, bool[] ii, bool[] jj, bool dryRun)
    {
        Edits e = new Edits();
        string after = "I " + Flags(ii, ReleaseDof) + ", J " + Flags(jj, ReleaseDof);
        foreach (string f in frames)
        {
            bool[] bi = new bool[6], bj = new bool[6]; double[] si = new double[6], sj = new double[6];
            M.FrameObj.GetReleases(f, ref bi, ref bj, ref si, ref sj);
            string before = "I " + Flags(bi, ReleaseDof) + ", J " + Flags(bj, ReleaseDof);
            if (before == after) { e.Unchanged++; continue; }
            if (!dryRun)
            {
                bool[] ni = (bool[])ii.Clone(), nj = (bool[])jj.Clone(); double[] zi = new double[6], zj = new double[6];
                if (M.FrameObj.SetReleases(f, ref ni, ref nj, ref zi, ref zj, eItemType.Objects) != 0) { e.Fail(f, "SetReleases refused (unstable release combination?)"); continue; }
            }
            e.Add("frame", f, before, after);
        }
        return e.Reply("frame releases -> " + after, dryRun);
    }

    // ---------------------------------------------------------------- loads

    void RequirePattern(string pattern)
    {
        int n = 0; string[] a = null;
        M.LoadPatterns.GetNameList(ref n, ref a);
        RequireName(a ?? new string[0], pattern, "load pattern");
    }

    // Joint forces F1 F2 F3 M1 M2 M3 in csys. replace: the joint's loads of this pattern are replaced (else added);
    // clear: they are deleted.
    public Dictionary<string, object> AssignJointLoad(IList<string> joints, string pattern, double[] f, string csys, bool replace, bool clear, bool dryRun)
    {
        RequirePattern(pattern);
        Edits e = new Edits();
        string what = clear ? "none" : F("F {0} {1} {2}, M {3} {4} {5} ({6})", f[0], f[1], f[2], f[3], f[4], f[5], csys) + (replace ? "" : " added");
        foreach (string j in joints)
        {
            List<object> before = JointForces(j, pattern);
            if (clear && before.Count == 0) { e.Unchanged++; continue; }
            if (!dryRun)
            {
                int ret;
                if (clear) ret = M.PointObj.DeleteLoadForce(j, pattern, eItemType.Objects);
                else { double[] v = (double[])f.Clone(); ret = M.PointObj.SetLoadForce(j, pattern, ref v, replace, csys, eItemType.Objects); }
                if (ret != 0) { e.Fail(j, "joint load call failed"); continue; }
            }
            e.Add("joint", j, before, dryRun ? (object)what : JointForces(j, pattern));
        }
        return e.Reply("joint load " + pattern + ": " + what, dryRun);
    }

    List<object> JointForces(string j, string pattern)
    {
        List<object> r = new List<object>();
        int n = 0; string[] pn = null, lp = null, cs = null; int[] step = null;
        double[] f1 = null, f2 = null, f3 = null, m1 = null, m2 = null, m3 = null;
        if (M.PointObj.GetLoadForce(j, ref n, ref pn, ref lp, ref step, ref cs, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3, eItemType.Objects) == 0)
            for (int k = 0; k < n; k++)
                if (lp[k] == pattern) r.Add(O("csys", cs[k], "F1", f1[k], "F2", f2[k], "F3", f3[k], "M1", m1[k], "M2", m2[k], "M3", m3[k]));
        return r;
    }

    // Frame loads. distributed: from d1 to d2 (relative 0..1 when rel, else length), v1 -> v2 per length; point: at d1, value v1.
    // dir: SapRead load direction codes (1-3 local, 4-6 global X/Y/Z, 7-9 projected, 10 gravity, 11 projected gravity).
    // moment: a moment instead of a force. replace: this pattern's loads of that type are replaced; clear: deleted.
    public Dictionary<string, object> AssignFrameLoad(IList<string> frames, string pattern, bool point, bool moment, int dir,
        double d1, double d2, double v1, double v2, bool rel, string csys, bool replace, bool clear, bool dryRun)
    {
        RequirePattern(pattern);
        if (dir < 1 || dir > 11) throw new ArgumentException("dir is 1-3 local, 4-6 global X/Y/Z, 7-9 projected X/Y/Z, 10 gravity, 11 projected gravity");
        Edits e = new Edits();
        string what = clear ? "none" : point
            ? F("{0} {1} at {2}{3}, dir {4} ({5})", moment ? "moment" : "force", v1, d1, rel ? " (relative)" : "", dir, csys)
            : F("{0} {1} -> {2} from {3} to {4}{5}, dir {6} ({7})", moment ? "moment" : "force", v1, v2, d1, d2, rel ? " (relative)" : "", dir, csys);
        if (!clear && !replace) what += " added";
        foreach (string f in frames)
        {
            List<object> before = FrameLoads(f, pattern, point);
            if (clear && before.Count == 0) { e.Unchanged++; continue; }
            if (!dryRun)
            {
                int ret;
                if (clear) ret = point ? M.FrameObj.DeleteLoadPoint(f, pattern, eItemType.Objects) : M.FrameObj.DeleteLoadDistributed(f, pattern, eItemType.Objects);
                else if (point) ret = M.FrameObj.SetLoadPoint(f, pattern, moment ? 2 : 1, dir, d1, v1, csys, rel, replace, eItemType.Objects);
                else ret = M.FrameObj.SetLoadDistributed(f, pattern, moment ? 2 : 1, dir, d1, d2, v1, v2, csys, rel, replace, eItemType.Objects);
                if (ret != 0) { e.Fail(f, "frame load call failed (distance beyond the frame?)"); continue; }
            }
            e.Add("frame", f, before, dryRun ? (object)what : FrameLoads(f, pattern, point));
        }
        return e.Reply((point ? "frame point load " : "frame distributed load ") + pattern + ": " + what, dryRun);
    }

    List<object> FrameLoads(string f, string pattern, bool point)
    {
        List<object> r = new List<object>();
        int n = 0; string[] fn = null, lp = null, cs = null; int[] type = null, dir = null;
        if (point)
        {
            double[] rel = null, dist = null, val = null;
            if (M.FrameObj.GetLoadPoint(f, ref n, ref fn, ref lp, ref type, ref cs, ref dir, ref rel, ref dist, ref val, eItemType.Objects) == 0)
                for (int k = 0; k < n; k++) if (lp[k] == pattern) r.Add(O("type", type[k] == 2 ? "moment" : "force", "dir", dir[k], "csys", cs[k], "dist", dist[k], "value", val[k]));
        }
        else
        {
            double[] rd1 = null, rd2 = null, d1 = null, d2 = null, v1 = null, v2 = null;
            if (M.FrameObj.GetLoadDistributed(f, ref n, ref fn, ref lp, ref type, ref cs, ref dir, ref rd1, ref rd2, ref d1, ref d2, ref v1, ref v2, eItemType.Objects) == 0)
                for (int k = 0; k < n; k++) if (lp[k] == pattern) r.Add(O("type", type[k] == 2 ? "moment" : "force", "dir", dir[k], "csys", cs[k], "dist1", d1[k], "dist2", d2[k], "val1", v1[k], "val2", v2[k]));
        }
        return r;
    }

    // Uniform area load (force per area) in dir (as frame loads). replace / clear as for frames.
    public Dictionary<string, object> AssignAreaLoad(IList<string> areas, string pattern, double value, int dir, string csys, bool replace, bool clear, bool dryRun)
    {
        RequirePattern(pattern);
        if (dir < 1 || dir > 11) throw new ArgumentException("dir is 1-3 local, 4-6 global X/Y/Z, 7-9 projected X/Y/Z, 10 gravity, 11 projected gravity");
        Edits e = new Edits();
        string what = clear ? "none" : F("uniform {0}, dir {1} ({2})", value, dir, csys) + (replace ? "" : " added");
        foreach (string a in areas)
        {
            List<object> before = AreaLoads(a, pattern);
            if (clear && before.Count == 0) { e.Unchanged++; continue; }
            if (!dryRun)
            {
                int ret = clear ? M.AreaObj.DeleteLoadUniform(a, pattern, eItemType.Objects) : M.AreaObj.SetLoadUniform(a, pattern, value, dir, replace, csys, eItemType.Objects);
                if (ret != 0) { e.Fail(a, "area load call failed"); continue; }
            }
            e.Add("area", a, before, dryRun ? (object)what : AreaLoads(a, pattern));
        }
        return e.Reply("area uniform load " + pattern + ": " + what, dryRun);
    }

    List<object> AreaLoads(string a, string pattern)
    {
        List<object> r = new List<object>();
        int n = 0; string[] an = null, lp = null, cs = null; int[] dir = null; double[] val = null;
        if (M.AreaObj.GetLoadUniform(a, ref n, ref an, ref lp, ref cs, ref dir, ref val, eItemType.Objects) == 0)
            for (int k = 0; k < n; k++) if (lp[k] == pattern) r.Add(O("dir", dir[k], "csys", cs[k], "value", val[k]));
        return r;
    }

    // ---------------------------------------------------------------- patterns, cases, combinations

    // A new pattern (with a linear static case of the same name), or a changed type / self-weight multiplier.
    public Dictionary<string, object> DefinePattern(string name, string type, double? selfWeight, bool dryRun)
    {
        int n = 0; string[] pats = null;
        M.LoadPatterns.GetNameList(ref n, ref pats);
        bool exists = Array.IndexOf(pats ?? new string[0], name) >= 0;
        eLoadPatternType t = eLoadPatternType.Dead;
        if (type != null) t = ParseEnum<eLoadPatternType>(type, "pattern type");
        Edits e = new Edits();
        Dictionary<string, object> before = null;
        if (exists)
        {
            eLoadPatternType bt = eLoadPatternType.Dead; double sw = 0;
            M.LoadPatterns.GetLoadType(name, ref bt);
            M.LoadPatterns.GetSelfWTMultiplier(name, ref sw);
            before = O("type", bt.ToString(), "selfWeightMultiplier", sw);
            if (type == null) t = bt;
            double nsw = selfWeight ?? sw;
            if (bt == t && sw == nsw) { e.Unchanged++; return e.Reply("load pattern " + name, dryRun); }
            if (!dryRun)
            {
                Check(M.LoadPatterns.SetLoadType(name, t), "SetLoadType " + name);
                Check(M.LoadPatterns.SetSelfWTMultiplier(name, nsw), "SetSelfWTMultiplier " + name);
            }
            e.Add("loadPattern", name, before, O("type", t.ToString(), "selfWeightMultiplier", nsw));
        }
        else
        {
            if (type == null) throw new ArgumentException("a new load pattern needs a type (Dead, Live, Wind, Quake, Snow, Other, ...)");
            if (!dryRun) Check(M.LoadPatterns.Add(name, t, selfWeight ?? 0, true), "LoadPatterns.Add " + name);
            e.Add("loadPattern", name, null, O("type", t.ToString(), "selfWeightMultiplier", selfWeight ?? 0, "case", "linear static case " + name + " added"));
        }
        return e.Reply("load pattern " + name, dryRun);
    }

    // A linear or nonlinear static case: its loads (pattern, scale) replace the old ones; initial: the case it
    // continues from ("" = zero initial conditions; null = unchanged).
    public Dictionary<string, object> DefineStaticCase(string name, bool nonlinear, IList<KeyValuePair<string, double>> loads, string initial, bool dryRun)
    {
        foreach (KeyValuePair<string, double> l in loads) RequirePattern(l.Key);
        List<string> cases = new List<string>(s.LoadCases());
        if (!string.IsNullOrEmpty(initial)) RequireName(cases.ToArray(), initial, "initial case");
        bool exists = cases.Contains(name);
        Dictionary<string, object> before = null;
        if (exists)
        {
            List<Dictionary<string, object>> rows = read.CaseRows(name, true);
            if (rows.Count > 0) before = rows[0];
            eLoadCaseType t = eLoadCaseType.LinearStatic; int sub = 0;
            M.LoadCases.GetTypeOAPI(name, ref t, ref sub);
            if (t != (nonlinear ? eLoadCaseType.NonlinearStatic : eLoadCaseType.LinearStatic))
                throw new ArgumentException("case " + name + " is " + t + ": define it under another name");
        }
        List<object> after = new List<object>();
        foreach (KeyValuePair<string, double> l in loads) after.Add(O("type", "Load", "name", l.Key, "scale", l.Value));
        if (!dryRun)
        {
            string[] lt = new string[loads.Count], ln = new string[loads.Count]; double[] sf = new double[loads.Count];
            for (int k = 0; k < loads.Count; k++) { lt[k] = "Load"; ln[k] = loads[k].Key; sf[k] = loads[k].Value; }
            if (nonlinear)
            {
                if (!exists) Check(M.LoadCases.StaticNonlinear.SetCase(name), "StaticNonlinear.SetCase " + name);
                Check(M.LoadCases.StaticNonlinear.SetLoads(name, loads.Count, ref lt, ref ln, ref sf), "StaticNonlinear.SetLoads " + name);
                if (initial != null) Check(M.LoadCases.StaticNonlinear.SetInitialCase(name, initial), "SetInitialCase " + name);
            }
            else
            {
                if (!exists) Check(M.LoadCases.StaticLinear.SetCase(name), "StaticLinear.SetCase " + name);
                Check(M.LoadCases.StaticLinear.SetLoads(name, loads.Count, ref lt, ref ln, ref sf), "StaticLinear.SetLoads " + name);
                if (initial != null) Check(M.LoadCases.StaticLinear.SetInitialCase(name, initial), "SetInitialCase " + name);
            }
        }
        Edits e = new Edits();
        e.Add("loadCase", name, before, O("type", nonlinear ? "NonlinearStatic" : "LinearStatic", "loads", after,
            "initialCase", initial == null ? (object)"unchanged" : initial.Length == 0 ? "(zero)" : initial));
        return e.Reply("load case " + name, dryRun);
    }

    // A combination (type: linear, envelope, absolute, srss, range) whose items (case or combo name, scale) replace the old ones.
    public Dictionary<string, object> DefineCombo(string name, string type, IList<KeyValuePair<string, double>> items, bool dryRun)
    {
        int ti = Array.IndexOf(ComboTypes, (type ?? "linear").ToLowerInvariant());
        if (ti < 0) throw new ArgumentException("combination type is " + string.Join(", ", ComboTypes));
        List<string> cases = new List<string>(s.LoadCases());
        int n = 0; string[] cn = null;
        M.RespCombo.GetNameList(ref n, ref cn);
        List<string> combos = new List<string>(cn ?? new string[0]);
        foreach (KeyValuePair<string, double> it in items)
            if (!cases.Contains(it.Key) && !combos.Contains(it.Key)) throw new ArgumentException("no load case or combination '" + it.Key + "'");
        if (cases.Contains(name)) throw new ArgumentException("'" + name + "' is a load case name");
        bool exists = combos.Contains(name);
        object before = null;
        if (exists) { List<Dictionary<string, object>> rows = read.ComboRows(name, true); if (rows.Count > 0) before = rows[0]; }
        List<object> after = new List<object>();
        foreach (KeyValuePair<string, double> it in items) after.Add(O("kind", combos.Contains(it.Key) ? "combo" : "case", "name", it.Key, "scale", it.Value));
        if (!dryRun)
        {
            if (exists) Check(M.RespCombo.Delete(name), "RespCombo.Delete " + name);
            Check(M.RespCombo.Add(name, ti), "RespCombo.Add " + name);
            foreach (KeyValuePair<string, double> it in items)
            {
                eCNameType ct = combos.Contains(it.Key) ? eCNameType.LoadCombo : eCNameType.LoadCase;
                Check(M.RespCombo.SetCaseList(name, ref ct, it.Key, it.Value), "RespCombo.SetCaseList " + name + " " + it.Key);
            }
        }
        Edits e = new Edits();
        e.Add("combo", name, before, O("type", ComboTypes[ti], "items", after));
        return e.Reply("combination " + name, dryRun);
    }

    // ---------------------------------------------------------------- groups and geometry

    // Adds and / or removes objects (kind -> names) to / from a group, creating it when missing.
    public Dictionary<string, object> SetGroup(string group, Dictionary<string, List<string>> add, Dictionary<string, List<string>> remove, bool dryRun)
    {
        bool exists = read.GroupExists(group);
        if (!exists && !dryRun) Check(M.GroupDef.SetGroup(group, -1, true, true, true, true, true, true, true, true, false, false, true), "GroupDef.SetGroup " + group);
        Edits e = new Edits();
        Dictionary<string, object> counts = new Dictionary<string, object>();
        foreach (string kind in new[] { "joint", "frame", "area", "link" })
        {
            HashSet<string> members = exists ? new HashSet<string>(read.GroupMembers(group, kind)) : new HashSet<string>();
            int before = members.Count;
            List<string> a, r;
            int added = 0, removed = 0;
            if (add != null && add.TryGetValue(kind, out a))
                foreach (string o in a)
                {
                    if (members.Contains(o)) { e.Unchanged++; continue; }
                    if (!dryRun && Assign(kind, o, group, false) != 0) { e.Fail(o, "group assign failed"); continue; }
                    members.Add(o); added++;
                }
            if (remove != null && remove.TryGetValue(kind, out r))
                foreach (string o in r)
                {
                    if (!members.Contains(o)) { e.Unchanged++; continue; }
                    if (!dryRun && Assign(kind, o, group, true) != 0) { e.Fail(o, "group remove failed"); continue; }
                    members.Remove(o); removed++;
                }
            if (added + removed > 0) e.Add(kind + "s", group, before, O("members", members.Count, "added", added, "removed", removed));
        }
        Dictionary<string, object> reply = e.Reply("group " + group, dryRun);
        if (!exists) reply["created"] = group;
        return reply;
    }

    int Assign(string kind, string obj, string group, bool remove)
    {
        switch (kind)
        {
            case "joint": return M.PointObj.SetGroupAssign(obj, group, remove, eItemType.Objects);
            case "frame": return M.FrameObj.SetGroupAssign(obj, group, remove, eItemType.Objects);
            case "area": return M.AreaObj.SetGroupAssign(obj, group, remove, eItemType.Objects);
            case "link": return M.LinkObj.SetGroupAssign(obj, group, remove, eItemType.Objects);
        }
        return 1;
    }

    // Where a move_joints rule puts a joint: exactly one of Delta (translate), To (one joint), Scale about About
    // (default the origin), or a piecewise-linear map of one axis (MapAxis 0..2, MapFrom strictly increasing -> MapTo;
    // a coordinate outside MapFrom's range is not mapped).
    public sealed class JointRule
    {
        public double[] Delta, To, Scale, About, MapFrom, MapTo;
        public int MapAxis = -1;
        public double Tol;   // a coordinate this close outside the map's range counts as its end

        public string Check()
        {
            int n = (Delta != null ? 1 : 0) + (To != null ? 1 : 0) + (Scale != null ? 1 : 0) + (MapAxis >= 0 ? 1 : 0);
            if (n != 1) return "give one rule: delta [dx, dy, dz], to [x, y, z], scale [sx, sy, sz] (about [x, y, z]) or map {axis, from, to}";
            if (About != null && Scale == null) return "about goes with scale";
            if (MapAxis >= 0)
            {
                if (MapFrom == null || MapTo == null || MapFrom.Length < 2 || MapFrom.Length != MapTo.Length) return "map needs from and to of the same length (at least 2)";
                for (int i = 1; i < MapFrom.Length; i++) if (!(MapFrom[i] > MapFrom[i - 1])) return "map.from must be strictly increasing";
            }
            return null;
        }

        public string Describe()
        {
            if (Delta != null) return F("delta [{0}, {1}, {2}]", Delta[0], Delta[1], Delta[2]);
            if (To != null) return F("to [{0}, {1}, {2}]", To[0], To[1], To[2]);
            if (Scale != null) { double[] a = About ?? new double[3]; return F("scale [{0}, {1}, {2}] about [{3}, {4}, {5}]", Scale[0], Scale[1], Scale[2], a[0], a[1], a[2]); }
            return "map " + "xyz"[MapAxis] + " [" + Join(MapFrom) + "] -> [" + Join(MapTo) + "]";
        }

        static string Join(double[] v) { string[] s = new string[v.Length]; for (int i = 0; i < v.Length; i++) s[i] = v[i].ToString("G10", Inv); return string.Join(", ", s); }

        // New coordinates, or null when the rule does not place this joint (outside the map's range).
        public double[] Apply(double[] c)
        {
            if (Delta != null) return new double[] { c[0] + Delta[0], c[1] + Delta[1], c[2] + Delta[2] };
            if (To != null) return (double[])To.Clone();
            if (Scale != null)
            {
                double[] a = About ?? new double[3];
                return new double[] { a[0] + Scale[0] * (c[0] - a[0]), a[1] + Scale[1] * (c[1] - a[1]), a[2] + Scale[2] * (c[2] - a[2]) };
            }
            double v = c[MapAxis];
            int last = MapFrom.Length - 1;
            if (v < MapFrom[0] - Tol || v > MapFrom[last] + Tol) return null;
            v = Math.Max(MapFrom[0], Math.Min(MapFrom[last], v));
            int k = 0;
            while (k < last - 1 && v > MapFrom[k + 1]) k++;
            double t = (v - MapFrom[k]) / (MapFrom[k + 1] - MapFrom[k]);
            double[] n = (double[])c.Clone();
            n[MapAxis] = MapTo[k] + t * (MapTo[k + 1] - MapTo[k]);
            return n;
        }
    }

    // Moves joints by one rule (JointRule). Connected objects follow. Before anything is applied the final geometry is
    // checked: frames / links that collapse to zero length or reverse, areas that flip or degenerate, joints that land on
    // another joint (each only when it was not so before), and joints connected to the moved ones that the rule would
    // also move but that were not among the targets (leftBehind: usually a forgotten ring or level). Collapsed, reversed,
    // flipped or coincident results are not applied unless force. A joint SAP2000 refuses because another joint is still
    // in the way is retried after the others have moved.
    const double SecondsPerJoint = 0.11;

    public Dictionary<string, object> MoveJoints(IList<string> joints, JointRule rule, bool force, bool dryRun)
    {
        string bad = rule.Check();
        if (bad != null) throw new ArgumentException(bad);
        if (rule.To != null && joints.Count != 1) throw new ArgumentException("'to' moves one joint; use delta, scale or map for several");
        SapRead.Index x = read.Objects();
        double tol = Math.Max(1e-6 * read.LargestExtent(), 1e-12);
        rule.Tol = tol;

        Dictionary<string, double[]> moved = new Dictionary<string, double[]>();
        int outside = 0, unchanged = 0;
        foreach (string j in joints)
        {
            double[] c = x.Xyz[j], n = rule.Apply(c);
            if (n == null) { outside++; continue; }
            if (Dist(c, n) <= tol) { unchanged++; continue; }
            moved[j] = n;
        }

        bool blocking;
        Dictionary<string, object> checks = MoveChecks(x, moved, rule, new HashSet<string>(joints), tol, out blocking);

        Edits e = new Edits();
        e.Unchanged = unchanged;
        bool apply = !dryRun && !(blocking && !force);
        List<string> todo = new List<string>(moved.Keys);
        int passes = 0;
        if (apply)
        {
            while (todo.Count > 0 && passes < 10)
            {
                passes++;
                List<string> refused = new List<string>();
                foreach (string j in todo)
                {
                    double[] n = moved[j];
                    if (M.EditPoint.ChangeCoordinates_1(j, n[0], n[1], n[2], true) != 0) refused.Add(j);
                }
                if (refused.Count == todo.Count) break;
                todo = refused;
            }
            foreach (string j in todo) e.Fail(j, "ChangeCoordinates refused (another joint there?)");
        }
        HashSet<string> failed = new HashSet<string>(apply ? todo : new List<string>());
        foreach (KeyValuePair<string, double[]> kv in moved)
            if (!failed.Contains(kv.Key)) e.Add("joint", kv.Key, x.Xyz[kv.Key], kv.Value);

        Dictionary<string, object> reply = e.Reply("move joints: " + rule.Describe(), !apply);
        reply["checks"] = checks;
        if (outside > 0) reply["outsideMap"] = outside;
        if (!apply)
        {
            // SAP2000 26.3 takes about 0.1 s per coordinate change (2026-10-12); the server allows an edit 15 minutes
            double est = Math.Round(moved.Count * SecondsPerJoint);
            reply["estimatedSeconds"] = est;
            if (est > 600) reply["timeWarning"] = "applying takes about " + est + " s; an edit may take at most 900 s. Split the targets into bands (z_min / z_max) with the same rule; the band edges then show as leftBehind until the next band is moved.";
        }
        if (passes > 1) reply["passes"] = passes;
        if (!dryRun && !apply)
        {
            reply["applied"] = false;
            reply["note"] = "NOT APPLIED: the checks found collapsed, reversed, flipped or coincident geometry (see checks). Change the rule or the targets, or pass force: true.";
        }
        return reply;
    }

    Dictionary<string, object> MoveChecks(SapRead.Index x, Dictionary<string, double[]> moved, JointRule rule, HashSet<string> targets, double tol, out bool blocking)
    {
        const int Cap = 50;
        Func<string, double[]> before = delegate(string j) { return x.Xyz[j]; };
        Func<string, double[]> after = delegate(string j) { double[] n; return moved.TryGetValue(j, out n) ? n : x.Xyz[j]; };
        List<object> collapsed = new List<object>(), reversed = new List<object>(), flipped = new List<object>(), coincident = new List<object>();
        int nCollapsed = 0, nReversed = 0, nFlipped = 0, nCoincident = 0;

        foreach (string kind in new[] { "frame", "link" })
            foreach (KeyValuePair<string, string[]> kv in kind == "frame" ? x.FrameEnds : x.LinkEnds)
            {
                string i = kv.Value[0], j = kv.Value[1];
                if (!moved.ContainsKey(i) && !moved.ContainsKey(j)) continue;
                double[] d0 = Sub(x.Xyz[j], x.Xyz[i]), d1 = Sub(after(j), after(i));
                double l0 = Norm(d0), l1 = Norm(d1);
                if (l1 <= tol && l0 > tol) { if (nCollapsed++ < Cap) collapsed.Add(O("kind", kind, "name", kv.Key, "lengthBefore", l0)); }
                else if (l0 > tol && l1 > tol && Dot(d0, d1) < 0) { if (nReversed++ < Cap) reversed.Add(O("kind", kind, "name", kv.Key)); }
            }
        double atol = tol * tol;
        foreach (KeyValuePair<string, string[]> kv in x.AreaPoints)
        {
            bool any = false;
            foreach (string p in kv.Value) if (moved.ContainsKey(p)) { any = true; break; }
            if (!any) continue;
            double[] n0 = Newell(kv.Value, before), n1 = Newell(kv.Value, after);
            double a0 = Norm(n0), a1 = Norm(n1);
            if (a1 <= atol && a0 > atol) { if (nCollapsed++ < Cap) collapsed.Add(O("kind", "area", "name", kv.Key, "areaBefore", a0 / 2)); }
            else if (a0 > atol && a1 > atol && Dot(n0, n1) < 0) { if (nFlipped++ < Cap) flipped.Add(O("kind", "area", "name", kv.Key)); }
        }

        // coincidence on a grid of cell = tol: each moved joint against every joint's final position
        Dictionary<string, List<string>> grid = new Dictionary<string, List<string>>();
        foreach (string j in x.Joints)
        {
            string key = Cell(after(j), tol, 0, 0, 0);
            List<string> l;
            if (!grid.TryGetValue(key, out l)) grid[key] = l = new List<string>();
            l.Add(j);
        }
        foreach (KeyValuePair<string, double[]> kv in moved)
            for (int a = -1; a <= 1; a++) for (int b = -1; b <= 1; b++) for (int c = -1; c <= 1; c++)
            {
                List<string> l;
                if (!grid.TryGetValue(Cell(kv.Value, tol, a, b, c), out l)) continue;
                foreach (string o in l)
                {
                    if (o == kv.Key || (moved.ContainsKey(o) && string.CompareOrdinal(o, kv.Key) < 0)) continue;
                    if (Dist(after(o), kv.Value) > tol || Dist(x.Xyz[o], x.Xyz[kv.Key]) <= tol) continue;
                    if (nCoincident++ < Cap) coincident.Add(new[] { kv.Key, o });
                }
            }

        // joints next to moved ones that the rule would also move, but that were not targets
        HashSet<string> left = new HashSet<string>();
        if (rule.To == null && moved.Count > 0)
        {
            Dictionary<string, List<string>> adj = Adjacency(x);
            foreach (string j in moved.Keys)
            {
                List<string> nb;
                if (!adj.TryGetValue(j, out nb)) continue;
                foreach (string o in nb)
                {
                    if (targets.Contains(o) || left.Contains(o)) continue;
                    double[] n = rule.Apply(x.Xyz[o]);
                    if (n != null && Dist(n, x.Xyz[o]) > tol) left.Add(o);
                }
            }
        }

        Dictionary<string, object> r = O("tolerance", tol,
            "collapsed", O("count", nCollapsed, "items", collapsed), "reversed", O("count", nReversed, "items", reversed),
            "flipped", O("count", nFlipped, "items", flipped), "coincident", O("count", nCoincident, "pairs", coincident));
        if (left.Count > 0)
        {
            List<string> names = new List<string>(left);
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (string o in names) { lo = Math.Min(lo, x.Xyz[o][2]); hi = Math.Max(hi, x.Xyz[o][2]); }
            r["leftBehind"] = O("count", names.Count, "zRange", new[] { lo, hi }, "joints", names.Count > Cap ? names.GetRange(0, Cap) : names,
                "note", rule.Delta != null
                    ? "connected to moved joints and not moved: the objects between them stretch. Fine when moving one part; otherwise add them."
                    : "connected to moved joints, not among the targets, and the rule would move them: probably forgotten. Add them (and dry-run again: their own neighbours may follow) or say why not.");
        }
        List<string> summary = new List<string>();
        if (nCollapsed > 0) summary.Add(nCollapsed + " collapsed");
        if (nReversed > 0) summary.Add(nReversed + " reversed");
        if (nFlipped > 0) summary.Add(nFlipped + " flipped");
        if (nCoincident > 0) summary.Add(nCoincident + " coincident");
        if (left.Count > 0) summary.Add(left.Count + " left behind");
        r["summary"] = summary.Count == 0 ? "geometry checks clean" : string.Join(", ", summary.ToArray());
        blocking = nCollapsed + nReversed + nFlipped + nCoincident > 0;
        return r;
    }

    static Dictionary<string, List<string>> Adjacency(SapRead.Index x)
    {
        Dictionary<string, List<string>> adj = new Dictionary<string, List<string>>();
        Action<string, string> link = delegate(string a, string b)
        {
            List<string> l;
            if (!adj.TryGetValue(a, out l)) adj[a] = l = new List<string>();
            l.Add(b);
        };
        foreach (string[] e in x.FrameEnds.Values) { link(e[0], e[1]); link(e[1], e[0]); }
        foreach (string[] e in x.LinkEnds.Values) { link(e[0], e[1]); link(e[1], e[0]); }
        foreach (string[] p in x.AreaPoints.Values)
            for (int i = 0; i < p.Length; i++) for (int k = 0; k < p.Length; k++) if (i != k) link(p[i], p[k]);
        return adj;
    }

    static string Cell(double[] p, double h, int dx, int dy, int dz)
    {
        return ((long)Math.Floor(p[0] / h) + dx) + "," + ((long)Math.Floor(p[1] / h) + dy) + "," + ((long)Math.Floor(p[2] / h) + dz);
    }
    static double[] Sub(double[] a, double[] b) { return new double[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static double Norm(double[] a) { return Math.Sqrt(Dot(a, a)); }
    static double Dist(double[] a, double[] b) { return Norm(Sub(a, b)); }

    // Newell normal of a polygon: its length is twice the polygon's area.
    static double[] Newell(string[] pts, Func<string, double[]> xyz)
    {
        double[] n = new double[3];
        for (int i = 0; i < pts.Length; i++)
        {
            double[] a = xyz(pts[i]), b = xyz(pts[(i + 1) % pts.Length]);
            n[0] += (a[1] - b[1]) * (a[2] + b[2]);
            n[1] += (a[2] - b[2]) * (a[0] + b[0]);
            n[2] += (a[0] - b[0]) * (a[1] + b[1]);
        }
        return n;
    }

    // A frame between two joints (or two points: new joints are made where none exists), with a section.
    public Dictionary<string, object> AddFrame(string i, string j, double[] xi, double[] xj, string section, string name, bool dryRun)
    {
        RequireName(PropNames("frame"), section, "frame section");
        SapRead.Index x = read.Objects();
        if (name != null && x.FrameEnds.ContainsKey(name)) throw new ArgumentException("frame '" + name + "' exists");
        if (i != null && j != null)
        {
            if (!x.Xyz.ContainsKey(i) || !x.Xyz.ContainsKey(j)) throw new ArgumentException("joint '" + (x.Xyz.ContainsKey(i) ? j : i) + "' does not exist");
            if (i == j) throw new ArgumentException("a frame needs two different joints");
        }
        else if (xi == null || xj == null) throw new ArgumentException("give joints i and j, or points from [x, y, z] and to [x, y, z]");
        Edits e = new Edits();
        string made = name ?? "(SAP2000 names it)";
        if (!dryRun)
        {
            string got = "";
            int ret = i != null ? M.FrameObj.AddByPoint(i, j, ref got, section, name ?? "")
                                : M.FrameObj.AddByCoord(xi[0], xi[1], xi[2], xj[0], xj[1], xj[2], ref got, section, name ?? "", "Global");
            Check(ret, "FrameObj.Add");
            made = got;
            string pi = "", pj = "";
            M.FrameObj.GetPoints(got, ref pi, ref pj);
            i = pi; j = pj;
        }
        e.Add("frame", made, null, O("i", i ?? Pt(xi), "j", j ?? Pt(xj), "section", section));
        return e.Reply("add frame", dryRun);
    }

    static string Pt(double[] p) { return p == null ? null : F("({0}, {1}, {2})", p[0], p[1], p[2]); }

    // Deletes frames, areas or links (with their loads and assignments). Joints are not deleted here: SAP2000 removes
    // a joint with the last object on it.
    public Dictionary<string, object> DeleteObjects(string kind, IList<string> names, bool dryRun)
    {
        if (kind == "joint") throw new ArgumentException("joints are not deleted directly: delete the objects on them (SAP2000 drops a free joint with its last object)");
        SapRead.Index x = read.Objects();
        Edits e = new Edits();
        foreach (string n in names)
        {
            object before = kind == "frame" ? (object)O("i", x.FrameEnds[n][0], "j", x.FrameEnds[n][1], "section", x.FrameSection[n])
                : kind == "area" ? (object)O("points", x.AreaPoints[n], "section", x.AreaSection[n])
                : O("i", x.LinkEnds[n][0], "j", x.LinkEnds[n][1], "property", x.LinkProp[n]);
            if (!dryRun)
            {
                int ret = kind == "frame" ? M.FrameObj.Delete(n, eItemType.Objects) : kind == "area" ? M.AreaObj.Delete(n, eItemType.Objects) : M.LinkObj.Delete(n, eItemType.Objects);
                if (ret != 0) { e.Fail(n, "Delete failed"); continue; }
            }
            e.Add(kind, n, before, null);
        }
        return e.Reply("delete " + kind + "s", dryRun);
    }

    // Whole input tables are edited through SapTables (export_tables / import_tables, the Excel round trip).

    // ---------------------------------------------------------------- helpers

    static void RequireName(string[] names, string name, string what)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException(what + " is required");
        if (Array.IndexOf(names, name) < 0)
        {
            List<string> near = new List<string>();
            foreach (string n in names) if (near.Count < 12 && n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) near.Add(n);
            throw new ArgumentException("no " + what + " '" + name + "'" + (near.Count > 0 ? " (similar: " + string.Join(", ", near.ToArray()) + ")" : ""));
        }
    }

    static T ParseEnum<T>(string value, string what) where T : struct
    {
        foreach (string n in Enum.GetNames(typeof(T))) if (string.Equals(n, value, StringComparison.OrdinalIgnoreCase)) return (T)Enum.Parse(typeof(T), n);
        throw new ArgumentException(what + " '" + value + "' is not one of " + string.Join(", ", Enum.GetNames(typeof(T))));
    }

    static string Flags(bool[] b, string[] names)
    {
        List<string> on = new List<string>();
        for (int k = 0; k < 6 && b != null && k < b.Length; k++) if (b[k]) on.Add(names[k]);
        return on.Count == 0 ? "none" : string.Join("+", on.ToArray());
    }

    static string F(string fmt, params object[] args) { return string.Format(Inv, fmt, args); }
    static Dictionary<string, object> O(params object[] kv) { return SapRead.O(kv); }
    static void Check(int ret, string what) { if (ret != 0) throw new Exception(what + " returned " + ret); }
}
