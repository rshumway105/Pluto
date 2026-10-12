// Pluto MCP server plumbing: JSON, log, the STA worker every COM call runs on, build info, server context.
// C# 5 (see McpServer.cs).
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

// ---------------------------------------------------------------- JSON

public static class Json
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static JavaScriptSerializer S()
    {
        JavaScriptSerializer s = new JavaScriptSerializer();
        s.MaxJsonLength = int.MaxValue;
        s.RecursionLimit = 256;
        return s;
    }

    // Objects come back as Dictionary<string, object>, arrays as object[].
    public static object Parse(string text) { return S().DeserializeObject(text); }

    // NaN / infinity are not JSON: written as null.
    public static string Write(object o) { return S().Serialize(Clean(o)); }

    static object Clean(object o)
    {
        if (o is double) { double d = (double)o; return double.IsNaN(d) || double.IsInfinity(d) ? null : o; }
        if (o is float) { float f = (float)o; return float.IsNaN(f) || float.IsInfinity(f) ? null : o; }
        IDictionary<string, object> map = o as IDictionary<string, object>;
        if (map != null)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> kv in map) r[kv.Key] = Clean(kv.Value);
            return r;
        }
        if (o is string) return o;
        IEnumerable seq = o as IEnumerable;
        if (seq != null && !(o is IDictionary))
        {
            List<object> r = new List<object>();
            foreach (object x in seq) r.Add(Clean(x));
            return r;
        }
        return o;
    }

    // Ordered builder: O("a", 1, "b", 2).
    public static Dictionary<string, object> O(params object[] kv)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }

    public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
    }

    public static string Str(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, Inv) : null;
    }

    public static bool Bool(Dictionary<string, object> d, string key, bool fallback)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
    }
}

// ---------------------------------------------------------------- log (stderr + file; never stdout)

public sealed class Log
{
    readonly string path;
    readonly object gate = new object();

    public Log(string path) { this.path = path; }

    public void Info(string text) { Write("info ", text); }
    public void Error(string what, Exception e) { Write("error", what + ": " + e); }

    void Write(string level, string text)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Json.Inv) + " " + level + " " + text;
        lock (gate)
        {
            Console.Error.WriteLine(line);
            try { File.AppendAllText(path, line + Environment.NewLine); } catch (IOException) { }
        }
    }
}

// ---------------------------------------------------------------- STA worker

// One thread, single-threaded apartment, owns every COM object of one driver: calls run on it in order.
// A call that outlives its timeout keeps running (COM cannot be interrupted); later calls queue behind it.
public sealed class StaWorker : IDisposable
{
    readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
    readonly Thread thread;
    volatile string busyWith;

    public StaWorker(string name)
    {
        thread = new Thread(Loop);
        thread.Name = name;
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    void Loop() { foreach (Action a in queue.GetConsumingEnumerable()) a(); }

    public T Invoke<T>(string what, TimeSpan timeout, Func<T> fn)
    {
        string blocked = busyWith;
        T result = default(T);
        Exception error = null;
        ManualResetEvent done = new ManualResetEvent(false);
        queue.Add(delegate
        {
            busyWith = what;
            try { result = fn(); }
            catch (Exception e) { error = e; }
            finally { busyWith = null; done.Set(); }
        });
        if (!done.WaitOne(timeout))
            throw new TimeoutException(what + " did not finish within " + timeout.TotalSeconds + " s" +
                (blocked != null ? " (queued behind '" + blocked + "', still running)" : "") +
                ". The program may be showing a dialog or still working: check its window. The call stays queued.");
        if (error != null) throw new ToolError(error.Message, error);
        return result;
    }

    public void Dispose() { queue.CompleteAdding(); }
}

// An error the tool reports to the LLM as a failed call (isError), not a protocol error.
public class ToolError : Exception
{
    public ToolError(string message) : base(message) { }
    public ToolError(string message, Exception inner) : base(message, inner) { }
}

// ---------------------------------------------------------------- build info

// bin\build.json, written by Build-PlutoMcp.ps1: which SAP2000v1.dll the exe was compiled against.
// The server compares it with the newest install so an upgrade (e.g. SAP 27) is reported, and
// Start-PlutoMcp.ps1 rebuilds before starting.
public sealed class BuildInfo
{
    public string SapDir, SapDll, SapDllSha256, BuiltUtc;
    public string CurrentSapDir, CurrentSha256;

    public bool RebuildNeeded
    {
        get { return CurrentSha256 != null && !string.Equals(CurrentSha256, SapDllSha256, StringComparison.OrdinalIgnoreCase); }
    }

    public static BuildInfo Load(string exeDir)
    {
        BuildInfo b = new BuildInfo();
        string path = Path.Combine(exeDir, "build.json");
        if (File.Exists(path))
        {
            Dictionary<string, object> d = Json.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
            b.SapDir = Json.Str(d, "sapDir");
            b.SapDll = Json.Str(d, "sapDll");
            b.SapDllSha256 = Json.Str(d, "sapDllSha256");
            b.BuiltUtc = Json.Str(d, "builtUtc");
        }
        b.CurrentSapDir = FindSapDir();
        if (b.CurrentSapDir != null)
        {
            string dll = Path.Combine(b.CurrentSapDir, "SAP2000v1.dll");
            if (File.Exists(dll)) b.CurrentSha256 = Sha256(dll);
        }
        return b;
    }

    // Same lookup as scripts\sap\Import-SapApi.ps1: PLUTO_SAP_DIR, else the newest "SAP2000 NN".
    public static string FindSapDir()
    {
        string env = Environment.GetEnvironmentVariable("PLUTO_SAP_DIR");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        string root = @"C:\Program Files\Computers and Structures";
        if (!Directory.Exists(root)) return null;
        string best = null; int bestN = -1;
        foreach (string d in Directory.GetDirectories(root, "SAP2000 *"))
        {
            int n;
            if (int.TryParse(Path.GetFileName(d).Substring(8).Trim(), NumberStyles.Integer, Json.Inv, out n) && n > bestN
                && File.Exists(Path.Combine(d, "SAP2000v1.dll"))) { best = d; bestN = n; }
        }
        return best;
    }

    static string Sha256(string path)
    {
        using (SHA256 h = SHA256.Create())
        using (FileStream f = File.OpenRead(path))
            return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
    }

    public string Summary()
    {
        return "built against " + (SapDir ?? "?") + (RebuildNeeded ? "; REBUILD NEEDED (newest install " + CurrentSapDir + ")" : "");
    }

    public Dictionary<string, object> ToJson()
    {
        return Json.O("builtAgainst", SapDir, "builtUtc", BuiltUtc, "newestInstall", CurrentSapDir, "rebuildNeeded", RebuildNeeded);
    }
}

// ---------------------------------------------------------------- context

public sealed class ServerContext : IDisposable
{
    public string ExeDir, WorkDir;
    public Log Log;
    public BuildInfo Build;
    public SapDriver Sap;

    // Work dir: PLUTO_WORK, else %LOCALAPPDATA%\Pluto\work. Outside the repo: model data never lands in it.
    public static ServerContext Create(BuildInfo build)
    {
        ServerContext c = new ServerContext();
        c.ExeDir = AppDomain.CurrentDomain.BaseDirectory;
        c.Build = build;
        c.WorkDir = Environment.GetEnvironmentVariable("PLUTO_WORK");
        if (string.IsNullOrEmpty(c.WorkDir))
            c.WorkDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Pluto\work");
        Directory.CreateDirectory(c.WorkDir);
        c.Log = new Log(Path.Combine(c.WorkDir, "pluto-mcp.log"));
        string sapDir = c.Build.SapDir ?? c.Build.CurrentSapDir;
        c.Sap = new SapDriver(sapDir == null ? null : Path.Combine(sapDir, "SAP2000.exe"), c.Log);
        return c;
    }

    public void Dispose() { Sap.Dispose(); }
}
