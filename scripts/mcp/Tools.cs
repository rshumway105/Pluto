// Pluto MCP: the tool table. Each tool: name, description (what the LLM reads to choose it), JSON Schema
// for its arguments, handler. Handler failures come back as isError results the LLM can act on.
// C# 5 (see McpServer.cs).
using System;
using System.Collections.Generic;
using System.IO;

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

    static Dictionary<string, object> Prop(string type, string description)
    {
        return Json.O("type", type, "description", description);
    }
}
