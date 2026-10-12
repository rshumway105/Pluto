// Pluto MCP: the live viewer host (plan § 4, phase 4). An HttpListener on http://localhost:<port>/ serves the
// repo's viewer\ under /viewer/ and the work dir under /work/ (HTTP Range, so a large .bin is read in slices, never
// whole), and a WebSocket at /live that the viewer opens in live mode (index.html?live=1, scripts\live.js). Tools
// send commands { id, cmd, args } and wait for the reply { id, ok, result | error }. localhost only; nothing outside
// the two roots is served. Started on the first viewer tool. C# 5 (see McpServer.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

public sealed class ViewerHost : IDisposable
{
    readonly string viewerDir, workDir;
    readonly Log log;
    HttpListener listener;
    Thread acceptThread;
    public int Port { get; private set; }

    readonly object gate = new object();
    WebSocket socket;                    // the connected viewer (the latest one wins)
    readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
    readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
    int nextId;
    public Dictionary<string, object> LastHello;   // what the viewer said when it connected
    public string LoadedBase;                       // <base> of the .bin last loaded through viewer_load (labels: <base>.labels.csv)

    sealed class Pending { public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false); public Dictionary<string, object> Reply; }

    public ViewerHost(string viewerDir, string workDir, Log log) { this.viewerDir = viewerDir; this.workDir = workDir; this.log = log; }

    public string Url { get { return "http://localhost:" + Port + "/viewer/index.html?live=1"; } }

    public bool Connected { get { lock (gate) return socket != null && socket.State == WebSocketState.Open; } }

    // Listens on the first free port from `port` (20 tries).
    public void Start(int port)
    {
        if (listener != null) return;
        for (int p = port; p < port + 20; p++)
        {
            HttpListener l = new HttpListener();
            l.Prefixes.Add("http://localhost:" + p + "/");
            try { l.Start(); }
            catch (HttpListenerException) { l.Close(); continue; }
            listener = l; Port = p;
            break;
        }
        if (listener == null) throw new ToolError("no free port from " + port + " to " + (port + 19) + " for the viewer");
        acceptThread = new Thread(Accept) { IsBackground = true, Name = "viewer http" };
        acceptThread.Start();
        log.Info("viewer host on " + Url);
    }

    void Accept()
    {
        while (listener != null && listener.IsListening)
        {
            HttpListenerContext c;
            try { c = listener.GetContext(); }
            catch (Exception) { return; }   // stopped
            ThreadPool.QueueUserWorkItem(delegate { Serve(c); });
        }
    }

    void Serve(HttpListenerContext c)
    {
        try
        {
            string path = Uri.UnescapeDataString(c.Request.Url.AbsolutePath);
            if (path == "/live" && c.Request.IsWebSocketRequest) { Live(c); return; }
            if (path == "/" || path == "/viewer" || path == "/viewer/") { c.Response.Redirect(Url); c.Response.Close(); return; }
            string root = null, rel = null;
            if (path.StartsWith("/viewer/")) { root = viewerDir; rel = path.Substring(8); }
            else if (path.StartsWith("/work/")) { root = workDir; rel = path.Substring(6); }
            string file = root == null ? null : Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
            if (file == null || !file.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                c.Response.StatusCode = 404; c.Response.Close(); return;
            }
            SendFile(c, file);
        }
        catch (Exception e)
        {
            log.Error("viewer http", e);
            try { c.Response.StatusCode = 500; c.Response.Close(); } catch (Exception) { }
        }
    }

    static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { ".html", "text/html; charset=utf-8" }, { ".js", "text/javascript; charset=utf-8" }, { ".css", "text/css; charset=utf-8" },
        { ".json", "application/json" }, { ".bin", "application/octet-stream" }, { ".csv", "text/csv" }, { ".png", "image/png" }, { ".svg", "image/svg+xml" }
    };

    // The whole file, or one "bytes=a-b" range (206).
    static void SendFile(HttpListenerContext c, string file)
    {
        HttpListenerResponse r = c.Response;
        string type;
        r.ContentType = Types.TryGetValue(Path.GetExtension(file), out type) ? type : "application/octet-stream";
        r.Headers["Cache-Control"] = "no-store";
        r.Headers["Accept-Ranges"] = "bytes";
        using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            long len = fs.Length, start = 0, end = len - 1;
            string range = c.Request.Headers["Range"];
            if (range != null && range.StartsWith("bytes="))
            {
                string[] ab = range.Substring(6).Split(',')[0].Split('-');
                long a, b;
                if (ab[0].Length == 0 && long.TryParse(ab[1], out b)) { start = Math.Max(0, len - b); }
                else
                {
                    if (long.TryParse(ab[0], out a)) start = a;
                    if (ab.Length > 1 && long.TryParse(ab[1], out b)) end = Math.Min(b, len - 1);
                }
                if (start > end || start >= len) { r.StatusCode = 416; r.Headers["Content-Range"] = "bytes */" + len; r.Close(); return; }
                r.StatusCode = 206;
                r.Headers["Content-Range"] = "bytes " + start + "-" + end + "/" + len;
            }
            long n = end - start + 1;
            r.ContentLength64 = Math.Max(0, n);
            if (c.Request.HttpMethod == "HEAD") { r.Close(); return; }
            fs.Seek(start, SeekOrigin.Begin);
            byte[] buf = new byte[1 << 20];
            while (n > 0)
            {
                int k = fs.Read(buf, 0, (int)Math.Min(buf.Length, n));
                if (k <= 0) break;
                r.OutputStream.Write(buf, 0, k);
                n -= k;
            }
            r.Close();
        }
    }

    // ---------------------------------------------------------------- WebSocket

    void Live(HttpListenerContext c)
    {
        WebSocket ws = c.AcceptWebSocketAsync(null).Result.WebSocket;
        lock (gate) { if (socket != null && socket != ws) { try { socket.Abort(); } catch (Exception) { } } socket = ws; }
        log.Info("viewer connected");
        byte[] buf = new byte[1 << 16];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                MemoryStream msg = new MemoryStream();
                WebSocketReceiveResult rr;
                do
                {
                    rr = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                    if (rr.MessageType == WebSocketMessageType.Close) return;
                    msg.Write(buf, 0, rr.Count);
                } while (!rr.EndOfMessage);
                OnMessage(Encoding.UTF8.GetString(msg.ToArray()));
            }
        }
        catch (Exception e) { log.Info("viewer socket closed: " + e.GetBaseException().Message); }
        finally
        {
            lock (gate) if (socket == ws) socket = null;
            FailPending("the viewer disconnected");
        }
    }

    void OnMessage(string text)
    {
        Dictionary<string, object> m = Json.Parse(text) as Dictionary<string, object>;
        if (m == null) return;
        string id = Json.Str(m, "id");
        if (id == null)
        {
            if (Json.Str(m, "event") == "hello") LastHello = m;
            return;
        }
        Pending p;
        lock (gate) { if (!pending.TryGetValue(id, out p)) return; pending.Remove(id); }
        p.Reply = m;
        p.Done.Set();
    }

    void FailPending(string why)
    {
        List<Pending> all;
        lock (gate) { all = new List<Pending>(pending.Values); pending.Clear(); }
        foreach (Pending p in all) { p.Reply = Json.O("ok", false, "error", why); p.Done.Set(); }
    }

    // Sends a command to the connected viewer and waits for its reply; the viewer's error becomes a ToolError.
    public object Command(string cmd, Dictionary<string, object> args, TimeSpan timeout)
    {
        WebSocket ws;
        lock (gate) ws = socket;
        if (ws == null || ws.State != WebSocketState.Open)
            throw new ToolError("no viewer is connected: viewer_open, then open " + Url + " in a browser (or let viewer_open open it)");
        string id = Interlocked.Increment(ref nextId).ToString(Json.Inv);
        Pending p = new Pending();
        lock (gate) pending[id] = p;
        byte[] data = Encoding.UTF8.GetBytes(Json.Write(Json.O("id", id, "cmd", cmd, "args", args ?? new Dictionary<string, object>())));
        sendLock.Wait();
        try { ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None).Wait(); }
        finally { sendLock.Release(); }
        if (!p.Done.Wait(timeout))
        {
            lock (gate) pending.Remove(id);
            throw new ToolError("the viewer did not answer '" + cmd + "' within " + timeout.TotalSeconds + " s");
        }
        if (!Json.Bool(p.Reply, "ok", false)) throw new ToolError("viewer: " + (Json.Str(p.Reply, "error") ?? "failed"));
        object result;
        return p.Reply.TryGetValue("result", out result) ? result : null;
    }

    // A path under the work dir as its /work/ URL (null when outside).
    public string WorkUrl(string path)
    {
        string full = Path.GetFullPath(path), root = Path.GetFullPath(workDir).TrimEnd('\\') + "\\";
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        string[] parts = full.Substring(root.Length).Split('\\');
        for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
        return "/work/" + string.Join("/", parts);
    }

    public void Dispose()
    {
        HttpListener l = listener;
        listener = null;
        if (l != null) { try { l.Stop(); l.Close(); } catch (Exception) { } }
        lock (gate) { if (socket != null) { try { socket.Abort(); } catch (Exception) { } } socket = null; }
    }
}
