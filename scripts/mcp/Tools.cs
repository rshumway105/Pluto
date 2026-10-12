// Pluto MCP: the tool table. Each tool: name, description (what the LLM reads to choose it), JSON Schema
// for its arguments, handler. Handler failures come back as isError results the LLM can act on.
// C# 5 (see McpServer.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public sealed class ToolSet
{
    delegate object Handler(Dictionary<string, object> args);

    sealed class Tool
    {
        public string Name, Description;
        public Dictionary<string, object> Schema;
        public Handler Run;
    }

    readonly List<Tool> tools = new List<Tool>();
    readonly ServerContext ctx;

    public ToolSet(ServerContext ctx)
    {
        this.ctx = ctx;

        Add("status",
            "Start here. Reports the Pluto server (version, work dir, whether a rebuild is needed after a program " +
            "upgrade), each analysis program (running or not, version, the model it holds, present units, object " +
            "counts, load cases and whether they have results, groups) and the capabilities available. Read only.",
            Schema(), Status);

        Add("self_test",
            "Install check: builds a 20 ft simply supported beam with 10 kip at midspan, runs it in SAP2000 and " +
            "checks max |V2| = 5 kip and max |M3| = 50 kip-ft. By default in a second SAP2000 instance that is " +
            "closed afterwards, so the user's open model is untouched. Takes about a minute.",
            Schema("use_running_instance", Prop("boolean",
                "Run in the SAP2000 the user has open instead. This REPLACES the model it holds: only with the user's consent.")),
            SelfTest);

        // ------------------------------------------------ phase 1: read and understand

        Add("open_model",
            "Opens a SAP2000 model (.sdb, or .s2k / .$2k imported) in the running SAP2000 and immediately saves it as a " +
            "working copy in the Pluto work dir; SAP then holds the copy, so the original file is never written. " +
            "REPLACES the model SAP2000 holds now (unsaved changes there are lost): confirm with the user first if " +
            "SAP2000 holds a model they are working on (status shows it). Not needed to read a model already open.",
            Schema(new[] { "path" }, "path", Prop("string", "Full path of the model file.")),
            OpenModel);

        Add("describe_model",
            "Overview of the model SAP2000 holds: units, object counts, extent, supports, materials, frame / area " +
            "sections and link properties with how many objects use each, groups with member counts, load patterns, " +
            "load cases (type, run status), combinations, and which cases have results. Read only. The first call on " +
            "a model indexes every object, which takes a while on large models.",
            Schema("max_rows", Prop("integer", "Rows per list (default 200).")),
            Describe);

        Add("list_objects",
            "Pages through objects of one kind: joints (coordinates), frames (end joints, section, length), areas " +
            "(corner joints, section), links (end joints, property, length). Optionally only a group's members or a " +
            "saved selection. Read only.",
            Schema(new[] { "kind" },
                "kind", Enum("joint, frame, area or link.", Kinds),
                "group", Prop("string", "Only members of this SAP2000 group."),
                "selection", Prop("string", "Only members of a selection saved by select_objects save_as."),
                "offset", Prop("integer", "First row (default 0)."),
                "limit", Prop("integer", "Rows (default 200, max 2000).")),
            ListObjects);

        Add("get_object",
            "One object in full: joint (coordinates, restraint, springs, constraints, local axes, what connects to " +
            "it, loads), frame (ends, length, section, local axes as global vectors, end releases, insertion point, " +
            "end offsets, modifiers, loads), area (corners, section, local axes, modifiers, loads), link (ends, " +
            "property, local axes). Plus its groups. Read only.",
            Schema(new[] { "kind", "name" },
                "kind", Enum("joint, frame, area or link.", Kinds),
                "name", Prop("string", "Object name as SAP2000 shows it.")),
            GetObject);

        Add("get_sections",
            "Section / property definitions with how many objects use each. frame: type, material, dimensions, " +
            "A, As2, As3, J, I22, I33, S, Z, r, modifiers. area: type, shell type, material, thicknesses, modifiers. " +
            "link: type (details via read_table 'Link Property Definitions ...'). Read only.",
            Schema("kind", Enum("frame (default), area or link.", new[] { "frame", "area", "link" }),
                "name", Prop("string", "Name, * and ? wildcards (default all)."),
                "offset", Prop("integer", "First row (default 0)."),
                "limit", Prop("integer", "Rows (default 200, max 2000).")),
            GetSections);

        Add("get_materials",
            "Materials: type, E, nu, alpha, G, weight and mass per volume, Fy / Fu (steel), f'c (concrete). Read only.",
            Schema("name", Prop("string", "Name, * and ? wildcards (default all).")),
            GetMaterials);

        Add("get_load_cases",
            "Load cases: type, run status, and for static cases the loads applied (pattern, scale) and the initial " +
            "case. Other case types: read_table 'Load Case Definitions' and the 'Case - ...' tables. Read only.",
            Schema("name", Prop("string", "Name, * and ? wildcards (default all).")),
            GetLoadCases);

        Add("get_combos",
            "Load combinations: type (linear additive, envelope, ...) and items (case or combo, scale). Read only.",
            Schema("name", Prop("string", "Name, * and ? wildcards (default all).")),
            GetCombos);

        Add("select_objects",
            "Finds objects by geometry and attributes, all criteria combined: kind, Z range, box, plane, section / " +
            "property, group, name. Coordinates are global, in the model's present units (describe_model). Returns " +
            "counts, extent and property use per kind, plus names (capped). save_as keeps the set in the work dir " +
            "for list_objects / select_objects 'selection'; show_in_sap selects them in the SAP2000 window (GUI " +
            "selection only). For 'floor 5 and above' resolve the Z with describe_levels first. Read only.",
            Schema("kind", Prop("string", "joint, frame, area or link; comma-separated for several (default all four)."),
                "z_min", Prop("number", "Lowest Z."),
                "z_max", Prop("number", "Highest Z."),
                "box", Json.O("type", "array", "items", Json.O("type", "number"), "minItems", 6, "maxItems", 6,
                    "description", "[xmin, ymin, zmin, xmax, ymax, zmax]."),
                "plane", Json.O("type", "object", "description", "Objects lying on a plane: {\"axis\": \"x\"|\"y\"|\"z\", \"value\": number}.",
                    "properties", Json.O("axis", Json.O("type", "string", "enum", new[] { "x", "y", "z" }), "value", Json.O("type", "number"))),
                "tolerance", Prop("number", "Geometric tolerance (default 0.1 % of the model's largest extent)."),
                "match", Enum("all (default): every joint of the object inside; any: one joint inside; centroid.", new[] { "all", "any", "centroid" }),
                "section", Prop("string", "Frame / area section or link property, * and ? wildcards."),
                "group", Prop("string", "Members of this SAP2000 group."),
                "selection", Prop("string", "Members of a saved selection."),
                "name", Prop("string", "Object name, * and ? wildcards."),
                "save_as", Prop("string", "Save the result under this name (letters, digits, - _)."),
                "show_in_sap", Prop("boolean", "Select the result in the SAP2000 window (replaces the GUI selection)."),
                "limit", Prop("integer", "Names returned per kind (default 200, max 2000; counts are always complete).")),
            SelectObjects);

        Add("describe_levels",
            "Candidate floor elevations, so 'level 5 and above' becomes a Z range: clusters of joint Z with the " +
            "number of horizontal frames / areas on each (floor-like when enough lie on it), grid Z lines, and groups " +
            "named like levels with their Z range. SAP2000 has no stories: when the levels are ambiguous, show the " +
            "candidates and ask the user. Read only.",
            Schema("tolerance", Prop("number", "Z clustering tolerance (default 0.2 % of the model's height).")),
            DescribeLevels);

        Add("read_table",
            "Any SAP2000 input table (DatabaseTables), e.g. 'Frame Section Assignments', 'Joint Restraint " +
            "Assignments', 'Grid Lines', 'Link Property Definitions 05 - Gap'. Without name: lists the tables that " +
            "hold data (search narrows it). Values are display-rounded text: input data only, never results. The " +
            "general way to read anything no dedicated tool covers. Read only.",
            Schema("name", Prop("string", "Table key, as listed."),
                "search", Prop("string", "When listing: only tables whose key contains this."),
                "group", Prop("string", "Only rows of this group's objects."),
                "filter", Json.O("type", "object", "additionalProperties", Json.O("type", "string"),
                    "description", "Field -> value, * and ? wildcards; every one must match."),
                "fields", Json.O("type", "array", "items", Json.O("type", "string"), "description", "Only these fields."),
                "offset", Prop("integer", "First row (default 0)."),
                "limit", Prop("integer", "Rows (default 200, max 2000)."),
                "to_file", Prop("boolean", "Also write every matching row to a TSV in the work dir and return its path.")),
            ReadTable);

        // ------------------------------------------------ phase 2: run and query results

        Add("run_analysis",
            "Runs the analysis on a new checkpoint: saves the model SAP2000 holds as <session dir>\\rNNN.sdb and runs it " +
            "there, so the opened file and earlier checkpoints keep their state and results. Reports each case's status, " +
            "the warnings / errors of SAP2000's analysis log, the equilibrium check per case, and the largest value of " +
            "every result quantity. Saves per-object envelopes for compare_runs. Can take minutes on large models; " +
            "confirm with the user before a long run.",
            Schema("cases", Json.O("type", "array", "items", Json.O("type", "string"),
                    "description", "Only these load cases (and the initial cases they continue from). Default: every case."),
                "summary", Prop("boolean", "Save envelopes and reactions for compare_runs / the reply (default true).")),
            RunAnalysis);

        Add("query_results",
            "Analysis results at full precision: the top N (largest magnitude by default), the controlling case and " +
            "location, max / min, the worst per case and optionally per group. kind and its quantities: joint (U1 U2 U3 R1 " +
            "R2 R3, U = translation magnitude), reaction (F1 F2 F3 M1 M2 M3), frame (P V2 V3 T M2 M3, along each frame), " +
            "area (F11 F22 F12 FMax FMin FVM M11 M22 M12 MMax MMin V13 V23 VMax per unit length, at corner joints), " +
            "area_stress (S11Top S22Top S12Top SMaxTop SMinTop SVMTop, the same ...Bot, S13Avg S23Avg SMaxAvg), link (P V2 V3 T " +
            "M2 M3 at each end). Local axes as SAP2000 defines them; units as reported. Default cases: every finished case " +
            "and every combination; nonlinear / multi-step static cases give their last step. Read only.",
            Schema(new[] { "kind", "quantity" },
                "kind", Enum("Result kind.", SapResults.ResultKinds),
                "quantity", Prop("string", "One quantity of that kind, e.g. M3, SVMTop, U3, F3."),
                "cases", Json.O("type", "array", "items", Json.O("type", "string"), "description", "Load cases / combinations, * and ? wildcards."),
                "group", Prop("string", "Only objects of this SAP2000 group."),
                "selection", Prop("string", "Only objects of a selection saved by select_objects."),
                "name", Prop("string", "Only objects whose name matches (* and ? wildcards)."),
                "top_n", Prop("integer", "How many to list (default 20, max 500)."),
                "envelope", Prop("boolean", "true (default): one entry per object, at its controlling case and location; false: individual rows."),
                "sort", Enum("abs (default): largest magnitude; max: most positive; min: most negative.", new[] { "abs", "max", "min" }),
                "by_group", Json.O("type", "array", "items", Json.O("type", "string"),
                    "description", "Also summarise per group: these group names, or [\"*\"] for every group (max 100)."),
                "to_file", Prop("boolean", "Also write every matching row to a TSV in the work dir and return its path.")),
            QueryResults);

        Add("reactions_summary",
            "Equilibrium check: per case / combination, the base reactions (total FX FY FZ, MX MY MZ about the base point) " +
            "against the applied load summed from the model's load assignments (self weight, joint, frame and area loads, face " +
            "pressures, case and linear combination factors). 'balanced' when applied + reaction is within tolerance; 'incomplete' " +
            "lists loads it cannot sum (edge pressure, auto lateral loads, acceleration); other case types are reported " +
            "unchecked. Read only.",
            Schema("cases", Json.O("type", "array", "items", Json.O("type", "string"), "description", "Load cases / combinations, * and ? wildcards (default all with results)."),
                "tolerance", Prop("number", "Allowed residual as a fraction of the larger of applied and reaction (default 0.005).")),
            ReactionsSummary);

        Add("compare_runs",
            "Biggest changes of one result quantity between two runs (checkpoints) of the current session, per object and " +
            "case, from the envelopes run_analysis saved: value in a, in b, change, relative change; counts of objects that " +
            "changed. Without kind: lists the runs. Default b = the latest run, a = the one before. Read only.",
            Schema("kind", Enum("Result kind.", SapResults.ResultKinds),
                "quantity", Prop("string", "One quantity of that kind (see query_results)."),
                "a", Prop("string", "Earlier run, e.g. r001."),
                "b", Prop("string", "Later run, e.g. r002."),
                "cases", Json.O("type", "array", "items", Json.O("type", "string"), "description", "Only these cases / combinations, * and ? wildcards."),
                "group", Prop("string", "Only objects of this SAP2000 group (as the model is now)."),
                "selection", Prop("string", "Only objects of a saved selection."),
                "top_n", Prop("integer", "How many changes to list (default 20, max 500).")),
            CompareRuns);

        Add("export_viewer",
            "Exports the model SAP2000 holds, with its results, for the Pluto viewer: analysis mesh, model and results " +
            ".s2k, then .bin + .features.json in the session dir (Run-Sap.ps1 -Export). Open viewer\\index.html and pick " +
            "the .bin. Takes a while on large models. Does not change the model.",
            Schema("results", Prop("boolean", "Include results (default true when the model has them)."),
                "final_only", Prop("boolean", "Staged / multi-step cases: only the last step."),
                "cylindrical", Prop("boolean", "Add radial / tangential translations (R, T) for tanks and other axisymmetric models.")),
            ExportViewer);

        // ------------------------------------------------ phase 3: change the model

        const string EditNote = " Goes into a checkpoint of the session, never the opened file (the latest checkpoint while it " +
            "is not run, else a new rNNN; a run checkpoint keeps its results). Logged in changes.jsonl. Returns before -> " +
            "after per object. dry_run: shows what would change, changes nothing. Ask the user before large edits.";

        Add("set_frame_section", "Assigns a frame section to frames (names, group and / or saved selection)." + EditNote,
            Schema(new[] { "section" }, Cat(Targets("frame"), new object[] { "section", Prop("string", "Frame section name (get_sections)."), "dry_run", DryRun })),
            delegate(Dictionary<string, object> a) { string sec = Json.Str(a, "section"); return Edit("set_frame_section", a, "frame", delegate(SapEdit e, List<string> t, bool d) { return e.SetFrameSection(t, sec, d); }); });

        Add("set_area_section", "Assigns an area section to areas." + EditNote,
            Schema(new[] { "section" }, Cat(Targets("area"), new object[] { "section", Prop("string", "Area section name (get_sections kind area)."), "dry_run", DryRun })),
            delegate(Dictionary<string, object> a) { string sec = Json.Str(a, "section"); return Edit("set_area_section", a, "area", delegate(SapEdit e, List<string> t, bool d) { return e.SetAreaSection(t, sec, d); }); });

        Add("set_link_property", "Assigns a link property to links." + EditNote,
            Schema(new[] { "property" }, Cat(Targets("link"), new object[] { "property", Prop("string", "Link property name (get_sections kind link)."), "dry_run", DryRun })),
            delegate(Dictionary<string, object> a) { string pr = Json.Str(a, "property"); return Edit("set_link_property", a, "link", delegate(SapEdit e, List<string> t, bool d) { return e.SetLinkProperty(t, pr, d); }); });

        Add("set_material_property", "Changes material values; only those given change: E, nu, alpha (isotropic), weight per volume, " +
            "Fy / Fu (steel), fc (concrete). Every object using the material is affected." + EditNote,
            Schema(new[] { "material" }, "material", Prop("string", "Material name (get_materials)."),
                "E", Prop("number", "Modulus of elasticity."), "nu", Prop("number", "Poisson's ratio."), "alpha", Prop("number", "Thermal coefficient."),
                "weight_per_volume", Prop("number", "Weight per unit volume."), "Fy", Prop("number", "Steel yield stress."),
                "Fu", Prop("number", "Steel tensile strength."), "fc", Prop("number", "Concrete compressive strength."), "dry_run", DryRun),
            SetMaterial);

        Add("set_restraint", "Sets joint restraints (supports): fixed_dofs lists the restrained directions in joint local axes, " +
            "e.g. all six for fixed, [U1, U2, U3] for pinned, [] to free the joint." + EditNote,
            Schema(new[] { "fixed_dofs" }, Cat(Targets("joint"), new object[] { "fixed_dofs", Json.O("type", "array", "items", Json.O("type", "string", "enum", new[] { "U1", "U2", "U3", "R1", "R2", "R3" }),
                "description", "Restrained degrees of freedom."), "dry_run", DryRun })),
            delegate(Dictionary<string, object> a)
            {
                bool[] fix = Dofs(Json.Strs(a, "fixed_dofs"), new[] { "U1", "U2", "U3", "R1", "R2", "R3" }, "fixed_dofs");
                return Edit("set_restraint", a, "joint", delegate(SapEdit e, List<string> t, bool d) { return e.SetRestraint(t, fix, d); });
            });

        Add("set_frame_release", "Sets frame end releases (frame local axes; replaces the frame's releases): i_end / j_end list the " +
            "released forces at each end, e.g. [M2, M3] for a pinned end, [] for fixed. SAP2000 refuses unstable sets." + EditNote,
            Schema(new[] { "i_end", "j_end" }, Cat(Targets("frame"), new object[] {
                "i_end", Json.O("type", "array", "items", Json.O("type", "string", "enum", new[] { "P", "V2", "V3", "T", "M2", "M3" }), "description", "Released at the I end."),
                "j_end", Json.O("type", "array", "items", Json.O("type", "string", "enum", new[] { "P", "V2", "V3", "T", "M2", "M3" }), "description", "Released at the J end."),
                "dry_run", DryRun })),
            delegate(Dictionary<string, object> a)
            {
                string[] rel = { "P", "V2", "V3", "T", "M2", "M3" };
                bool[] ii = Dofs(Json.Strs(a, "i_end"), rel, "i_end"), jj = Dofs(Json.Strs(a, "j_end"), rel, "j_end");
                return Edit("set_frame_release", a, "frame", delegate(SapEdit e, List<string> t, bool d) { return e.SetFrameRelease(t, ii, jj, d); });
            });

        Add("assign_joint_load", "Joint forces / moments for a load pattern (F1 F2 F3 M1 M2 M3 in csys, default GLOBAL). replace (default " +
            "true) replaces the joint's loads of that pattern, false adds; clear removes them." + EditNote,
            Schema(new[] { "pattern" }, Cat(Targets("joint"), new object[] { "pattern", Prop("string", "Load pattern."),
                "F1", Prop("number", "Force along 1 (X in GLOBAL)."), "F2", Prop("number", "Force along 2."), "F3", Prop("number", "Force along 3 (Z; negative = down)."),
                "M1", Prop("number", "Moment about 1."), "M2", Prop("number", "Moment about 2."), "M3", Prop("number", "Moment about 3."),
                "csys", Prop("string", "Coordinate system (default GLOBAL; Local = joint local axes)."),
                "replace", Prop("boolean", "Replace (default) or add."), "clear", Prop("boolean", "Remove this pattern's joint loads."), "dry_run", DryRun })),
            delegate(Dictionary<string, object> a)
            {
                string pat = Json.Str(a, "pattern"), cs = Json.Str(a, "csys") ?? "GLOBAL";
                double[] f = new double[6]; string[] k = { "F1", "F2", "F3", "M1", "M2", "M3" };
                for (int i = 0; i < 6; i++) f[i] = Json.Num(a, k[i]) ?? 0;
                bool rep = Json.Bool(a, "replace", true), clear = Json.Bool(a, "clear", false);
                return Edit("assign_joint_load", a, "joint", delegate(SapEdit e, List<string> t, bool d) { return e.AssignJointLoad(t, pat, f, cs, rep, clear, d); });
            });

        Add("assign_frame_load", "Frame loads for a load pattern: distributed (value per length from start to end; end_value for a " +
            "trapezoid) or point (value at distance). Distances are relative 0..1 unless relative is false. dir: local1 / local2 / " +
            "local3, X / Y / Z, X_projected / Y_projected / Z_projected, gravity (positive = down), gravity_projected. replace " +
            "(default true) replaces the frame's loads of that pattern and type, false adds; clear removes them." + EditNote,
            Schema(new[] { "pattern", "type", "dir" }, Cat(Targets("frame"), new object[] { "pattern", Prop("string", "Load pattern."),
                "type", Enum("distributed or point.", new[] { "distributed", "point" }),
                "load", Enum("force (default) or moment.", new[] { "force", "moment" }),
                "dir", Prop("string", "Direction (see above)."),
                "value", Prop("number", "Distributed: value at start (per length); point: the load."),
                "end_value", Prop("number", "Distributed: value at end (default = value)."),
                "start", Prop("number", "Distributed: start (default 0); point: location (default 0.5)."),
                "end", Prop("number", "Distributed: end (default 1, the whole frame)."),
                "relative", Prop("boolean", "Distances relative 0..1 (default true) or lengths."),
                "csys", Prop("string", "Coordinate system for X / Y / Z directions (default GLOBAL)."),
                "replace", Prop("boolean", "Replace (default) or add."), "clear", Prop("boolean", "Remove this pattern's frame loads of this type."), "dry_run", DryRun })),
            AssignFrameLoad);

        Add("assign_area_load", "Uniform area load (force per area) for a load pattern, dir as for frame loads (gravity: positive = down). " +
            "replace (default true) replaces the area's uniform loads of that pattern, false adds; clear removes them." + EditNote,
            Schema(new[] { "pattern", "dir" }, Cat(Targets("area"), new object[] { "pattern", Prop("string", "Load pattern."),
                "value", Prop("number", "Force per unit area."), "dir", Prop("string", "Direction: local1 / local2 / local3, X / Y / Z, ..._projected, gravity."),
                "csys", Prop("string", "Coordinate system for X / Y / Z directions (default GLOBAL)."),
                "replace", Prop("boolean", "Replace (default) or add."), "clear", Prop("boolean", "Remove this pattern's uniform loads."), "dry_run", DryRun })),
            delegate(Dictionary<string, object> a)
            {
                string pat = Json.Str(a, "pattern"); int dir = DirCode(Json.Str(a, "dir"));
                string cs = Json.Str(a, "csys") ?? (dir <= 3 ? "Local" : "GLOBAL");
                double v = Json.Num(a, "value") ?? 0; bool rep = Json.Bool(a, "replace", true), clear = Json.Bool(a, "clear", false);
                if (!clear && Json.Num(a, "value") == null) throw new ToolError("value is required (or clear)");
                return Edit("assign_area_load", a, "area", delegate(SapEdit e, List<string> t, bool d) { return e.AssignAreaLoad(t, pat, v, dir, cs, rep, clear, d); });
            });

        Add("define_load_pattern", "Adds a load pattern (with its linear static case of the same name) or changes an existing one's type / " +
            "self-weight multiplier." + EditNote,
            Schema(new[] { "name" }, "name", Prop("string", "Pattern name."),
                "type", Prop("string", "Dead, SuperDead, Live, ReduceLive, Quake, Wind, Snow, Other, ... (required for a new pattern)."),
                "self_weight_multiplier", Prop("number", "Self weight multiplier (1 includes the model's weight)."), "dry_run", DryRun),
            delegate(Dictionary<string, object> a)
            {
                string name = Json.Str(a, "name"), type = Json.Str(a, "type"); double? sw = Json.Num(a, "self_weight_multiplier");
                return EditModel("define_load_pattern", a, delegate(SapEdit e, bool d) { return e.DefinePattern(name, type, sw, d); });
            });

        Add("define_load_case", "Adds or redefines a linear or nonlinear static load case: its loads (pattern, scale) replace the old " +
            "ones; initial_case is the case it continues from ('' = zero initial conditions)." + EditNote,
            Schema(new[] { "name", "loads" }, "name", Prop("string", "Case name."),
                "type", Enum("linear_static (default) or nonlinear_static.", new[] { "linear_static", "nonlinear_static" }),
                "loads", Json.O("type", "array", "items", Json.O("type", "object", "properties", Json.O("pattern", Json.O("type", "string"), "scale", Json.O("type", "number")), "required", new[] { "pattern" }),
                    "description", "[{\"pattern\": name, \"scale\": factor (default 1)}]."),
                "initial_case", Prop("string", "Case to continue from ('' = zero; omit = unchanged)."), "dry_run", DryRun),
            delegate(Dictionary<string, object> a)
            {
                string name = Json.Str(a, "name"), type = Json.Str(a, "type") ?? "linear_static", init = Json.Str(a, "initial_case");
                if (type != "linear_static" && type != "nonlinear_static") throw new ToolError("type is linear_static or nonlinear_static");
                List<KeyValuePair<string, double>> loads = Pairs(a, "loads", "pattern");
                return EditModel("define_load_case", a, delegate(SapEdit e, bool d) { return e.DefineStaticCase(name, type == "nonlinear_static", loads, init, d); });
            });

        Add("define_combo", "Adds or redefines a load combination: type linear (additive, default), envelope, absolute, srss or range; its " +
            "items (case or combination, scale) replace the old ones." + EditNote,
            Schema(new[] { "name", "items" }, "name", Prop("string", "Combination name."),
                "type", Enum("Combination type.", SapEdit.ComboTypes),
                "items", Json.O("type", "array", "items", Json.O("type", "object", "properties", Json.O("name", Json.O("type", "string"), "scale", Json.O("type", "number")), "required", new[] { "name" }),
                    "description", "[{\"name\": case or combination, \"scale\": factor (default 1)}]."), "dry_run", DryRun),
            delegate(Dictionary<string, object> a)
            {
                string name = Json.Str(a, "name"), type = Json.Str(a, "type");
                List<KeyValuePair<string, double>> items = Pairs(a, "items", "name");
                return EditModel("define_combo", a, delegate(SapEdit e, bool d) { return e.DefineCombo(name, type, items, d); });
            });

        Add("set_group", "Adds objects to and / or removes them from a SAP2000 group (created when missing): add / remove name lists of " +
            "one kind, add_selection adds every object of a saved selection, add_group every member of another group." + EditNote,
            Schema(new[] { "group" }, "group", Prop("string", "Group name."),
                "kind", Enum("Kind of the names in add / remove.", Kinds),
                "add", Json.O("type", "array", "items", Json.O("type", "string"), "description", "Names to add."),
                "remove", Json.O("type", "array", "items", Json.O("type", "string"), "description", "Names to remove."),
                "add_selection", Prop("string", "Saved selection whose objects are added."),
                "add_group", Prop("string", "Group whose members are added."), "dry_run", DryRun),
            SetGroup);

        Add("move_joints", "Moves joints by delta [dx, dy, dz] (global), or one joint to [x, y, z]. Connected objects follow." + EditNote,
            Schema(Cat(Targets("joint"), new object[] {
                "delta", Json.O("type", "array", "items", Json.O("type", "number"), "minItems", 3, "maxItems", 3, "description", "[dx, dy, dz]."),
                "to", Json.O("type", "array", "items", Json.O("type", "number"), "minItems", 3, "maxItems", 3, "description", "[x, y, z] for one joint."),
                "dry_run", DryRun })),
            delegate(Dictionary<string, object> a)
            {
                double[] delta = Json.Nums(a, "delta"), to = Json.Nums(a, "to");
                return Edit("move_joints", a, "joint", delegate(SapEdit e, List<string> t, bool d) { return e.MoveJoints(t, delta, to, d); });
            });

        Add("add_frame", "Adds a frame between joints i and j, or between points from and to (joints made where none exists), with a section." + EditNote,
            Schema(new[] { "section" }, "i", Prop("string", "Start joint."), "j", Prop("string", "End joint."),
                "from", Json.O("type", "array", "items", Json.O("type", "number"), "minItems", 3, "maxItems", 3, "description", "[x, y, z] start (global)."),
                "to", Json.O("type", "array", "items", Json.O("type", "number"), "minItems", 3, "maxItems", 3, "description", "[x, y, z] end."),
                "section", Prop("string", "Frame section."), "name", Prop("string", "Frame name (default: SAP2000 picks one)."), "dry_run", DryRun),
            delegate(Dictionary<string, object> a)
            {
                string i = Json.Str(a, "i"), j = Json.Str(a, "j"), sec = Json.Str(a, "section"), name = Json.Str(a, "name");
                double[] xi = Json.Nums(a, "from"), xj = Json.Nums(a, "to");
                return EditModel("add_frame", a, delegate(SapEdit e, bool d) { return e.AddFrame(i, j, xi, xj, sec, name, d); });
            });

        Add("delete_objects", "Deletes frames, areas or links with their loads and assignments (joints go with their last object)." + EditNote,
            Schema(new[] { "kind" }, Cat(new object[] { "kind", Enum("frame, area or link.", new[] { "frame", "area", "link" }) }, TargetsOf("objects"), new object[] { "dry_run", DryRun })),
            delegate(Dictionary<string, object> a)
            {
                string kind = Kind(a, "kind", true);
                return Edit("delete_objects", a, kind, delegate(SapEdit e, List<string> t, bool d) { return e.DeleteObjects(kind, t, d); });
            });

        Add("revert", "Reopens a checkpoint of the current session (rNNN, see list_checkpoints) in SAP2000, replacing what it holds. A run " +
            "checkpoint's results are queryable again; the next edit or run saves a new checkpoint, so rNNN stays as it is.",
            Schema(new[] { "checkpoint" }, "checkpoint", Prop("string", "e.g. r000 (the model as opened), r002."),
                "session_dir", Prop("string", "Another session's folder (default: the current model's).")),
            delegate(Dictionary<string, object> a)
            {
                string cp = Json.Str(a, "checkpoint");
                if (string.IsNullOrEmpty(cp)) throw new ToolError("checkpoint is required (list_checkpoints)");
                return ctx.Sap.Revert(cp, Json.Str(a, "session_dir"));
            });

        Add("list_checkpoints", "The session's checkpoints: each rNNN with when it was saved, whether it was run (case status, " +
            "equilibrium), the edits made into it, and which one SAP2000 holds. Read only.",
            Schema("session_dir", Prop("string", "Another session's folder (default: the current model's).")),
            ListCheckpoints);

        Add("get_change_log", "The session's edit log (changes.jsonl): tool, arguments, checkpoint, time, and the changes (first 20 " +
            "per edit; the file has up to 2000). Read only.",
            Schema("checkpoint", Prop("string", "Only edits into this checkpoint."), "offset", Prop("integer", "First entry (default 0)."),
                "limit", Prop("integer", "Entries (default 50)."), "session_dir", Prop("string", "Another session's folder.")),
            GetChangeLog);
    }

    object Status(Dictionary<string, object> a)
    {
        return Json.O(
            "server", Json.O("version", PlutoMcp.ServerVersion, "workDir", ctx.WorkDir, "build", ctx.Build.ToJson()),
            "programs", new object[] { ctx.Sap.Status() },
            "capabilities", Json.O(ctx.Sap.Program, ctx.Sap.Capabilities()));
    }

    object SelfTest(Dictionary<string, object> a)
    {
        string dir = Path.Combine(ctx.WorkDir, "selftest", DateTime.Now.ToString("yyyyMMdd-HHmmss", Json.Inv));
        return ctx.Sap.SelfTest(dir, Json.Bool(a, "use_running_instance", false));
    }

    static readonly string[] Kinds = { "joint", "frame", "area", "link" };

    object OpenModel(Dictionary<string, object> a)
    {
        string path = Json.Str(a, "path");
        if (string.IsNullOrEmpty(path)) throw new ToolError("path is required");
        path = Path.GetFullPath(path);
        string dir = NewSessionDir(path);
        return ctx.Sap.OpenModel(path, dir);
    }

    object Describe(Dictionary<string, object> a)
    {
        int cap = Limit(a, "max_rows");
        return ctx.Sap.Read("describe_model", delegate(SapRead r) { return r.Describe(cap); });
    }

    object ListObjects(Dictionary<string, object> a)
    {
        string kind = Kind(a, "kind", true);
        string group = Json.Str(a, "group"), selection = Json.Str(a, "selection");
        int offset = Json.Int(a, "offset", 0), limit = Limit(a, "limit");
        List<string> saved = selection == null ? null : SelectionNames(LoadSelection(selection), kind);
        return ctx.Sap.Read("list_objects", delegate(SapRead r)
        {
            List<string> only = saved;
            if (group != null)
            {
                if (!r.GroupExists(group)) throw new ToolError("no group '" + group + "'");
                List<string> members = r.GroupMembers(group, kind);
                only = only == null ? members : members.FindAll(delegate(string n) { return saved.Contains(n); });
            }
            return r.ListObjects(kind, only, offset, limit);
        });
    }

    object GetObject(Dictionary<string, object> a)
    {
        string kind = Kind(a, "kind", true), name = Json.Str(a, "name");
        if (string.IsNullOrEmpty(name)) throw new ToolError("name is required");
        return ctx.Sap.Read("get_object", delegate(SapRead r) { return r.GetObject(kind, name); });
    }

    object GetSections(Dictionary<string, object> a)
    {
        string kind = Json.Str(a, "kind") ?? "frame", name = Json.Str(a, "name");
        if (kind != "frame" && kind != "area" && kind != "link") throw new ToolError("kind must be frame, area or link");
        int offset = Json.Int(a, "offset", 0), limit = Limit(a, "limit");
        return ctx.Sap.Read("get_sections", delegate(SapRead r)
        {
            SapRead.Index x = r.Objects();
            List<Dictionary<string, object>> rows =
                kind == "frame" ? r.FrameSectionRows(name, true, x) :
                kind == "area" ? r.AreaSectionRows(name, x) : r.LinkPropRows(name, x);
            return Page(Json.O("kind", kind, "units", x.Units), "sections", rows, offset, limit);
        });
    }

    object GetMaterials(Dictionary<string, object> a)
    {
        string name = Json.Str(a, "name");
        return ctx.Sap.Read("get_materials", delegate(SapRead r) { return Json.O("units", r.Objects().Units, "materials", r.MaterialRows(name, true)); });
    }

    object GetLoadCases(Dictionary<string, object> a)
    {
        string name = Json.Str(a, "name");
        return ctx.Sap.Read("get_load_cases", delegate(SapRead r) { return Json.O("loadCases", r.CaseRows(name, true)); });
    }

    object GetCombos(Dictionary<string, object> a)
    {
        string name = Json.Str(a, "name");
        return ctx.Sap.Read("get_combos", delegate(SapRead r) { return Json.O("combos", r.ComboRows(name, true)); });
    }

    object SelectObjects(Dictionary<string, object> a)
    {
        SapRead.Filter f = new SapRead.Filter();
        string kinds = Json.Str(a, "kind");
        if (kinds != null)
        {
            List<string> ks = new List<string>();
            foreach (string k in kinds.Split(','))
            {
                string kk = k.Trim().ToLowerInvariant().TrimEnd('s');
                if (Array.IndexOf(Kinds, kk) < 0) throw new ToolError("unknown kind '" + k + "'");
                ks.Add(kk);
            }
            f.Kinds = ks.ToArray();
        }
        f.ZMin = Json.Num(a, "z_min"); f.ZMax = Json.Num(a, "z_max");
        double[] box = Json.Nums(a, "box");
        if (box != null) { if (box.Length != 6) throw new ToolError("box is [xmin, ymin, zmin, xmax, ymax, zmax]"); f.Box = box; }
        Dictionary<string, object> plane = Json.Obj(a, "plane");
        if (plane != null)
        {
            f.PlaneAxis = (Json.Str(plane, "axis") ?? "").ToLowerInvariant();
            double? v = Json.Num(plane, "value");
            if ((f.PlaneAxis != "x" && f.PlaneAxis != "y" && f.PlaneAxis != "z") || v == null)
                throw new ToolError("plane is {\"axis\": \"x\"|\"y\"|\"z\", \"value\": number}");
            f.PlaneValue = v.Value;
        }
        double? tol = Json.Num(a, "tolerance");
        f.Match = Json.Str(a, "match") ?? "all";
        if (f.Match != "all" && f.Match != "any" && f.Match != "centroid") throw new ToolError("match is all, any or centroid");
        f.Property = Json.Str(a, "section");
        f.NameLike = Json.Str(a, "name");
        string group = Json.Str(a, "group"), selection = Json.Str(a, "selection"), saveAs = Json.Str(a, "save_as");
        if (saveAs != null && Safe(saveAs) != saveAs) throw new ToolError("save_as: letters, digits, - and _ only");
        Dictionary<string, object> saved = selection == null ? null : LoadSelection(selection);
        bool show = Json.Bool(a, "show_in_sap", false);
        int limit = Limit(a, "limit");

        return ctx.Sap.Read("select_objects", delegate(SapRead r)
        {
            SapRead.Index x = r.Objects();
            f.Tol = tol ?? Math.Max(1e-9, 0.001 * r.LargestExtent());
            if (group != null && !r.GroupExists(group)) throw new ToolError("no group '" + group + "'");
            if (group != null || saved != null)
            {
                f.GroupKinds = new Dictionary<string, HashSet<string>>();
                foreach (string k in Kinds)
                {
                    HashSet<string> set = group == null ? null : new HashSet<string>(r.GroupMembers(group, k));
                    if (saved != null)
                    {
                        HashSet<string> s2 = new HashSet<string>(SelectionNames(saved, k));
                        if (set == null) set = s2; else set.IntersectWith(s2);
                    }
                    f.GroupKinds[k] = set;
                }
            }
            Dictionary<string, List<string>> sel = r.Select(f);
            Dictionary<string, object> res = Json.O("units", x.Units, "tolerance", f.Tol, "summary", r.SelectionSummary(sel));
            Dictionary<string, object> names = new Dictionary<string, object>();
            foreach (KeyValuePair<string, List<string>> kv in sel)
                names[kv.Key] = kv.Value.Count <= limit ? kv.Value : kv.Value.GetRange(0, limit);
            res["names"] = names;
            if (saveAs != null)
            {
                string path = SelectionPath(saveAs);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Json.Write(Json.O("name", saveAs, "model", r.ModelPath(),
                    "savedUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv), "criteria", a, "objects", sel)));
                res["savedAs"] = path;
            }
            if (show)
            {
                bool wholeGroup = group != null && saved == null && f.Kinds == null && f.ZMin == null && f.ZMax == null &&
                    f.Box == null && f.PlaneAxis == null && f.Property == null && f.NameLike == null;
                int shown = r.ShowSelection(sel, wholeGroup ? group : null);
                if (shown >= 0) res["selectedInSap"] = shown;
                else res["selectedInSap"] = "not shown: " + (-shown) + " objects; SAP2000 selects about 30 objects a second, so " +
                    "show_in_sap is limited to " + SapRead.ShowCap + " (a whole group, given alone, has no limit)";
            }
            return res;
        });
    }

    object DescribeLevels(Dictionary<string, object> a)
    {
        double tol = Json.Num(a, "tolerance") ?? 0;
        return ctx.Sap.Read("describe_levels", delegate(SapRead r) { return r.Levels(tol, SapRead.DefaultLimit); });
    }

    object ReadTable(Dictionary<string, object> a)
    {
        string name = Json.Str(a, "name"), group = Json.Str(a, "group"), search = Json.Str(a, "search");
        int offset = Json.Int(a, "offset", 0), limit = Limit(a, "limit");
        Dictionary<string, string> filter = null;
        Dictionary<string, object> fo = Json.Obj(a, "filter");
        if (fo != null)
        {
            filter = new Dictionary<string, string>();
            foreach (KeyValuePair<string, object> kv in fo) filter[kv.Key] = Convert.ToString(kv.Value, Json.Inv);
        }
        string[] fields = Json.Strs(a, "fields");
        bool toFile = Json.Bool(a, "to_file", false);
        if (name == null) return ctx.Sap.Read("read_table", delegate(SapRead r) { return r.Tables(search, offset, limit); });
        return ctx.Sap.Read("read_table", delegate(SapRead r)
        {
            if (group != null && !r.GroupExists(group)) throw new ToolError("no group '" + group + "'");
            List<Dictionary<string, string>> rows = r.TableRows(name, group, filter);
            if (fields != null)
                rows = rows.ConvertAll(delegate(Dictionary<string, string> row)
                {
                    Dictionary<string, string> d = new Dictionary<string, string>();
                    foreach (string fl in fields) { string v; if (row.TryGetValue(fl, out v)) d[fl] = v; }
                    return d;
                });
            Dictionary<string, object> res = Json.O("table", name, "units", r.Objects().Units, "precision", "display-rounded text (input data only)");
            if (rows.Count == 0)
                res["note"] = "no rows: the table is empty, the filter matched nothing, or the key is wrong (read_table without name lists the tables)";
            if (toFile && rows.Count > 0) res["file"] = WriteTsv(name, rows);
            return Page(res, "rows", rows, offset, limit);
        });
    }

    // ---------------------------------------------------------------- phase 2 handlers

    object RunAnalysis(Dictionary<string, object> a)
    {
        string[] cases = Json.Strs(a, "cases");
        if (cases != null && cases.Length == 0) cases = null;
        return ctx.Sap.RunAnalysis(cases, Json.Bool(a, "summary", true), delegate(string path) { return NewSessionDir(path); });
    }

    object QueryResults(Dictionary<string, object> a)
    {
        SapResults.ResultQuery q = new SapResults.ResultQuery();
        q.Kind = Json.Str(a, "kind");
        if (q.Kind == null || Array.IndexOf(SapResults.ResultKinds, q.Kind) < 0) throw new ToolError("kind must be " + string.Join(", ", SapResults.ResultKinds));
        q.Quantity = Json.Str(a, "quantity");
        if (string.IsNullOrEmpty(q.Quantity)) throw new ToolError("quantity is required: " + string.Join(", ", SapResults.Quantities(q.Kind)));
        q.Cases = Json.Strs(a, "cases");
        q.Top = Math.Min(500, Math.Max(1, Json.Int(a, "top_n", 20)));
        q.Envelope = Json.Bool(a, "envelope", true);
        q.Sort = Json.Str(a, "sort") ?? "abs";
        if (q.Sort != "abs" && q.Sort != "max" && q.Sort != "min") throw new ToolError("sort is abs, max or min");
        string[] byGroup = Json.Strs(a, "by_group");
        string group = Json.Str(a, "group"), selection = Json.Str(a, "selection"), name = Json.Str(a, "name");
        Dictionary<string, object> saved = selection == null ? null : LoadSelection(selection);
        if (Json.Bool(a, "to_file", false))
            q.ToFile = Path.Combine(ctx.WorkDir, "results", Safe(q.Kind + "-" + q.Quantity) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Json.Inv) + ".tsv");
        return ctx.Sap.Results("query_results", delegate(SapRead r, SapResults res)
        {
            q.Only = Only(r, SapResults.ObjectKind(q.Kind), group, saved, name);
            if (byGroup != null && byGroup.Length > 0)
            {
                List<string> gs = new List<string>();
                foreach (string g in r.GroupRows().ConvertAll(delegate(Dictionary<string, object> d) { return (string)d["name"]; }))
                    foreach (string p in byGroup) if (SapRead.Like(g, p) && !gs.Contains(g)) gs.Add(g);
                if (gs.Count == 0) throw new ToolError("by_group matched no group");
                q.Groups = gs.Count > 100 ? gs.GetRange(0, 100) : gs;
            }
            return res.Query(q);
        });
    }

    object ReactionsSummary(Dictionary<string, object> a)
    {
        string[] cases = Json.Strs(a, "cases");
        double tol = Json.Num(a, "tolerance") ?? SapDriver.DefaultTolerance;
        return ctx.Sap.Results("reactions_summary", delegate(SapRead r, SapResults res) { return res.Reactions(cases, tol); });
    }

    object CompareRuns(Dictionary<string, object> a)
    {
        string kind = Json.Str(a, "kind"), quantity = Json.Str(a, "quantity"), ra = Json.Str(a, "a"), rb = Json.Str(a, "b");
        string group = Json.Str(a, "group"), selection = Json.Str(a, "selection");
        string[] cases = Json.Strs(a, "cases");
        int top = Math.Min(500, Math.Max(1, Json.Int(a, "top_n", 20)));
        Dictionary<string, object> saved = selection == null ? null : LoadSelection(selection);
        if (kind != null && Array.IndexOf(SapResults.ResultKinds, kind) < 0) throw new ToolError("kind must be " + string.Join(", ", SapResults.ResultKinds));
        HashSet<string> only = null;
        string model = ctx.Sap.Read("compare_runs", delegate(SapRead r)
        {
            if (kind != null) only = Only(r, SapResults.ObjectKind(kind), group, saved, null);
            return r.ModelPath();
        });
        string dir = SapDriver.SessionDirOf(model);
        if (dir == null) throw new ToolError("SAP2000 holds " + model + ", which is not a Pluto session: open_model, then run_analysis (twice) first");
        string runsDir = Path.Combine(dir, "runs");
        List<string> runs = new List<string>();
        if (Directory.Exists(runsDir)) foreach (string d in Directory.GetDirectories(runsDir)) runs.Add(Path.GetFileName(d));
        runs.Sort(StringComparer.Ordinal);
        if (kind == null) return Json.O("sessionDir", dir, "runs", runs, "note", runs.Count < 2 ? "compare_runs needs two runs: run_analysis again after a change" : "pass kind and quantity to compare");
        if (string.IsNullOrEmpty(quantity)) throw new ToolError("quantity is required: " + string.Join(", ", SapResults.Quantities(kind)));
        if (rb == null) { if (runs.Count == 0) throw new ToolError("no runs in this session: run_analysis first"); rb = runs[runs.Count - 1]; }
        if (ra == null)
        {
            int ib = runs.IndexOf(rb);
            if (ib < 1) throw new ToolError("no run before " + rb + " (runs: " + string.Join(", ", runs.ToArray()) + ")");
            ra = runs[ib - 1];
        }
        string fa = Path.Combine(runsDir, ra, kind + ".tsv"), fb = Path.Combine(runsDir, rb, kind + ".tsv");
        foreach (string f in new[] { fa, fb })
            if (!File.Exists(f)) throw new ToolError("no " + kind + " envelope for " + Path.GetFileName(Path.GetDirectoryName(f)) + " (runs: " + string.Join(", ", runs.ToArray()) + ")");
        Dictionary<string, object> res;
        try { res = SapResults.Compare(fa, fb, quantity, only, cases, top); }
        catch (ArgumentException e) { throw new ToolError(e.Message); }
        res["kind"] = kind; res["a"] = ra; res["b"] = rb; res["runs"] = runs;
        res["basis"] = "per object and case: the value of largest magnitude over stations / joints / steps; change = b - a";
        return res;
    }

    object ExportViewer(Dictionary<string, object> a)
    {
        bool wantResults = Json.Bool(a, "results", true), finalOnly = Json.Bool(a, "final_only", false), cyl = Json.Bool(a, "cylindrical", false);
        bool hasResults = false;
        string model = ctx.Sap.Results("export_viewer", delegate(SapRead r, SapResults res)
        {
            hasResults = res.ResolveCases(null).Count > 0;
            return r.ModelPath();
        });
        string dir = SapDriver.SessionDirOf(model) ?? Path.Combine(ctx.WorkDir, "viewer");
        string name = Safe(Path.GetFileNameWithoutExtension(model)), id = name;
        string sessionFile = Path.Combine(dir, "session.json");
        if (File.Exists(sessionFile))
        {
            Dictionary<string, object> sj = Json.Parse(File.ReadAllText(sessionFile)) as Dictionary<string, object>;
            string orig = sj == null ? null : Json.Str(sj, "original");
            if (orig != null) id = Safe(Path.GetFileNameWithoutExtension(orig)) + "-" + name;
        }
        string outBase = Path.Combine(dir, "viewer", name);
        Directory.CreateDirectory(Path.GetDirectoryName(outBase));
        string runner = Path.GetFullPath(Path.Combine(ctx.ExeDir, @"..\..\sap\Run-Sap.ps1"));
        if (!File.Exists(runner)) throw new ToolError("Run-Sap.ps1 not found at " + runner);
        StringBuilder args = new StringBuilder("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + runner + "\" -KeepOpen -Export \"" + outBase + "\" -ModelId \"sap/" + id + "\"");
        if (!wantResults || !hasResults) args.Append(" -NoResults");
        if (finalOnly) args.Append(" -FinalOnly");
        if (cyl) args.Append(" -Cylindrical");

        System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), args.ToString());
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        string output, errors;
        using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
        {
            p.StandardInput.Close();
            System.Threading.Tasks.Task<string> so = p.StandardOutput.ReadToEndAsync(), se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30 * 60 * 1000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new ToolError("export did not finish within 30 minutes"); }
            output = so.Result; errors = se.Result;
            if (p.ExitCode != 0)
            {
                File.WriteAllText(outBase + ".export.log", output + "\n" + errors);
                throw new ToolError("Run-Sap.ps1 -Export failed (exit " + p.ExitCode + "): " + Tail(errors.Length > 0 ? errors : output, 1500) + " (log: " + outBase + ".export.log)");
            }
        }
        File.WriteAllText(outBase + ".export.log", output + (errors.Length > 0 ? "\n" + errors : ""));
        List<string> steps = new List<string>();
        Dictionary<string, object> result = null;
        foreach (string line in output.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            if (l.StartsWith("RESULT ")) result = Json.Parse(l.Substring(7)) as Dictionary<string, object>;
            else if (l.StartsWith("[") && steps.Count < 40) steps.Add(l);
        }
        Dictionary<string, object> reply = Json.O("bin", outBase + ".bin", "features", outBase + ".features.json",
            "modelS2k", outBase + ".model.s2k", "results", wantResults && hasResults ? (object)(outBase + ".results.s2k") : "not exported" + (hasResults ? "" : ": the model has no results"),
            "seconds", Math.Round(sw.Elapsed.TotalSeconds, 1), "log", outBase + ".export.log", "steps", steps,
            "open", "open viewer\\index.html in a browser and pick the .bin (keep the .features.json beside it for groups)");
        if (result == null) reply["warning"] = "Run-Sap.ps1 printed no RESULT line: see the log";
        if (!File.Exists(outBase + ".bin")) reply["warning"] = "no .bin was written: see the log";
        return reply;
    }

    // ---------------------------------------------------------------- phase 3 handlers

    static readonly Dictionary<string, object> DryRun = Prop("boolean", "Only report what would change (default false).");

    delegate Dictionary<string, object> TargetEdit(SapEdit e, List<string> targets, bool dryRun);
    delegate Dictionary<string, object> ModelEdit(SapEdit e, bool dryRun);

    // An edit of named objects of one kind (names / group / selection, combined).
    object Edit(string tool, Dictionary<string, object> a, string kind, TargetEdit fn)
    {
        string selection = Json.Str(a, "selection");
        Dictionary<string, object> saved = selection == null ? null : LoadSelection(selection);
        bool dry = Json.Bool(a, "dry_run", false);
        return ctx.Sap.Edit(tool, a, dry, delegate(SapRead r, SapEdit e)
        {
            List<string> t = Resolve(r, kind, a, saved);
            if (t.Count == 0) throw new ToolError("no " + kind + "s match");
            Dictionary<string, object> res = fn(e, t, dry);
            res["targets"] = t.Count;
            return res;
        }, NewSessionDir);
    }

    object EditModel(string tool, Dictionary<string, object> a, ModelEdit fn)
    {
        bool dry = Json.Bool(a, "dry_run", false);
        return ctx.Sap.Edit(tool, a, dry, delegate(SapRead r, SapEdit e) { return fn(e, dry); }, NewSessionDir);
    }

    static object[] Targets(string kind) { return TargetsOf(kind + "s"); }

    static object[] TargetsOf(string what)
    {
        return new object[] {
            "names", Json.O("type", "array", "items", Json.O("type", "string"), "description", "Names of the " + what + "."),
            "group", Prop("string", "Every one of the " + what + " in this SAP2000 group."),
            "selection", Prop("string", "Every one of the " + what + " in a selection saved by select_objects.") };
    }

    static object[] Cat(params object[][] parts)
    {
        List<object> all = new List<object>();
        foreach (object[] p in parts) all.AddRange(p);
        return all.ToArray();
    }

    // Objects of one kind named by names, group and saved selection together (at least one), in model order.
    static List<string> Resolve(SapRead r, string kind, Dictionary<string, object> a, Dictionary<string, object> saved)
    {
        string[] names = Json.Strs(a, "names");
        string group = Json.Str(a, "group");
        if (names == null && group == null && saved == null) throw new ToolError("say which " + kind + "s: names, group or selection");
        SapRead.Index x = r.Objects();
        List<string> all = x.Names(kind);
        HashSet<string> set = null;
        if (names != null)
        {
            HashSet<string> known = new HashSet<string>(all);
            foreach (string n in names) if (!known.Contains(n)) throw new ToolError("no " + kind + " '" + n + "'");
            set = new HashSet<string>(names);
        }
        if (group != null)
        {
            if (!r.GroupExists(group)) throw new ToolError("no group '" + group + "'");
            HashSet<string> g = new HashSet<string>(r.GroupMembers(group, kind));
            if (set == null) set = g; else set.IntersectWith(g);
        }
        if (saved != null)
        {
            HashSet<string> sv = new HashSet<string>(SelectionNames(saved, kind));
            if (set == null) set = sv; else set.IntersectWith(sv);
        }
        return all.FindAll(delegate(string n) { return set.Contains(n); });
    }

    static bool[] Dofs(string[] given, string[] all, string key)
    {
        if (given == null) throw new ToolError(key + " is required: a list of " + string.Join(", ", all) + " ([] for none)");
        bool[] b = new bool[6];
        foreach (string g in given)
        {
            int i = Array.FindIndex(all, delegate(string d) { return string.Equals(d, g, StringComparison.OrdinalIgnoreCase); });
            if (i < 0) throw new ToolError(key + ": '" + g + "' is not one of " + string.Join(", ", all));
            b[i] = true;
        }
        return b;
    }

    // Load direction names -> SAP2000 codes (1-3 local, 4-6 X Y Z, 7-9 projected, 10 gravity, 11 projected gravity).
    static int DirCode(string d)
    {
        if (d == null) throw new ToolError("dir is required: local1, local2, local3, X, Y, Z, X_projected, Y_projected, Z_projected, gravity, gravity_projected");
        string k = d.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        string[] names = { "", "local1", "local2", "local3", "x", "y", "z", "xprojected", "yprojected", "zprojected", "gravity", "gravityprojected" };
        int i = Array.IndexOf(names, k);
        if (i > 0) return i;
        int n;
        if (int.TryParse(k, out n) && n >= 1 && n <= 11) return n;
        throw new ToolError("dir '" + d + "' is not one of local1, local2, local3, X, Y, Z, X_projected, Y_projected, Z_projected, gravity, gravity_projected");
    }

    // [{key: name, scale: number}] -> (name, scale), scale default 1.
    static List<KeyValuePair<string, double>> Pairs(Dictionary<string, object> a, string key, string nameKey)
    {
        List<KeyValuePair<string, double>> r = new List<KeyValuePair<string, double>>();
        object v;
        if (!a.TryGetValue(key, out v) || !(v is object[])) throw new ToolError(key + " is required: [{\"" + nameKey + "\": ..., \"scale\": ...}]");
        foreach (object o in (object[])v)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            string n = d == null ? null : Json.Str(d, nameKey);
            if (string.IsNullOrEmpty(n)) throw new ToolError("every " + key + " entry needs \"" + nameKey + "\"");
            r.Add(new KeyValuePair<string, double>(n, Json.Num(d, "scale") ?? 1));
        }
        return r;
    }

    object SetMaterial(Dictionary<string, object> a)
    {
        string mat = Json.Str(a, "material");
        Dictionary<string, double> v = new Dictionary<string, double>();
        string[][] map = { new[] { "E", "E" }, new[] { "nu", "nu" }, new[] { "alpha", "alpha" }, new[] { "weight_per_volume", "weightPerVolume" },
            new[] { "Fy", "Fy" }, new[] { "Fu", "Fu" }, new[] { "fc", "fc" } };
        foreach (string[] m in map) { double? x = Json.Num(a, m[0]); if (x != null) v[m[1]] = x.Value; }
        if (v.Count == 0) throw new ToolError("give at least one of E, nu, alpha, weight_per_volume, Fy, Fu, fc");
        return EditModel("set_material_property", a, delegate(SapEdit e, bool d) { return e.SetMaterial(mat, v, d); });
    }

    object AssignFrameLoad(Dictionary<string, object> a)
    {
        string pat = Json.Str(a, "pattern"), type = Json.Str(a, "type"), load = Json.Str(a, "load") ?? "force";
        if (type != "distributed" && type != "point") throw new ToolError("type is distributed or point");
        if (load != "force" && load != "moment") throw new ToolError("load is force or moment");
        int dir = DirCode(Json.Str(a, "dir"));
        bool point = type == "point", rel = Json.Bool(a, "relative", true), rep = Json.Bool(a, "replace", true), clear = Json.Bool(a, "clear", false);
        if (!clear && Json.Num(a, "value") == null) throw new ToolError("value is required (or clear)");
        double v1 = Json.Num(a, "value") ?? 0, v2 = Json.Num(a, "end_value") ?? v1;
        double d1 = Json.Num(a, "start") ?? (point ? (rel ? 0.5 : 0) : 0), d2 = Json.Num(a, "end") ?? 1;
        if (!point && !rel && Json.Num(a, "end") == null) throw new ToolError("end is required when relative is false");
        string cs = Json.Str(a, "csys") ?? (dir <= 3 ? "Local" : "GLOBAL");
        return Edit("assign_frame_load", a, "frame", delegate(SapEdit e, List<string> t, bool d)
        {
            return e.AssignFrameLoad(t, pat, point, load == "moment", dir, d1, d2, v1, v2, rel, cs, rep, clear, d);
        });
    }

    object SetGroup(Dictionary<string, object> a)
    {
        string group = Json.Str(a, "group"), kind = Kind(a, "kind", false), fromSel = Json.Str(a, "add_selection"), fromGroup = Json.Str(a, "add_group");
        string[] add = Json.Strs(a, "add"), remove = Json.Strs(a, "remove");
        if (string.IsNullOrEmpty(group)) throw new ToolError("group is required");
        if ((add != null || remove != null) && kind == null) throw new ToolError("kind is required with add / remove");
        if (add == null && remove == null && fromSel == null && fromGroup == null) throw new ToolError("give add, remove, add_selection or add_group");
        Dictionary<string, object> saved = fromSel == null ? null : LoadSelection(fromSel);
        bool dry = Json.Bool(a, "dry_run", false);
        return ctx.Sap.Edit("set_group", a, dry, delegate(SapRead r, SapEdit e)
        {
            Dictionary<string, List<string>> plus = new Dictionary<string, List<string>>(), minus = new Dictionary<string, List<string>>();
            HashSet<string> known = kind == null ? null : new HashSet<string>(r.Objects().Names(kind));
            if (add != null) { foreach (string n in add) if (!known.Contains(n)) throw new ToolError("no " + kind + " '" + n + "'"); plus[kind] = new List<string>(add); }
            if (remove != null) minus[kind] = new List<string>(remove);
            foreach (string k in Kinds)
            {
                List<string> more = new List<string>();
                if (saved != null) more.AddRange(SelectionNames(saved, k));
                if (fromGroup != null)
                {
                    if (!r.GroupExists(fromGroup)) throw new ToolError("no group '" + fromGroup + "'");
                    more.AddRange(r.GroupMembers(fromGroup, k));
                }
                if (more.Count == 0) continue;
                List<string> l;
                if (!plus.TryGetValue(k, out l)) { l = new List<string>(); plus[k] = l; }
                foreach (string n in more) if (!l.Contains(n)) l.Add(n);
            }
            return e.SetGroup(group, plus, minus, dry);
        }, NewSessionDir);
    }

    // The session dir named, or the current model's.
    string SessionDirFor(Dictionary<string, object> a)
    {
        string dir = Json.Str(a, "session_dir");
        if (dir != null) { if (!File.Exists(Path.Combine(dir, "session.json"))) throw new ToolError("no Pluto session in " + dir); return dir; }
        string model = ctx.Sap.Read("session", delegate(SapRead r) { return r.ModelPath(); });
        dir = SapDriver.SessionDirOf(model);
        if (dir == null) throw new ToolError("SAP2000 holds " + model + ", which is not a Pluto session: open_model first");
        return dir;
    }

    object ListCheckpoints(Dictionary<string, object> a)
    {
        string dir = SessionDirFor(a);
        string current = null;
        try { current = ctx.Sap.Read("session", delegate(SapRead r) { return r.ModelPath(); }); } catch (ToolError) { }
        Dictionary<string, List<string>> edits = new Dictionary<string, List<string>>();
        foreach (Dictionary<string, object> e in ChangeLines(dir))
        {
            string cp = Json.Str(e, "checkpoint") ?? "?";
            List<string> l;
            if (!edits.TryGetValue(cp, out l)) { l = new List<string>(); edits[cp] = l; }
            l.Add(Json.Str(e, "tool") + ": " + (Json.Str(e, "edit") ?? "") + " (" + Convert.ToString(e.ContainsKey("changed") ? e["changed"] : 0, Json.Inv) + ")");
        }
        List<string> files = new List<string>(Directory.GetFiles(dir, "r*.sdb"));
        files.Sort(StringComparer.OrdinalIgnoreCase);
        List<object> list = new List<object>();
        foreach (string f in files)
        {
            string n = Path.GetFileNameWithoutExtension(f);
            if (!System.Text.RegularExpressions.Regex.IsMatch(n, @"^r\d+$")) continue;
            Dictionary<string, object> d = Json.O("checkpoint", n, "savedUtc", File.GetLastWriteTimeUtc(f).ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv),
                "held", current != null && string.Equals(Path.GetFullPath(current), Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase));
            if (n == "r000") d["what"] = "the model as opened";
            string summary = Path.Combine(dir, "runs", n, "summary.json");
            if (File.Exists(summary))
            {
                Dictionary<string, object> s = Json.Parse(File.ReadAllText(summary)) as Dictionary<string, object>;
                Dictionary<string, object> run = s == null ? null : Json.Obj(s, "run");
                Dictionary<string, object> react = s == null ? null : Json.Obj(s, "reactions");
                List<object> eq = new List<object>();
                object cl;
                if (react != null && react.TryGetValue("cases", out cl) && cl is object[])
                    foreach (object o in (object[])cl) { Dictionary<string, object> c = o as Dictionary<string, object>; if (c != null) eq.Add(Json.Str(c, "case") + " " + Json.Str(c, "check")); }
                d["run"] = Json.O("cases", run == null ? null : run["cases"], "notFinished", run != null && run.ContainsKey("notFinished") ? run["notFinished"] : null, "equilibrium", eq);
            }
            else d["run"] = Directory.Exists(Path.Combine(dir, "runs", n)) ? (object)"run (no summary)" : null;
            List<string> el;
            if (edits.TryGetValue(n, out el)) { d["edits"] = el.Count > 20 ? el.GetRange(0, 20) : el; d["editCount"] = el.Count; }
            list.Add(d);
        }
        Dictionary<string, object> sj = File.Exists(Path.Combine(dir, "session.json")) ? Json.Parse(File.ReadAllText(Path.Combine(dir, "session.json"))) as Dictionary<string, object> : null;
        return Json.O("sessionDir", dir, "original", sj == null ? null : Json.Str(sj, "original"), "checkpoints", list,
            "note", "revert(checkpoint) reopens one; edits and runs go into the latest checkpoint while it is not run, else a new one");
    }

    object GetChangeLog(Dictionary<string, object> a)
    {
        string dir = SessionDirFor(a), cp = Json.Str(a, "checkpoint");
        int offset = Json.Int(a, "offset", 0), limit = Math.Min(500, Math.Max(1, Json.Int(a, "limit", 50)));
        List<Dictionary<string, object>> lines = ChangeLines(dir).FindAll(delegate(Dictionary<string, object> e) { return cp == null || Json.Str(e, "checkpoint") == cp; });
        foreach (Dictionary<string, object> e in lines)
        {
            object c;
            if (e.TryGetValue("changes", out c) && c is object[] && ((object[])c).Length > 20)
            {
                object[] all = (object[])c; object[] first = new object[20]; Array.Copy(all, first, 20);
                e["changes"] = first; e["changesListed"] = 20;
            }
        }
        return Page(Json.O("sessionDir", dir, "file", Path.Combine(dir, "changes.jsonl")), "entries", lines, offset, limit);
    }

    static List<Dictionary<string, object>> ChangeLines(string dir)
    {
        List<Dictionary<string, object>> r = new List<Dictionary<string, object>>();
        string f = Path.Combine(dir, "changes.jsonl");
        if (!File.Exists(f)) return r;
        foreach (string l in File.ReadAllLines(f))
        {
            if (l.Trim().Length == 0) continue;
            Dictionary<string, object> d = Json.Parse(l) as Dictionary<string, object>;
            if (d != null) r.Add(d);
        }
        return r;
    }

    static string Tail(string s, int n) { s = (s ?? "").Trim(); return s.Length <= n ? s : "..." + s.Substring(s.Length - n); }

    // Object names allowed by group, saved selection and name pattern together (null: all).
    static HashSet<string> Only(SapRead r, string kind, string group, Dictionary<string, object> saved, string name)
    {
        HashSet<string> only = null;
        if (group != null)
        {
            if (!r.GroupExists(group)) throw new ToolError("no group '" + group + "'");
            only = new HashSet<string>(r.GroupMembers(group, kind));
        }
        if (saved != null)
        {
            HashSet<string> s = new HashSet<string>(SelectionNames(saved, kind));
            if (only == null) only = s; else only.IntersectWith(s);
        }
        if (name != null)
        {
            HashSet<string> s = new HashSet<string>(r.Objects().Names(kind).FindAll(delegate(string n) { return SapRead.Like(n, name); }));
            if (only == null) only = s; else only.IntersectWith(s);
        }
        return only;
    }

    string NewSessionDir(string modelPath)
    {
        return Path.Combine(ctx.WorkDir, "models", Safe(Path.GetFileNameWithoutExtension(modelPath)), DateTime.Now.ToString("yyyyMMdd-HHmmss", Json.Inv));
    }

    // ---------------------------------------------------------------- helpers

    string WriteTsv(string table, List<Dictionary<string, string>> rows)
    {
        List<string> cols = new List<string>();
        foreach (Dictionary<string, string> r in rows) foreach (string k in r.Keys) if (!cols.Contains(k)) cols.Add(k);
        StringBuilder sb = new StringBuilder();
        sb.Append(string.Join("\t", cols.ToArray())).Append('\n');
        foreach (Dictionary<string, string> r in rows)
        {
            string[] v = new string[cols.Count];
            for (int i = 0; i < cols.Count; i++) { string s; v[i] = r.TryGetValue(cols[i], out s) ? s.Replace('\t', ' ') : ""; }
            sb.Append(string.Join("\t", v)).Append('\n');
        }
        string path = Path.Combine(ctx.WorkDir, "tables", Safe(table) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Json.Inv) + ".tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    // One page of rows; nextOffset when more follow.
    static object Page<T>(Dictionary<string, object> res, string key, List<T> rows, int offset, int limit)
    {
        offset = Math.Max(0, offset);
        int n = Math.Max(0, Math.Min(limit, rows.Count - offset));
        res["total"] = rows.Count; res["offset"] = offset; res["returned"] = n;
        res[key] = n > 0 ? rows.GetRange(offset, n) : new List<T>();
        if (offset + n < rows.Count) res["nextOffset"] = offset + n;
        return res;
    }

    // Saved selections: <work dir>\selections\<name>.json, { name, model, savedUtc, criteria, objects: { kind: [names] } }.
    string SelectionPath(string name) { return Path.Combine(ctx.WorkDir, "selections", Safe(name) + ".json"); }

    Dictionary<string, object> LoadSelection(string name)
    {
        string path = SelectionPath(name);
        if (!File.Exists(path)) throw new ToolError("no saved selection '" + name + "' (select_objects save_as makes one)");
        return Json.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
    }

    static List<string> SelectionNames(Dictionary<string, object> sel, string kind)
    {
        List<string> r = new List<string>();
        Dictionary<string, object> objs = Json.Obj(sel, "objects");
        object v;
        if (objs != null && objs.TryGetValue(kind, out v) && v is object[]) foreach (object o in (object[])v) r.Add(Convert.ToString(o, Json.Inv));
        return r;
    }

    static string Kind(Dictionary<string, object> a, string key, bool required)
    {
        string k = Json.Str(a, key);
        if (k == null) { if (required) throw new ToolError(key + " is required: joint, frame, area or link"); return null; }
        k = k.Trim().ToLowerInvariant().TrimEnd('s');
        if (Array.IndexOf(Kinds, k) < 0) throw new ToolError(key + " must be joint, frame, area or link (got '" + Json.Str(a, key) + "')");
        return k;
    }

    static int Limit(Dictionary<string, object> a, string key)
    {
        int n = Json.Int(a, key, SapRead.DefaultLimit);
        return n <= 0 ? SapRead.DefaultLimit : Math.Min(n, 2000);
    }

    // File-name-safe: letters, digits, - and _ (others become _).
    static string Safe(string s)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in s ?? "") b.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return b.Length == 0 ? "_" : b.ToString();
    }

    // ---------------------------------------------------------------- protocol side

    public object List()
    {
        List<object> list = new List<object>();
        foreach (Tool t in tools) list.Add(Json.O("name", t.Name, "description", t.Description, "inputSchema", t.Schema));
        return Json.O("tools", list);
    }

    public object Call(string name, Dictionary<string, object> args)
    {
        Tool tool = tools.Find(delegate(Tool t) { return t.Name == name; });
        if (tool == null) throw new ArgumentException("unknown tool: " + name);
        try
        {
            ctx.Log.Info("call " + name + " " + Json.Write(args));
            object result = tool.Run(args ?? new Dictionary<string, object>());
            return Content(Json.Write(result), false);
        }
        catch (Exception e)
        {
            ctx.Log.Error("tool " + name, e);
            return Content(e is ToolError || e is TimeoutException ? e.Message : e.GetType().Name + ": " + e.Message, true);
        }
    }

    static Dictionary<string, object> Content(string text, bool isError)
    {
        return Json.O("content", new object[] { Json.O("type", "text", "text", text) }, "isError", isError);
    }

    void Add(string name, string description, Dictionary<string, object> schema, Handler run)
    {
        Tool t = new Tool();
        t.Name = name; t.Description = description; t.Schema = schema; t.Run = run;
        tools.Add(t);
    }

    // Schema("name", Prop(...), ...): an object schema of optional properties.
    static Dictionary<string, object> Schema(params object[] props)
    {
        return Json.O("type", "object", "properties", Json.O(props), "additionalProperties", false);
    }

    static Dictionary<string, object> Schema(string[] required, params object[] props)
    {
        Dictionary<string, object> s = Schema(props);
        s["required"] = required;
        return s;
    }

    static Dictionary<string, object> Enum(string description, string[] values)
    {
        return Json.O("type", "string", "enum", values, "description", description);
    }

    static Dictionary<string, object> Prop(string type, string description)
    {
        return Json.O("type", type, "description", description);
    }
}
