// Hand-check fixtures: two small models built over the OAPI, edited, run and read back through the same SapEdit /
// SapResults code the Pluto tools use, each result checked against a value worked by hand. The tool verification of a
// design record (MCP self_test fixtures: true). Run in a SAP2000 of its own: building a model replaces what SAP holds.
// Units kip, ft. C# 5 (see SapSession.cs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SAP2000v1;

public static class SapFixtures
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const double Tolerance = 1e-6;   // relative

    static void C(int r, string what) { if (r != 0) throw new InvalidOperationException("fixture: " + what + " returned " + r); }

    // Builds and checks both fixtures, saving their models in dir. Each check: name, what, hand, program, pass.
    public static List<Dictionary<string, object>> Run(SapSession s, string dir)
    {
        Directory.CreateDirectory(dir);
        List<Dictionary<string, object>> checks = new List<Dictionary<string, object>>();
        Plate(s, Path.Combine(dir, "plate.sdb"), checks);
        Cantilever(s, Path.Combine(dir, "cantilever.sdb"), checks);
        return checks;
    }

    static void Check(List<Dictionary<string, object>> checks, string name, string what, double hand, double program)
    {
        bool pass = Math.Abs(program - hand) <= Tolerance * Math.Max(Math.Abs(hand), 1e-12);
        checks.Add(SapRead.O("name", name, "what", what, "hand", hand, "program", program, "tolerance", Tolerance, "pass", pass));
    }

    // ---------------------------------------------------------------- plate

    // 10 ft x 10 ft thin shell, 1 ft thick, 2 x 2 mesh, the 8 edge joints pinned, the centre joint free. CONC: E 432000
    // ksf, nu 0.2, 0.15 kcf. DEAD (self weight) and SDL (0.1 ksf gravity through assign_area_load). Then weight x 2,
    // E x 2 and 0.02 ksf in X added to SDL through set_material_property / assign_area_load, and run again.
    static void Plate(SapSession s, string path, List<Dictionary<string, object>> checks)
    {
        cSapModel m = s.Model;
        C(m.InitializeNewModel(eUnits.kip_ft_F), "Init");
        C(m.File.NewBlank(), "NewBlank");
        C(m.PropMaterial.SetMaterial("CONC", eMatType.Concrete), "Mat");
        C(m.PropMaterial.SetMPIsotropic("CONC", 432000, 0.2, 5.5e-6), "MPIso");
        C(m.PropMaterial.SetWeightAndMass("CONC", 1, 0.15), "Weight");
        C(m.PropArea.SetShell_1("SLAB", 1, true, "CONC", 0, 1.0, 1.0), "Shell");
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                double x0 = 5 * i, y0 = 5 * j;
                double[] x = { x0, x0 + 5, x0 + 5, x0 }, y = { y0, y0, y0 + 5, y0 + 5 }, z = { 0, 0, 0, 0 };
                string name = "";
                C(m.AreaObj.AddByCoord(4, ref x, ref y, ref z, ref name, "SLAB"), "Area");
            }
        int n = 0; string[] pts = null;
        m.PointObj.GetNameList(ref n, ref pts);
        string centre = null;
        foreach (string p in pts)
        {
            double x = 0, y = 0, z = 0;
            m.PointObj.GetCoordCartesian(p, ref x, ref y, ref z, "Global");
            if (Math.Abs(x - 5) < 1e-9 && Math.Abs(y - 5) < 1e-9) { centre = p; continue; }
            bool[] r = { true, true, true, false, false, false };
            C(m.PointObj.SetRestraint(p, ref r), "Restraint");
        }
        C(m.LoadPatterns.Add("SDL", eLoadPatternType.SuperDead, 0, true), "SDL");
        s.SaveAs(path);

        SapRead read = new SapRead(s);
        SapEdit edit = new SapEdit(s, read);
        List<string> areas = new List<string>(read.Objects().Areas);
        edit.AssignAreaLoad(areas, "SDL", 0.1, 10, "Global", true, false, false);
        read.Invalidate();
        SapResults res = new SapResults(s, read);
        res.Run(null);
        Dictionary<string, Dictionary<string, object>> r1 = Reactions(res);
        Check(checks, "plate: self weight", "DEAD reaction FZ = 0.15 kcf x 1 ft x 100 sq ft", 15, Num(r1, "DEAD", "reaction", "FZ"));
        Check(checks, "plate: area load, gravity", "SDL reaction FZ = 0.1 ksf x 100 sq ft", 10, Num(r1, "SDL", "reaction", "FZ"));
        double u1 = Peak(res, "joint", "U3", "SDL", centre);

        C(m.SetModelIsLocked(false), "Unlock");
        Dictionary<string, double> mat = new Dictionary<string, double>();
        mat["weightPerVolume"] = 0.3; mat["E"] = 864000;
        edit.SetMaterial("CONC", mat, false);
        edit.AssignAreaLoad(areas, "SDL", 0.02, 4, "Global", false, false, false);
        read.Invalidate();
        res.Run(null);
        Dictionary<string, Dictionary<string, object>> r2 = Reactions(res);
        Check(checks, "plate: material weight x 2", "DEAD reaction FZ = 0.3 kcf x 100 cu ft", 30, Num(r2, "DEAD", "reaction", "FZ"));
        Check(checks, "plate: area load, lateral", "SDL reaction FX = -0.02 ksf x 100 sq ft", -2, Num(r2, "SDL", "reaction", "FX"));
        Check(checks, "plate: E x 2", "SDL centre deflection ratio after / before = 1/2", 0.5, Peak(res, "joint", "U3", "SDL", centre) / u1);
        s.SaveAs(path);
    }

    // ---------------------------------------------------------------- cantilever (one mass)

    // 10 ft cantilever, 1 ft x 1 ft steel (weightless, massless), 1 kip-s2/ft at the tip in U1 only, tip restrained in
    // U2 U3 R1 R3: one mode. k from bending + shear flexibility (SAP rectangle As = 5/6 A, G = E / 2(1 + nu)).
    // PUSH: 10 kip in X at the tip. MODAL; RS: flat 0.5 g in U1; THM: linear modal, PUSH as a step, no damping.
    static void Cantilever(SapSession s, string path, List<Dictionary<string, object>> checks)
    {
        cSapModel m = s.Model;
        C(m.InitializeNewModel(eUnits.kip_ft_F), "Init");
        C(m.File.NewBlank(), "NewBlank");
        C(m.PropMaterial.SetMaterial("STL", eMatType.Steel), "Mat");
        C(m.PropMaterial.SetMPIsotropic("STL", 4176000, 0.3, 6.5e-6), "MPIso");
        C(m.PropMaterial.SetWeightAndMass("STL", 1, 0), "Weight");
        C(m.PropFrame.SetRectangle("COL", "STL", 1, 1), "Rect");
        string f = "", pi = "", pj = "";
        C(m.FrameObj.AddByCoord(0, 0, 0, 0, 0, 10, ref f, "COL"), "Frame");
        C(m.FrameObj.GetPoints(f, ref pi, ref pj), "Points");
        bool[] fix = { true, true, true, true, true, true }, tip = { false, true, true, true, false, true };
        C(m.PointObj.SetRestraint(pi, ref fix), "Fix");
        C(m.PointObj.SetRestraint(pj, ref tip), "Tip");
        double[] mass = { 1, 0, 0, 0, 0, 0 };
        C(m.PointObj.SetMass(pj, ref mass), "Mass");

        double E = 4176000, G = E / 2.6, I = 1.0 / 12, L = 10, As = 5.0 / 6, F = 10, g = 32.174, sa = 0.5;
        double k = 1 / (L * L * L / (3 * E * I) + L / (G * As));
        double T = 2 * Math.PI / Math.Sqrt(k);

        C(m.LoadPatterns.Add("PUSH", eLoadPatternType.Other, 0, true), "PUSH");
        double[] load = { F, 0, 0, 0, 0, 0 };
        C(m.PointObj.SetLoadForce(pj, "PUSH", ref load), "Force");
        C(m.LoadCases.ModalEigen.SetNumberModes("MODAL", 3, 1), "Modes");
        double[] per = { 0, 10 }, val = { sa, sa };
        C(m.Func.FuncRS.SetUser("FLAT", 2, ref per, ref val, 0.05), "FuncRS");
        C(m.LoadCases.ResponseSpectrum.SetCase("RS"), "RS");
        string[] ln = { "U1" }, fn = { "FLAT" }, cs = { "Global" };
        double[] sf = { g }, ang = { 0 };
        C(m.LoadCases.ResponseSpectrum.SetLoads("RS", 1, ref ln, ref fn, ref sf, ref cs, ref ang), "RSLoads");
        C(m.LoadCases.ResponseSpectrum.SetModalCase("RS", "MODAL"), "RSModal");
        double[] t = { 0, 1000 }, v = { 1, 1 };
        C(m.Func.FuncTH.SetUser("STEP", 2, ref t, ref v), "FuncTH");
        C(m.LoadCases.ModHistLinear.SetCase("THM"), "THM");
        string[] lt = { "Load" }, lname = { "PUSH" }, lf = { "STEP" }, lcs = { "Global" };
        double[] lsf = { 1 }, ltf = { 1 }, lat = { 0 }, lang = { 0 };
        C(m.LoadCases.ModHistLinear.SetLoads("THM", 1, ref lt, ref lname, ref lf, ref lsf, ref ltf, ref lat, ref lcs, ref lang), "THLoads");
        C(m.LoadCases.ModHistLinear.SetModalCase("THM", "MODAL"), "THModal");
        C(m.LoadCases.ModHistLinear.SetTimeStep("THM", 250, T / 100), "THStep");
        C(m.LoadCases.ModHistLinear.SetDampConstant("THM", 0), "THDamp");
        s.SaveAs(path);

        SapRead read = new SapRead(s);
        SapResults res = new SapResults(s, read);
        res.Run(null);
        Check(checks, "cantilever: period", "T = 2 pi sqrt(m / k), k from bending + shear", T, ModalPeriod(m, "MODAL"));
        Dictionary<string, Dictionary<string, object>> r = Reactions(res);
        Check(checks, "cantilever: RS base shear", "V = m Sa = 0.5 g x 1 kip-s2/ft", sa * g, Math.Abs(Num(r, "RS", "reaction", "FX")));
        Check(checks, "cantilever: RS tip displacement", "U = m Sa / k", sa * g / k, Peak(res, "joint", "U1", "RS", pj));
        Check(checks, "cantilever: TH step, peak displacement", "undamped step: U = 2 F / k", 2 * F / k, Peak(res, "joint", "U1", "THM", pj));
        Check(checks, "cantilever: TH step, peak base shear", "undamped step: V = 2 F", 2 * F, Math.Abs(Num(r, "THM", "reaction", "FX")));
    }

    // ---------------------------------------------------------------- reads

    // Base reactions per case (an envelope case: the step with the largest |FX| + |FZ|).
    static Dictionary<string, Dictionary<string, object>> Reactions(SapResults res)
    {
        Dictionary<string, Dictionary<string, object>> r = new Dictionary<string, Dictionary<string, object>>();
        foreach (object o in (List<object>)res.Reactions(null, 0.005)["cases"])
        {
            Dictionary<string, object> d = (Dictionary<string, object>)o;
            string c = (string)d["case"];
            Dictionary<string, object> old;
            if (!r.TryGetValue(c, out old) || Size(d) > Size(old)) r[c] = d;
        }
        return r;
    }

    static double Size(Dictionary<string, object> d)
    {
        Dictionary<string, object> f = (Dictionary<string, object>)d["reaction"];
        return Math.Abs(Convert.ToDouble(f["FX"], Inv)) + Math.Abs(Convert.ToDouble(f["FZ"], Inv));
    }

    static double Num(Dictionary<string, Dictionary<string, object>> r, string c, string part, string key)
    {
        Dictionary<string, object> d;
        if (!r.TryGetValue(c, out d)) throw new InvalidOperationException("fixture: no base reaction for " + c);
        return Convert.ToDouble(((Dictionary<string, object>)d[part])[key], Inv);
    }

    // The largest |value| of one object's quantity in one case, through query_results' code.
    static double Peak(SapResults res, string kind, string quantity, string c, string obj)
    {
        SapResults.ResultQuery q = new SapResults.ResultQuery();
        q.Kind = kind; q.Quantity = quantity; q.Cases = new[] { c }; q.Only = new HashSet<string> { obj }; q.Top = 1;
        Dictionary<string, object> r = res.Query(q);
        if (!r.ContainsKey("max")) throw new InvalidOperationException("fixture: no " + kind + " " + quantity + " for " + obj + " in " + c);
        return Math.Max(Math.Abs(Convert.ToDouble(((Dictionary<string, object>)r["max"])["value"], Inv)),
                        Math.Abs(Convert.ToDouble(((Dictionary<string, object>)r["min"])["value"], Inv)));
    }

    static double ModalPeriod(cSapModel m, string c)
    {
        C(m.Results.Setup.DeselectAllCasesAndCombosForOutput(), "Deselect");
        C(m.Results.Setup.SetCaseSelectedForOutput(c, true), "Select " + c);
        int n = 0; string[] lc = null, st = null; double[] sn = null, p = null, fq = null, cf = null, ev = null;
        C(m.Results.ModalPeriod(ref n, ref lc, ref st, ref sn, ref p, ref fq, ref cf, ref ev), "ModalPeriod");
        if (n == 0) throw new InvalidOperationException("fixture: no modal period for " + c);
        return p[0];
    }
}
