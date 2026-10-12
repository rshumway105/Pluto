// Edits to the model SAP2000 holds, each returning what it changed as before -> after (JSON-ready): sections and
// properties, materials, restraints, releases, joint / frame / area loads, load patterns, cases and combinations,
// groups, joint moves, new frames and deletes, through the object API. dryRun: nothing is
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

    // Moves joints by delta (global), or one joint to xyz. Connected objects follow.
    public Dictionary<string, object> MoveJoints(IList<string> joints, double[] delta, double[] to, bool dryRun)
    {
        if ((delta == null) == (to == null)) throw new ArgumentException("give either delta [dx, dy, dz] or to [x, y, z]");
        if (to != null && joints.Count != 1) throw new ArgumentException("'to' moves one joint; use delta for several");
        SapRead.Index x = read.Objects();
        Edits e = new Edits();
        foreach (string j in joints)
        {
            double[] c = x.Xyz[j];
            double[] n = to != null ? to : new double[] { c[0] + delta[0], c[1] + delta[1], c[2] + delta[2] };
            if (n[0] == c[0] && n[1] == c[1] && n[2] == c[2]) { e.Unchanged++; continue; }
            if (!dryRun && M.EditPoint.ChangeCoordinates_1(j, n[0], n[1], n[2], true) != 0) { e.Fail(j, "ChangeCoordinates refused (another joint there?)"); continue; }
            e.Add("joint", j, c, n);
        }
        return e.Reply("move joints", dryRun);
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

    // No table editing: on SAP2000 26.3 (2026-10-12) DatabaseTables.SetTableForEditingArray and ApplyEditedTables return 1
    // with an empty import log even for an unchanged round trip, and afterwards GetTableForDisplayArray gives no rows for
    // any table (also after CancelTableEditing and opening another model) until SAP2000 restarts. An edit_table tool
    // waits until that is understood; the targeted edits above use the object API.
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
