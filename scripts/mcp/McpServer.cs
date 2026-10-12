// Pluto MCP server: Model Context Protocol over stdio (newline-delimited JSON-RPC 2.0) in front of the
// model drivers, so an LLM client (Claude Code, Copilot agent mode) can read, run and query a model.
// C# 5, .NET Framework 4.8, built by Build-PlutoMcp.ps1 with the Framework's csc.exe (no SDK, no packages).
// Plan: vault/decisions/2026-10-12-llm-model-interface.
//
// stdout carries protocol messages only; Console.Out is pointed at stderr so nothing else can reach it.
// Requests are handled one at a time, in order (a tools/call blocks the loop until it returns).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class PlutoMcp
{
    public const string ServerVersion = "0.1.0";
    const string DefaultProtocol = "2025-06-18";

    static TextWriter proto;

    public static int Main(string[] args)
    {
        proto = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        proto.NewLine = "\n";
        Console.SetOut(Console.Error);

        BuildInfo build = BuildInfo.Load(AppDomain.CurrentDomain.BaseDirectory);
        sapDir = build.SapDir ?? build.CurrentSapDir;
        AppDomain.CurrentDomain.AssemblyResolve += FromSapDir;
        ServerContext ctx = ServerContext.Create(build);
        ToolSet tools = new ToolSet(ctx);
        ctx.Log.Info("start " + ServerVersion + "; work dir " + ctx.WorkDir + "; " + ctx.Build.Summary());
        using (StreamReader input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
        {
            string line;
            while ((line = input.ReadLine()) != null)
                if (line.Trim().Length > 0) Handle(line, tools, ctx.Log);
        }
        ctx.Log.Info("stdin closed: stop");
        ctx.Dispose();
        return 0;
    }

    // SAP2000v1.dll and its dependencies load from the install the exe was built against, not a copy
    // beside the exe: nothing stale to keep in step with SAP upgrades.
    static string sapDir;
    static System.Reflection.Assembly FromSapDir(object sender, ResolveEventArgs e)
    {
        if (sapDir == null) return null;
        string path = Path.Combine(sapDir, new System.Reflection.AssemblyName(e.Name).Name + ".dll");
        return File.Exists(path) ? System.Reflection.Assembly.LoadFrom(path) : null;
    }

    static void Handle(string line, ToolSet tools, Log log)
    {
        Dictionary<string, object> msg;
        try { msg = Json.Parse(line) as Dictionary<string, object>; }
        catch (Exception e) { Send(Error(null, -32700, "parse error: " + e.Message)); return; }
        if (msg == null) { Send(Error(null, -32600, "a message must be a JSON object")); return; }

        string method = Json.Str(msg, "method");
        bool isRequest = msg.ContainsKey("id");
        object id = isRequest ? msg["id"] : null;
        if (method == null) return;                                  // a response to us: none expected
        if (!isRequest) { log.Info("notification " + method); return; }  // initialized, cancelled

        Dictionary<string, object> p = Json.Obj(msg, "params");
        try
        {
            object result;
            switch (method)
            {
                case "initialize": result = Initialize(p); break;
                case "ping": result = new Dictionary<string, object>(); break;
                case "tools/list": result = tools.List(); break;
                case "tools/call": result = tools.Call(Json.Str(p, "name"), Json.Obj(p, "arguments")); break;
                default: Send(Error(id, -32601, "method not found: " + method)); return;
            }
            Send(Json.O("jsonrpc", "2.0", "id", id, "result", result));
        }
        catch (Exception e)
        {
            log.Error(method, e);
            Send(Error(id, -32603, e.Message));
        }
    }

    static object Initialize(Dictionary<string, object> p)
    {
        string version = Json.Str(p, "protocolVersion") ?? DefaultProtocol;
        return Json.O(
            "protocolVersion", version,
            "capabilities", Json.O("tools", Json.O("listChanged", false)),
            "serverInfo", Json.O("name", "pluto", "version", ServerVersion),
            "instructions",
                "Pluto drives structural analysis models through each program's API (SAP2000 via the CSI OAPI " +
                "today; ETABS planned). Call `status` first: it says whether the program is running, which model it " +
                "holds, the units every number is in, and which capabilities exist yet. Never assume a capability " +
                "that `status` does not list. Values are in the model's present units unless a tool says otherwise.");
    }

    static Dictionary<string, object> Error(object id, int code, string message)
    {
        return Json.O("jsonrpc", "2.0", "id", id, "error", Json.O("code", code, "message", message));
    }

    static readonly object sendLock = new object();
    static void Send(object message)
    {
        string text = Json.Write(message);
        lock (sendLock) { proto.WriteLine(text); proto.Flush(); }
    }
}
