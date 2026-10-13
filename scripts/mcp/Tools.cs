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
            "closed afterwards, so the user's open model is untouched. Takes about a minute. fixtures: the hand-check " +
            "fixtures instead (a plate: self weight, area loads, material weight and E changed through the edit tools; a " +
            "one-mass cantilever: period, response spectrum and step time history), always in a second SAP2000; the result " +
            "is saved as the tool verification design records cite.",
            Schema("use_running_instance", Prop("boolean",
                "Run in the SAP2000 the user has open instead. This REPLACES the model it holds: only with the user's consent."),
                "fixtures", Prop("boolean", "Run the hand-check fixtures (about two minutes).")),
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

        Add("describe_basis",
            "What the model rests on, for a design record or a review: describe_model plus the analysis settings (active " +
            "DOFs, mass sources), frame releases by pattern, materials in full (E, weight, Fy / fc), combinations with " +
            "their items, and each load case's definition: static loads and initial case, nonlinear geometry (P-delta), " +
            "modal modes, response spectrum and modal time history loads with functions, modal case, combination rules, " +
            "damping and steps. Read only; values in the model's present units.",
            Schema("max_rows", Prop("integer", "Rows per list (default 200).")),
            delegate(Dictionary<string, object> a)
            {
                int cap = Limit(a, "max_rows");
                return ctx.Sap.Read("describe_basis", delegate(SapRead r) { return r.Basis(cap); });
            });

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
                "summary", Prop("boolean", "Save envelopes and reactions for compare_runs / the reply (default true)."),
                "update_viewer", Prop("boolean", "When a live viewer is connected, export and reload it after the run (default true).")),
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

        Add("move_joints", "Moves joints by ONE rule in one call; for a shape change (stretch, squash, re-level) give the rule, not a call per " +
            "level: delta [dx, dy, dz] (translate), to [x, y, z] (one joint), scale [sx, sy, sz] about [x, y, z] (e.g. heights x 0.8 about " +
            "the base: scale [1, 1, 0.8], about [0, 0, 0]), or map {axis, from, to} (piecewise-linear: old levels -> new levels; outside " +
            "from's range a joint stays). The server computes every coordinate. Targets: names, group, selection and / or z_min / z_max " +
            "(all combined). Checks before applying: frames / links collapsing or reversing, areas flipping, joints landing on " +
            "others (any of these blocks the edit unless force), and leftBehind: joints connected to the moved ones that the rule " +
            "would also move but were not targeted. Dry-run first and show the user the checks. Connected objects follow." + EditNote,
            Schema(Cat(Targets("joint"), new object[] {
                "z_min", Prop("number", "Target joints with Z >= z_min (global; combined with the other targets)."),
                "z_max", Prop("number", "Target joints with Z <= z_max."),
                "delta", Json.O("type", "array", "items", Json.O("type", "number"), "minItems", 3, "maxItems", 3, "description", "[dx, dy, dz]."),
                "to", Json.O("type", "array", "items", Json.O("type", "number"), "minItems", 3, "maxItems", 3, "description", "[x, y, z] for one joint."),
                "scale", Json.O("type", "array", "items", Json.O("type", "number"), "minItems", 3, "maxItems", 3, "description", "[sx, sy, sz] factors about 'about'."),
                "about", Json.O("type", "array", "items", Json.O("type", "number"), "minItems", 3, "maxItems", 3, "description", "[x, y, z] fixed point of scale (default origin)."),
                "map", Json.O("type", "object", "description", "{\"axis\": \"x\"|\"y\"|\"z\", \"from\": [old levels, increasing], \"to\": [new levels]}.",
                    "properties", Json.O("axis", Json.O("type", "string", "enum", new[] { "x", "y", "z" }),
                        "from", Json.O("type", "array", "items", Json.O("type", "number")), "to", Json.O("type", "array", "items", Json.O("type", "number")))),
                "force", Prop("boolean", "Apply even when the checks find collapsed, reversed, flipped or coincident geometry."),
                "dry_run", DryRun })),
            delegate(Dictionary<string, object> a)
            {
                SapEdit.JointRule rule = new SapEdit.JointRule();
                rule.Delta = Json.Nums(a, "delta"); rule.To = Json.Nums(a, "to"); rule.Scale = Json.Nums(a, "scale"); rule.About = Json.Nums(a, "about");
                Dictionary<string, object> map = Json.Obj(a, "map");
                if (map != null)
                {
                    rule.MapAxis = "xyz".IndexOf((Json.Str(map, "axis") ?? "").ToLowerInvariant());
                    if (rule.MapAxis < 0 || (Json.Str(map, "axis") ?? "").Length != 1) throw new ToolError("map.axis is x, y or z");
                    rule.MapFrom = Json.Nums(map, "from"); rule.MapTo = Json.Nums(map, "to");
                }
                bool force = Json.Bool(a, "force", false);
                return Edit("move_joints", a, "joint", delegate(SapEdit e, List<string> t, bool d) { return e.MoveJoints(t, rule, force, d); });
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
        // ------------------------------------------------ phase 4: drive the viewer

        Add("viewer_open", "Starts the live Pluto viewer: the server hosts it on http://localhost:<port> and opens it in the default " +
            "browser (open_browser false: only returns the URL). The viewer then follows viewer_load / show / highlight / isolate / " +
            "focus, and viewer_get_selection reads what the user clicked. Returns the URL and the viewer's state.",
            Schema("open_browser", Prop("boolean", "Open the URL in the default browser when no viewer is connected (default true).")),
            ViewerOpen);

        Add("viewer_load", "Loads a model into the live viewer: by default the latest export_viewer of the current checkpoint (export: " +
            "true exports first, or when none exists). The .bin streams in slices (HTTP Range), so large files load too. " +
            "SAP2000 models are Z up: the viewer is set to Z up for them unless up says otherwise.",
            Schema("bin", Prop("string", "A .bin under the work dir (default: the current checkpoint's export)."),
                "export", Prop("boolean", "Run export_viewer first (default: only when the checkpoint has no export)."),
                "up", Enum("World up axis: z (SAP2000, ETABS; the default for the current checkpoint's export) or y (STAAD). " +
                    "Default for a bin given by path: the viewer's current setting.", new[] { "z", "y" })),
            ViewerLoad);

        Add("viewer_show", "Chooses what the live viewer shows: the shell result component (e.g. 'SVM Top', as listed in the viewer's " +
            "components), the load case, the beam component, the deformed shape. Names match case-insensitively, a prefix is " +
            "enough; the reply lists the available ones.",
            Schema("component", Prop("string", "Shell result component."), "case", Prop("string", "Load case."),
                "beam_component", Prop("string", "Beam result component."), "deformed", Prop("boolean", "Show the deformed shape.")),
            delegate(Dictionary<string, object> a)
            {
                return Viewer().Command("show", Json.O("component", Json.Str(a, "component"), "case", Json.Str(a, "case"),
                    "beam_component", Json.Str(a, "beam_component"), "deformed", a.ContainsKey("deformed") ? (object)Json.Bool(a, "deformed", false) : null), TimeSpan.FromSeconds(60));
            });

        Add("viewer_highlight", "Paints objects in the live viewer in one colour: a SAP2000 group, a saved selection (select_objects " +
            "save_as) and / or names of one kind; only (default true) turns the other groups off. clear removes the highlight. " +
            "Objects are mapped to the viewer's mesh ids through the export's labels.",
            Schema(Cat(new object[] { "kind", Enum("Kind of the names.", Kinds) }, TargetsOf("objects"), new object[] {
                "color", Prop("string", "#rrggbb (default #ff3030)."), "label", Prop("string", "Name shown in the viewer's Groups tab."),
                "only", Prop("boolean", "Turn the other groups off (default true)."), "clear", Prop("boolean", "Remove the highlight.") })),
            ViewerHighlight);

        Add("viewer_isolate", "Shows only the given objects in the live viewer (a group, a saved selection and / or names of one kind; " +
            "shells and beams), everything else hidden; clear shows everything again.",
            Schema(Cat(new object[] { "kind", Enum("Kind of the names.", Kinds) }, TargetsOf("objects"), new object[] {
                "clear", Prop("boolean", "Show everything again.") })),
            ViewerIsolate);

        Add("viewer_focus", "Moves the live viewer's orbit centre to one object and flashes it: a joint, an area, or a frame / link " +
            "(its first joint).",
            Schema(new[] { "kind", "name" }, "kind", Enum("joint, frame, area or link.", Kinds), "name", Prop("string", "SAP2000 name.")),
            ViewerFocus);

        Add("viewer_get_selection", "What the live viewer shows and what the user pinned (clicked): component, load case, the pinned " +
            "value with its element / node (mapped back to the SAP2000 object), and the groups shown. Read only.",
            Schema(), ViewerSelection);
        // ------------------------------------------------ input tables through Excel

        Add("export_tables", "Exports SAP2000 input tables (read_table lists the keys) to an Excel workbook, one sheet per table, and " +
            "opens it in Excel for the user to look at and edit (open: false only writes it). group: only that group's rows. " +
            "Values are text as SAP2000 writes them. After the user saves, import_tables brings the changes back. Read only.",
            Schema(new[] { "tables" }, "tables", Json.O("type", "array", "items", Json.O("type", "string"), "description", "Table keys, e.g. [\"Frame Section Assignments\", \"Joint Restraint Assignments\"]."),
                "group", Prop("string", "Only rows of this group's objects."),
                "open", Prop("boolean", "Open the workbook in Excel (default true).")),
            ExportTables);

        Add("import_tables", "Brings an exported workbook back into SAP2000 after the user edited it in Excel: per table the changed, " +
            "added and deleted rows against the export, and a warning when the model changed since. dry_run DEFAULTS TO TRUE: " +
            "show the changes to the user, then call again with dry_run false. Applied: into a checkpoint (unlocked), all tables " +
            "in one SAP2000 import, its log returned, rows read back, logged like every edit; revert goes back.",
            Schema("workbook", Prop("string", "The .xlsx (default: the latest export of the current session)."),
                "dry_run", Prop("boolean", "Only show the changes (default true)."),
                "force", Prop("boolean", "Import even though the model's tables changed since the export.")),
            ImportTables);

        // ------------------------------------------------ design record: why the model is as it is

        Add("log_note", "Records an assumption, condition, decision or question about the model in the session's notes.jsonl, with " +
            "its basis, so the design record can show why the model is as it is. Log what the user states or confirms; a note " +
            "the LLM infers is logged only after the user agrees. supersedes: an earlier note this one replaces (a decision " +
            "superseding a question answers it); nothing is deleted. Does not change the model.",
            Schema(new[] { "kind", "text" },
                "kind", Enum("assumption (taken as true without proof), condition (an underlying condition the design rests on: site, " +
                    "existing structure, supplied data), decision (a choice made, and why), question (open, to be answered).", NoteKinds),
                "text", Prop("string", "The note, one or two sentences."),
                "topic", Enum("What it concerns.", NoteTopics),
                "basis", Prop("string", "Where it comes from: code clause, drawing, report, supplied data, engineering judgement."),
                "elements", Json.O("type", "array", "items", Json.O("type", "string"), "description", "Design elements (BOD element note names) it concerns."),
                "group", Prop("string", "SAP2000 group it concerns."),
                "names", Json.O("type", "array", "items", Json.O("type", "string"), "description", "Object names it concerns."),
                "supersedes", Prop("string", "Id of an earlier note this one replaces (list_notes), e.g. n003."),
                "session_dir", Prop("string", "Another session's folder (default: the current model's).")),
            LogNote);

        Add("list_notes", "The session's notes (log_note): id, kind, topic, text, basis, what they concern, checkpoint and time. " +
            "Superseded notes are left out unless asked for (then marked with the note that replaced them). Read only.",
            Schema("kind", Enum("Only this kind.", NoteKinds), "topic", Enum("Only this topic.", NoteTopics),
                "superseded", Prop("boolean", "Include superseded notes (default false)."),
                "session_dir", Prop("string", "Another session's folder (default: the current model's).")),
            ListNotes);

        Add("write_design_record", "Writes the session's design record: one Markdown note for Obsidian in <notes dir>\\Records\\<model>-<session>.md " +
            "(notes dir: notes_dir, else PLUTO_NOTES_DIR). Sections: purpose, model basis (describe_basis of the checkpoint SAP2000 " +
            "holds, else the last one saved), assumptions and conditions (log_note), decisions and the edits with their reasons, " +
            "analysis and checks (case status, log warnings, equilibrium per run), tool verification (hand-check fixtures), open " +
            "questions, session folder. Rewriting regenerates it but keeps text between pluto:notes markers and the properties " +
            "edited by hand (status, author, elements). Does not change the model.",
            Schema("title", Prop("string", "Note title (default: 'Design record <model> <session>'; kept from the note when omitted)."),
                "purpose", Prop("string", "What the session set out to do: the Purpose section, written once (later edits in the note win)."),
                "elements", Json.O("type", "array", "items", Json.O("type", "string"), "description", "Design elements (BOD element note names) the record supports; linked as [[name]]."),
                "author", Prop("string", "Who did the work (kept from the note when omitted)."),
                "notes_dir", Prop("string", "The Obsidian vault folder (default PLUTO_NOTES_DIR)."),
                "session_dir", Prop("string", "Another session's folder (default: the current model's).")),
            WriteDesignRecord);

        Add("draft_bod_element", "Drafts a basis-of-design element note, <notes dir>\\Elements\\<element>.md (notes dir: notes_dir, else " +
            "PLUTO_NOTES_DIR), from the model SAP2000 holds: the group's objects, sections, extent and supports (group), the load " +
            "patterns and combinations, the assumptions and conditions logged for this element (log_note elements), the model and " +
            "its design record. The engineer completes it by hand (criteria, load values and basis, governing checks); it is never " +
            "overwritten. Does not change the model.",
            Schema(new[] { "element" }, "element", Prop("string", "The design element's name, also the note's name (e.g. TANK-A)."),
                "group", Prop("string", "SAP2000 group holding the element's objects."),
                "notes_dir", Prop("string", "The Obsidian vault folder (default PLUTO_NOTES_DIR)."),
                "session_dir", Prop("string", "Session whose notes and record to use (default: the current model's, when it is one).")),
            DraftBodElement);

        // Every edit (the tools with dry_run) takes an optional reason, kept in changes.jsonl for the design record.
        foreach (Tool t in tools)
        {
            Dictionary<string, object> p = Json.Obj(t.Schema, "properties");
            if (p != null && p.ContainsKey("dry_run"))
                p["reason"] = Prop("string", "Why the change is made (optional): logged with the edit for the design record.");
        }
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
        if (Json.Bool(a, "fixtures", false)) return ctx.Sap.Fixtures(dir, Path.Combine(ctx.WorkDir, "fixtures.json"));
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
        Dictionary<string, object> run = ctx.Sap.RunAnalysis(cases, Json.Bool(a, "summary", true), delegate(string path) { return NewSessionDir(path); });
        UpdateViewerAfterRun(a, run);
        return run;
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
        if (ctx.Sap.OwnProcessId > 0) args.Append(" -AttachPid " + ctx.Sap.OwnProcessId.ToString(Json.Inv));

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

    // ---------------------------------------------------------------- tables in Excel

    ExcelTables excel;
    ExcelTables Excel() { if (excel == null) excel = new ExcelTables(); return excel; }

    object ExportTables(Dictionary<string, object> a)
    {
        string[] keys = Json.Strs(a, "tables");
        if (keys == null || keys.Length == 0) throw new ToolError("tables is required: table keys (read_table without a name lists them)");
        string group = Json.Str(a, "group");
        bool open = Json.Bool(a, "open", true);
        string model = null, units = null;
        List<SapTables.Table> full = new List<SapTables.Table>();
        List<List<int>> exported = new List<List<int>>();
        ctx.Sap.Read("export_tables", delegate(SapRead r)
        {
            model = r.ModelPath(); units = r.Objects().Units;
            if (group != null && !r.GroupExists(group)) throw new ToolError("no group '" + group + "'");
            SapTables t = r.Tables();
            foreach (string k in keys)
            {
                SapTables.Table all;
                try { all = t.Read(k, ""); }
                catch (ArgumentException e) { throw new ToolError(e.Message); }
                List<int> ids = new List<int>();
                if (group == null) for (int i = 0; i < all.Rows.Count; i++) ids.Add(i);
                else
                {
                    // the group's rows, located in the full table (first unused equal row)
                    SapTables.Table sub = t.Read(k, group);
                    bool[] taken = new bool[all.Rows.Count];
                    foreach (string[] row in sub.Rows)
                        for (int i = 0; i < all.Rows.Count; i++)
                            if (!taken[i] && SameRow(all.Fields, all.Rows[i], sub.Fields, row)) { taken[i] = true; ids.Add(i); break; }
                }
                full.Add(all); exported.Add(ids);
            }
            return 0;
        });
        string dir = SapDriver.SessionDirOf(model) ?? ctx.WorkDir;
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", Json.Inv);
        string book = Path.Combine(dir, "tables", Safe(Path.GetFileNameWithoutExtension(model)) + "-" + stamp + ".xlsx");
        Directory.CreateDirectory(Path.GetDirectoryName(book));
        string snap = book + ".pluto.json";
        HashSet<string> used = new HashSet<string>();
        List<ExcelTables.Sheet> sheets = new List<ExcelTables.Sheet>();
        List<object> tables = new List<object>();
        for (int i = 0; i < full.Count; i++)
        {
            ExcelTables.Sheet s = new ExcelTables.Sheet();
            s.Table = full[i].Key; s.Name = ExcelTables.SheetName(full[i].Key, used); s.Fields = full[i].Fields;
            foreach (int id in exported[i]) { s.Rows.Add(full[i].Rows[id]); s.RowIds.Add(id); }
            sheets.Add(s);
            tables.Add(Json.O("table", full[i].Key, "sheet", s.Name, "fields", full[i].Fields, "rows", full[i].Rows, "exported", exported[i]));
        }
        File.WriteAllText(snap, Json.Write(Json.O("format", "pluto-tables", "version", 1, "model", model, "units", units, "group", group,
            "exportedUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv), "workbook", book, "tables", tables)));
        Excel().Write(book, sheets, snap);
        if (open) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(book) { UseShellExecute = true });
        List<object> summary = new List<object>();
        for (int i = 0; i < sheets.Count; i++) summary.Add(Json.O("table", sheets[i].Table, "sheet", sheets[i].Name, "rows", sheets[i].Rows.Count, "fields", sheets[i].Fields.Length));
        return Json.O("workbook", book, "opened", open, "model", model, "units", units, "group", group, "tables", summary,
            "note", "Edit and save the workbook in Excel (values are text as SAP2000 writes them; add rows at the bottom, delete " +
                    "whole rows; keep row 1 and the hidden column A). Then import_tables: a dry run shows the changes first.");
    }

    object ImportTables(Dictionary<string, object> a)
    {
        string book = Json.Str(a, "workbook");
        bool dry = Json.Bool(a, "dry_run", true), force = Json.Bool(a, "force", false);
        if (book == null)
        {
            string model = ctx.Sap.Read("import_tables", delegate(SapRead r) { return r.ModelPath(); });
            string tdir = Path.Combine(SapDriver.SessionDirOf(model) ?? ctx.WorkDir, "tables");
            string[] books = Directory.Exists(tdir) ? Directory.GetFiles(tdir, "*.xlsx") : new string[0];
            if (books.Length == 0) throw new ToolError("no exported workbook in " + tdir + ": export_tables first, or give workbook");
            Array.Sort(books, delegate(string x, string y) { return File.GetLastWriteTimeUtc(y).CompareTo(File.GetLastWriteTimeUtc(x)); });
            book = books[0];
        }
        if (!File.Exists(book)) throw new ToolError("no such workbook: " + book);
        string snapFile;
        List<ExcelTables.Sheet> sheets = Excel().Read(book, out snapFile);
        if (snapFile == null || !File.Exists(snapFile)) throw new ToolError("the workbook's snapshot is missing (" + snapFile + "): export again");
        Dictionary<string, object> snap = Json.Parse(File.ReadAllText(snapFile)) as Dictionary<string, object>;
        Dictionary<string, Dictionary<string, object>> snapTables = new Dictionary<string, Dictionary<string, object>>();
        foreach (object o in (object[])snap["tables"]) { Dictionary<string, object> d = (Dictionary<string, object>)o; snapTables[Json.Str(d, "table")] = d; }

        // workbook -> edited full tables, against the snapshot
        List<SapTables.Table> edited = new List<SapTables.Table>(), before = new List<SapTables.Table>();
        List<object> report = new List<object>();
        List<object> changes = new List<object>();
        foreach (ExcelTables.Sheet s in sheets)
        {
            Dictionary<string, object> st;
            if (!snapTables.TryGetValue(s.Table, out st)) throw new ToolError("sheet '" + s.Name + "': table " + s.Table + " is not in the snapshot");
            string[] fields = Json.Strs(st, "fields");
            List<string[]> rows = new List<string[]>();
            foreach (object ro in (object[])st["rows"]) rows.Add(Array.ConvertAll((object[])ro, delegate(object x) { return Convert.ToString(x, Json.Inv); }));
            HashSet<int> exportedIds = new HashSet<int>();
            foreach (object x in (object[])st["exported"]) exportedIds.Add(Convert.ToInt32(x, Json.Inv));
            int[] map = new int[s.Fields.Length];
            for (int c = 0; c < s.Fields.Length; c++)
            {
                map[c] = Array.IndexOf(fields, s.Fields[c]);
                if (map[c] < 0) throw new ToolError("sheet '" + s.Name + "': column '" + s.Fields[c] + "' is not a field of " + s.Table);
            }
            foreach (string f in fields) if (Array.IndexOf(s.Fields, f) < 0) throw new ToolError("sheet '" + s.Name + "': column '" + f + "' was removed; keep every column");
            SapTables.Table snapT = new SapTables.Table(); snapT.Key = s.Table; snapT.Fields = fields; snapT.Rows = rows;
            List<string[]> next = new List<string[]>();
            for (int i = 0; i < rows.Count; i++) next.Add((string[])rows[i].Clone());
            bool[] seen = new bool[rows.Count];
            List<string[]> added = new List<string[]>();
            int changed = 0;
            for (int k = 0; k < s.Rows.Count; k++)
            {
                string[] row = new string[fields.Length];
                for (int c = 0; c < s.Fields.Length; c++) row[map[c]] = s.Rows[k][c];
                for (int c = 0; c < row.Length; c++) if (row[c] == null) row[c] = "";
                int id = s.RowIds[k];
                if (id >= 0 && id < rows.Count && exportedIds.Contains(id) && !seen[id])
                {
                    seen[id] = true;
                    if (!SameRow(fields, rows[id], fields, row)) { changed++; changes.Add(Json.O("table", s.Table, "before", RowObj(fields, rows[id]), "after", RowObj(fields, row))); next[id] = row; }
                }
                else { added.Add(row); changes.Add(Json.O("table", s.Table, "before", null, "after", RowObj(fields, row))); }
            }
            List<int> deleted = new List<int>();
            foreach (int id in exportedIds) if (id < rows.Count && !seen[id]) { deleted.Add(id); changes.Add(Json.O("table", s.Table, "before", RowObj(fields, rows[id]), "after", null)); }
            deleted.Sort(); for (int i = deleted.Count - 1; i >= 0; i--) next.RemoveAt(deleted[i]);
            next.AddRange(added);
            report.Add(Json.O("table", s.Table, "sheet", s.Name, "changed", changed, "added", added.Count, "deleted", deleted.Count, "rowsAfter", next.Count));
            if (changed + added.Count + deleted.Count == 0) continue;
            SapTables.Table t = new SapTables.Table(); t.Key = s.Table; t.Fields = fields; t.Rows = next;
            edited.Add(t); before.Add(snapT);
        }
        Dictionary<string, object> res = Json.O("workbook", book, "tables", report);
        if (edited.Count == 0) { res["note"] = "the workbook matches the export: nothing to import"; res["changes"] = changes; return res; }

        Func<SapRead, List<object>> drift = delegate(SapRead r)
        {
            List<object> d = new List<object>();
            SapTables tt = r.Tables();
            for (int i = 0; i < before.Count; i++)
            {
                SapTables.Table now = tt.Read(before[i].Key, "");
                bool same = now.Rows.Count == before[i].Rows.Count;
                for (int k = 0; same && k < now.Rows.Count; k++) same = SameRow(now.Fields, now.Rows[k], before[i].Fields, before[i].Rows[k]);
                if (!same) d.Add(Json.O("table", before[i].Key, "rowsAtExport", before[i].Rows.Count, "rowsNow", now.Rows.Count));
            }
            return d;
        };
        if (dry)
        {
            List<object> dr = ctx.Sap.Read("import_tables", delegate(SapRead r) { return drift(r); });
            res["dryRun"] = true;
            res["changes"] = changes.Count > 100 ? changes.GetRange(0, 100) : changes;
            if (dr.Count > 0) { res["modelChangedSinceExport"] = dr; res["warning"] = "these tables changed in the model since the export; importing replaces them with the workbook (force: true)"; }
            res["note"] = "dry run: nothing changed. Show the changes to the user; import_tables with dry_run false applies them (into a checkpoint, logged).";
            return res;
        }
        Dictionary<string, object> applied = ctx.Sap.Edit("import_tables", a, false, delegate(SapRead r, SapEdit e)
        {
            List<object> dr = drift(r);
            if (dr.Count > 0 && !force) throw new ToolError("tables changed in the model since the export (" + Json.Write(dr) + "): export again, or force: true to replace them with the workbook");
            Dictionary<string, object> imp;
            try { imp = e.Tables().Apply(edited); }
            catch (ArgumentException x) { throw new ToolError(x.Message); }
            // read back: rows SAP2000 did not take as sent
            List<object> notTaken = new List<object>();
            SapTables tt = r.Tables();
            foreach (SapTables.Table t in edited)
            {
                SapTables.Table now = tt.Read(t.Key, "");
                int miss = 0;
                for (int k = 0; k < t.Rows.Count; k++) if (k >= now.Rows.Count || !SameRow(t.Fields, t.Rows[k], now.Fields, now.Rows[k], true)) miss++;
                if (miss > 0 || now.Rows.Count != t.Rows.Count) notTaken.Add(Json.O("table", t.Key, "rowsSent", t.Rows.Count, "rowsNow", now.Rows.Count, "rowsDiffering", miss));
            }
            Dictionary<string, object> out1 = Json.O("edit", "import " + edited.Count + " table(s) from " + Path.GetFileName(book), "import", imp, "changes", changes);
            if (notTaken.Count > 0) out1["notAsSent"] = notTaken;
            return out1;
        }, NewSessionDir);
        applied["tables"] = report;
        applied["workbook"] = book;
        return applied;
    }

    // Rows equal field by field (by name); numbers compare as numbers ("0.250" = "0.25"). sentOnly: a's empty fields
    // are not compared (SAP2000 fills them in on import, e.g. a new row's GUID).
    static bool SameRow(string[] fa, string[] a, string[] fb, string[] b) { return SameRow(fa, a, fb, b, false); }

    static bool SameRow(string[] fa, string[] a, string[] fb, string[] b, bool sentOnly)
    {
        for (int i = 0; i < fa.Length; i++)
        {
            int j = Array.IndexOf(fb, fa[i]);
            string x = i < a.Length ? a[i] ?? "" : "", y = j >= 0 && j < b.Length ? b[j] ?? "" : "";
            if (x == y || (sentOnly && x.Length == 0)) continue;
            double dx, dy;
            if (double.TryParse(x, System.Globalization.NumberStyles.Float, Json.Inv, out dx) && double.TryParse(y, System.Globalization.NumberStyles.Float, Json.Inv, out dy)
                && Math.Abs(dx - dy) <= 1e-12 * Math.Max(1, Math.Max(Math.Abs(dx), Math.Abs(dy)))) continue;
            return false;
        }
        return true;
    }

    static Dictionary<string, object> RowObj(string[] fields, string[] row)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        for (int i = 0; i < fields.Length; i++) d[fields[i]] = i < row.Length ? row[i] : "";
        return d;
    }

    // ---------------------------------------------------------------- phase 4 handlers

    ViewerHost Viewer() { return ctx.Viewer(true); }

    object ViewerOpen(Dictionary<string, object> a)
    {
        ViewerHost v = Viewer();
        bool opened = false;
        if (!v.Connected && Json.Bool(a, "open_browser", true))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(v.Url) { UseShellExecute = true });
            opened = true;
            for (int i = 0; i < 150 && !v.Connected; i++) System.Threading.Thread.Sleep(100);
        }
        Dictionary<string, object> r = Json.O("url", v.Url, "connected", v.Connected, "openedBrowser", opened);
        if (v.Connected) r["state"] = v.Command("state", null, TimeSpan.FromSeconds(20));
        else r["note"] = "open " + v.Url + " in Chrome or Edge; the viewer connects by itself";
        return r;
    }

    object ViewerLoad(Dictionary<string, object> a)
    {
        ViewerHost v = Viewer();
        if (!v.Connected) throw new ToolError("no viewer is connected: viewer_open first");
        string bin = Json.Str(a, "bin"), up = Json.Str(a, "up");
        if (up != null && up != "z" && up != "y") throw new ToolError("up must be z or y");
        Dictionary<string, object> exported = null;
        if (bin == null)
        {
            if (up == null) up = "z";   // the current checkpoint is a SAP2000 model
            string model = ctx.Sap.Read("viewer_load", delegate(SapRead r) { return r.ModelPath(); });
            string dir = SapDriver.SessionDirOf(model) ?? Path.Combine(ctx.WorkDir, "viewer");
            bin = Path.Combine(dir, "viewer", Safe(Path.GetFileNameWithoutExtension(model)) + ".bin");
            if (Json.Bool(a, "export", false) || !File.Exists(bin))
            {
                exported = (Dictionary<string, object>)ExportViewer(new Dictionary<string, object>());
                bin = (string)exported["bin"];
            }
        }
        if (!File.Exists(bin)) throw new ToolError("no such file: " + bin);
        string baseName = bin.Substring(0, bin.Length - 4), url = v.WorkUrl(bin);
        if (url == null) throw new ToolError("the viewer serves only files under the work dir " + ctx.WorkDir);
        string feat = File.Exists(baseName + ".features.json") ? v.WorkUrl(baseName + ".features.json") : null;
        object state = v.Command("load", Json.O("bin", url, "features", feat, "name", Path.GetFileNameWithoutExtension(bin), "up", up), TimeSpan.FromMinutes(10));
        v.LoadedBase = baseName;
        labels = null;
        Dictionary<string, object> res = Json.O("loaded", bin, "features", feat != null, "state", state);
        if (exported != null) res["exported"] = Json.O("seconds", exported["seconds"], "results", exported["results"]);
        return res;
    }

    // ---- SAP2000 names <-> the export's mesh ids (<base>.labels.csv: kind,id,element,object)

    sealed class Labels
    {
        public string Base;
        public readonly Dictionary<string, List<int>> ByName = new Dictionary<string, List<int>>();   // "kind|SAP name" -> mesh ids
        public readonly Dictionary<string, string> ById = new Dictionary<string, string>();           // "kind|mesh id" -> SAP name
    }
    Labels labels;

    Labels CurrentLabels()
    {
        ViewerHost v = Viewer();
        if (v.LoadedBase == null) throw new ToolError("no model was loaded through viewer_load in this server session");
        if (labels != null && labels.Base == v.LoadedBase) return labels;
        string file = v.LoadedBase + ".labels.csv";
        if (!File.Exists(file)) throw new ToolError("no labels next to the loaded model: " + file);
        Labels l = new Labels(); l.Base = v.LoadedBase;
        foreach (string line in File.ReadAllLines(file))
        {
            string[] f = Csv(line);
            if (f.Length < 4 || f[0] == "kind") continue;
            string sap = f[0] == "joint" ? f[2] : f[3];   // joints: the joint; meshed objects: the object
            if (string.IsNullOrEmpty(sap)) continue;
            int id;
            if (!int.TryParse(f[1], out id)) continue;
            List<int> ids;
            string key = f[0] + "|" + sap;
            if (!l.ByName.TryGetValue(key, out ids)) { ids = new List<int>(); l.ByName[key] = ids; }
            ids.Add(id);
            l.ById[f[0] + "|" + f[1]] = sap;
        }
        labels = l;
        return l;
    }

    static string[] Csv(string line)
    {
        List<string> r = new List<string>();
        StringBuilder sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q) { if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (c == '"') q = false; else sb.Append(c); }
            else if (c == '"') q = true;
            else if (c == ',') { r.Add(sb.ToString()); sb.Length = 0; }
            else sb.Append(c);
        }
        r.Add(sb.ToString());
        return r.ToArray();
    }

    // SAP2000 objects named by group / selection / names (kind) -> mesh ids per viewer domain.
    Dictionary<string, List<int>> MeshTargets(Dictionary<string, object> a)
    {
        string group = Json.Str(a, "group"), selection = Json.Str(a, "selection"), kind = Kind(a, "kind", false);
        string[] names = Json.Strs(a, "names");
        if (group == null && selection == null && names == null) throw new ToolError("say what: group, selection or names (with kind)");
        if (names != null && kind == null) throw new ToolError("kind is required with names");
        Dictionary<string, object> saved = selection == null ? null : LoadSelection(selection);
        Dictionary<string, List<string>> sap = ctx.Sap.Read("viewer targets", delegate(SapRead r)
        {
            Dictionary<string, List<string>> d = new Dictionary<string, List<string>>();
            foreach (string k in Kinds)
            {
                List<string> l = new List<string>();
                if (group != null) { if (!r.GroupExists(group)) throw new ToolError("no group '" + group + "'"); l.AddRange(r.GroupMembers(group, k)); }
                if (saved != null) l.AddRange(SelectionNames(saved, k));
                if (names != null && k == kind) l.AddRange(names);
                d[k] = l;
            }
            return d;
        });
        Labels lb = CurrentLabels();
        Dictionary<string, List<int>> m = new Dictionary<string, List<int>>();
        m["nodes"] = new List<int>(); m["shells"] = new List<int>(); m["beams"] = new List<int>();
        int missing = 0;
        foreach (KeyValuePair<string, List<string>> kv in sap)
        {
            string dom = kv.Key == "joint" ? "nodes" : kv.Key == "area" ? "shells" : kv.Key == "frame" ? "beams" : null;
            foreach (string n in kv.Value)
            {
                List<int> ids;
                if (dom != null && lb.ByName.TryGetValue(kv.Key + "|" + n, out ids)) m[dom].AddRange(ids);
                else missing++;
            }
        }
        if (missing > 0) m["missing"] = new List<int>(new[] { missing });
        return m;
    }

    object ViewerHighlight(Dictionary<string, object> a)
    {
        ViewerHost v = Viewer();
        if (Json.Bool(a, "clear", false)) return v.Command("unhighlight", null, TimeSpan.FromSeconds(60));
        Dictionary<string, List<int>> m = MeshTargets(a);
        List<object> members = new List<object>();
        if (m["shells"].Count > 0) members.Add(Json.O("domain", "shells", "ids", m["shells"]));
        if (m["beams"].Count > 0) members.Add(Json.O("domain", "beams", "ids", m["beams"]));
        if (m["nodes"].Count > 0 && m["shells"].Count == 0 && m["beams"].Count == 0) members.Add(Json.O("nodeIds", m["nodes"]));
        if (members.Count == 0) throw new ToolError("none of these objects is in the viewer's mesh");
        string label = Json.Str(a, "label") ?? Json.Str(a, "group") ?? Json.Str(a, "selection") ?? "highlight";
        object item = Json.O("id", Guid.NewGuid().ToString("N"), "name", label, "color", Json.Str(a, "color") ?? "#ff3030", "members", members);
        object st = v.Command("highlight", Json.O("items", new object[] { item }, "only", Json.Bool(a, "only", true)), TimeSpan.FromSeconds(60));
        Dictionary<string, object> r = Json.O("highlighted", Json.O("shells", m["shells"].Count, "beams", m["beams"].Count, "nodes", m["nodes"].Count), "state", st);
        if (m.ContainsKey("missing")) r["notInMesh"] = m["missing"][0];
        return r;
    }

    object ViewerIsolate(Dictionary<string, object> a)
    {
        ViewerHost v = Viewer();
        if (Json.Bool(a, "clear", false)) return v.Command("isolate", Json.O("shells", null, "beams", null), TimeSpan.FromSeconds(60));
        Dictionary<string, List<int>> m = MeshTargets(a);
        if (m["shells"].Count == 0 && m["beams"].Count == 0) throw new ToolError("none of these objects is a shell or beam of the viewer's mesh");
        return v.Command("isolate", Json.O("shells", m["shells"], "beams", m["beams"]), TimeSpan.FromSeconds(60));
    }

    object ViewerFocus(Dictionary<string, object> a)
    {
        ViewerHost v = Viewer();
        string kind = Kind(a, "kind", true), name = Json.Str(a, "name");
        if (string.IsNullOrEmpty(name)) throw new ToolError("name is required");
        Labels lb = CurrentLabels();
        string joint = name;
        if (kind == "area")
        {
            List<int> ids;
            if (!lb.ByName.TryGetValue("area|" + name, out ids)) throw new ToolError("area '" + name + "' is not in the viewer's mesh");
            return v.Command("focus", Json.O("kind", "elem", "id", ids[0]), TimeSpan.FromSeconds(30));
        }
        if (kind == "frame" || kind == "link")
            joint = ctx.Sap.Read("viewer_focus", delegate(SapRead r)
            {
                string[] p = r.Objects().PointsOf(kind, name);
                if (p.Length == 0) throw new ToolError("no " + kind + " '" + name + "'");
                return p[0];
            });
        List<int> nid;
        if (!lb.ByName.TryGetValue("joint|" + joint, out nid)) throw new ToolError("joint '" + joint + "' is not in the viewer's mesh");
        return v.Command("focus", Json.O("kind", "node", "id", nid[0]), TimeSpan.FromSeconds(30));
    }

    object ViewerSelection(Dictionary<string, object> a)
    {
        ViewerHost v = Viewer();
        Dictionary<string, object> st = v.Command("state", null, TimeSpan.FromSeconds(20)) as Dictionary<string, object>;
        Dictionary<string, object> r = Json.O("state", st);
        Dictionary<string, object> ro = st == null ? null : Json.Obj(st, "readout");
        if (ro != null && v.LoadedBase != null)
        {
            Labels lb = CurrentLabels();
            string el = Json.Str(ro, "element") ?? "", nd = Json.Str(ro, "node") ?? "";
            System.Text.RegularExpressions.Match me = System.Text.RegularExpressions.Regex.Match(el, @"^\s*(\d+)");
            string sap;
            if (me.Success)
            {
                string kind = el.Contains("beam idx") ? "frame" : "area";
                if (lb.ById.TryGetValue(kind + "|" + me.Groups[1].Value, out sap)) r["sapObject"] = Json.O("kind", kind, "name", sap);
            }
            System.Text.RegularExpressions.Match mn = System.Text.RegularExpressions.Regex.Match(nd, @"^\s*(\d+)");
            if (mn.Success && lb.ById.TryGetValue("joint|" + mn.Groups[1].Value, out sap)) r["sapJoint"] = sap;
        }
        if (st != null && !Json.Bool(st, "pinned", false)) r["note"] = "nothing pinned: the user clicks a point in the viewer to pin it";
        return r;
    }

    // After run_analysis: re-export and reload when a viewer is connected (update_viewer, default true).
    void UpdateViewerAfterRun(Dictionary<string, object> a, Dictionary<string, object> run)
    {
        ViewerHost v = ctx.Viewer(false);
        if (v == null || !v.Connected || !Json.Bool(a, "update_viewer", true)) return;
        try
        {
            Dictionary<string, object> load = (Dictionary<string, object>)ViewerLoad(Json.O("export", true));
            run["viewer"] = Json.O("reloaded", load["loaded"]);
        }
        catch (Exception e) { run["viewerError"] = e.Message; }
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
        double? zMin = kind == "joint" ? Json.Num(a, "z_min") : null, zMax = kind == "joint" ? Json.Num(a, "z_max") : null;
        if (names == null && group == null && saved == null && zMin == null && zMax == null)
            throw new ToolError("say which " + kind + "s: names, group or selection" + (kind == "joint" ? " (or z_min / z_max)" : ""));
        SapRead.Index x = r.Objects();
        List<string> all = x.Names(kind);
        HashSet<string> set = null;
        if (zMin != null || zMax != null)
        {
            double tol = 1e-6 * r.LargestExtent(), lo = (zMin ?? double.MinValue) - tol, hi = (zMax ?? double.MaxValue) + tol;
            set = new HashSet<string>(all.FindAll(delegate(string n) { double z = x.Xyz[n][2]; return z >= lo && z <= hi; }));
        }
        if (names != null)
        {
            HashSet<string> known = new HashSet<string>(all);
            foreach (string n in names) if (!known.Contains(n)) throw new ToolError("no " + kind + " '" + n + "'");
            HashSet<string> nm = new HashSet<string>(names);
            if (set == null) set = nm; else set.IntersectWith(nm);
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
            string why = Json.Str(e, "reason");
            l.Add(Json.Str(e, "tool") + ": " + (Json.Str(e, "edit") ?? "") + " (" + Convert.ToString(e.ContainsKey("changed") ? e["changed"] : 0, Json.Inv) + ")" +
                (why == null ? "" : " because " + why));
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

    // ---------------------------------------------------------------- design record handlers

    static readonly string[] NoteKinds = { "assumption", "condition", "decision", "question" };
    static readonly string[] NoteTopics = { "geometry", "supports", "materials", "sections", "loads", "analysis", "results" };

    static string OneOf(Dictionary<string, object> a, string key, string[] values, bool required)
    {
        string v = Json.Str(a, key);
        if (v == null) { if (required) throw new ToolError(key + " is required: " + string.Join(", ", values)); return null; }
        v = v.Trim().ToLowerInvariant();
        if (Array.IndexOf(values, v) < 0) throw new ToolError(key + " must be one of " + string.Join(", ", values) + " (got '" + Json.Str(a, key) + "')");
        return v;
    }

    object LogNote(Dictionary<string, object> a)
    {
        string kind = OneOf(a, "kind", NoteKinds, true), topic = OneOf(a, "topic", NoteTopics, false);
        string text = (Json.Str(a, "text") ?? "").Trim();
        if (text.Length == 0) throw new ToolError("text is required");
        string dir = SessionDirFor(a), sup = Json.Str(a, "supersedes");
        List<Dictionary<string, object>> notes = NoteLines(dir);
        if (sup != null)
        {
            if (notes.Find(delegate(Dictionary<string, object> n) { return Json.Str(n, "id") == sup; }) == null)
                throw new ToolError("no note " + sup + " in this session (list_notes)");
            Dictionary<string, object> by = notes.Find(delegate(Dictionary<string, object> n) { return Json.Str(n, "supersedes") == sup; });
            if (by != null) throw new ToolError(sup + " is already superseded by " + Json.Str(by, "id"));
        }
        Dictionary<string, object> note = Json.O("id", "n" + (notes.Count + 1).ToString("000", Json.Inv),
            "utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Json.Inv), "checkpoint", HeldCheckpoint(dir),
            "kind", kind, "topic", topic, "text", text, "basis", Json.Str(a, "basis"), "elements", Json.Strs(a, "elements"),
            "group", Json.Str(a, "group"), "names", Json.Strs(a, "names"), "supersedes", sup);
        foreach (string k in new List<string>(note.Keys)) if (note[k] == null) note.Remove(k);
        string file = Path.Combine(dir, "notes.jsonl");
        File.AppendAllText(file, Json.Write(note) + "\n");
        return Json.O("logged", note, "file", file, "notes", notes.Count + 1);
    }

    object ListNotes(Dictionary<string, object> a)
    {
        string kind = OneOf(a, "kind", NoteKinds, false), topic = OneOf(a, "topic", NoteTopics, false);
        bool superseded = Json.Bool(a, "superseded", false);
        string dir = SessionDirFor(a);
        List<Dictionary<string, object>> notes = NoteLines(dir);
        Dictionary<string, string> replacedBy = new Dictionary<string, string>();
        foreach (Dictionary<string, object> n in notes)
        {
            string s = Json.Str(n, "supersedes");
            if (s != null) replacedBy[s] = Json.Str(n, "id");
        }
        List<object> list = new List<object>();
        foreach (Dictionary<string, object> n in notes)
        {
            string by;
            if (replacedBy.TryGetValue(Json.Str(n, "id") ?? "", out by)) { if (!superseded) continue; n["supersededBy"] = by; }
            if (kind != null && Json.Str(n, "kind") != kind) continue;
            if (topic != null && Json.Str(n, "topic") != topic) continue;
            list.Add(n);
        }
        return Json.O("sessionDir", dir, "file", Path.Combine(dir, "notes.jsonl"), "total", notes.Count, "superseded", replacedBy.Count, "notes", list);
    }

    object WriteDesignRecord(Dictionary<string, object> a)
    {
        string notesDir = Json.Str(a, "notes_dir") ?? Environment.GetEnvironmentVariable("PLUTO_NOTES_DIR");
        if (string.IsNullOrEmpty(notesDir)) throw new ToolError("no notes dir: pass notes_dir or set PLUTO_NOTES_DIR (the Obsidian vault folder for design records)");
        string dir = SessionDirFor(a);
        // the basis of the checkpoint SAP2000 holds (saved as basis-rNNN.json), else the newest one saved
        Dictionary<string, object> basis = null;
        string from = null, held = HeldCheckpoint(dir);
        if (held != null)
        {
            string text = Json.Write(ctx.Sap.Read("describe_basis", delegate(SapRead r) { return r.Basis(SapRead.DefaultLimit); }));
            File.WriteAllText(Path.Combine(dir, "basis-" + held + ".json"), text);
            basis = Json.Parse(text) as Dictionary<string, object>;
            from = held + ", read from SAP2000 now";
        }
        else
        {
            List<string> saved = new List<string>(Directory.GetFiles(dir, "basis-r*.json"));
            saved.Sort(StringComparer.OrdinalIgnoreCase);
            if (saved.Count > 0)
            {
                string f = saved[saved.Count - 1];
                basis = Json.Parse(File.ReadAllText(f)) as Dictionary<string, object>;
                from = Path.GetFileNameWithoutExtension(f).Substring(6) + ", saved " + File.GetLastWriteTimeUtc(f).ToString("yyyy-MM-dd HH:mm", Json.Inv) + " UTC";
            }
        }
        DesignRecord.Args ra = new DesignRecord.Args();
        ra.Title = Json.Str(a, "title"); ra.Purpose = Json.Str(a, "purpose"); ra.Author = Json.Str(a, "author"); ra.Elements = Json.Strs(a, "elements");
        return DesignRecord.Write(dir, notesDir, basis, from, Path.Combine(ctx.WorkDir, "fixtures.json"), ra);
    }

    object DraftBodElement(Dictionary<string, object> a)
    {
        string element = (Json.Str(a, "element") ?? "").Trim();
        if (element.Length == 0) throw new ToolError("element is required");
        string notesDir = Json.Str(a, "notes_dir") ?? Environment.GetEnvironmentVariable("PLUTO_NOTES_DIR");
        if (string.IsNullOrEmpty(notesDir)) throw new ToolError("no notes dir: pass notes_dir or set PLUTO_NOTES_DIR (the Obsidian vault folder)");
        string group = Json.Str(a, "group");
        string[] read = ctx.Sap.Read("draft_bod_element", delegate(SapRead r)
        {
            string g;
            try { g = group == null ? null : Json.Write(r.GroupSummary(group)); }
            catch (ArgumentException e) { throw new ToolError(e.Message); }
            return new[] { Json.Write(r.Basis(SapRead.DefaultLimit)), g, r.ModelPath() };
        });
        string dir = Json.Str(a, "session_dir");
        if (dir != null) { if (!File.Exists(Path.Combine(dir, "session.json"))) throw new ToolError("no Pluto session in " + dir); }
        else dir = SapDriver.SessionDirOf(read[2]);
        return DesignRecord.DraftElement(element, notesDir, Json.Parse(read[0]) as Dictionary<string, object>,
            read[1] == null ? null : Json.Parse(read[1]) as Dictionary<string, object>, dir);
    }

    static List<Dictionary<string, object>> NoteLines(string dir)
    {
        List<Dictionary<string, object>> r = new List<Dictionary<string, object>>();
        string f = Path.Combine(dir, "notes.jsonl");
        if (!File.Exists(f)) return r;
        foreach (string l in File.ReadAllLines(f))
        {
            if (l.Trim().Length == 0) continue;
            Dictionary<string, object> d = Json.Parse(l) as Dictionary<string, object>;
            if (d != null) r.Add(d);
        }
        return r;
    }

    // The checkpoint (rNNN) SAP2000 holds when it is in this session, else null.
    string HeldCheckpoint(string dir)
    {
        try
        {
            string model = ctx.Sap.Read("session", delegate(SapRead r) { return r.ModelPath(); });
            if (!string.Equals(SapDriver.SessionDirOf(model), dir, StringComparison.OrdinalIgnoreCase)) return null;
            string n = Path.GetFileNameWithoutExtension(model);
            return System.Text.RegularExpressions.Regex.IsMatch(n, @"^r\d+$") ? n : null;
        }
        catch (ToolError) { return null; }
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
