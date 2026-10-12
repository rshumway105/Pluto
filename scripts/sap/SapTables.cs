// Input tables for editing through DatabaseTables (late-bound, like SapSession.TableRows): read a table with every
// field as SAP2000 writes it, and replace tables and import them (ApplyEditedTables) with the import log. Used by
// the Pluto MCP server's export_tables / import_tables (the Excel round trip). C# 5 (see SapSession.cs).
//
// Facts (SAP 26.3, 2026-10-12, probed in a separate instance on a scratch copy):
//  * The array, CSV-string and CSV-file setters all import ("3 of 3 records successfully read").
//  * A model with results refuses: Set / ApplyEditedTables return 1 with an empty log. Unlock it first.
//  * Once a SAP2000 has imported a .s2k (OpenFile on a text file), ApplyEditedTables fails with an empty log for
//    every model it opens later, and after that failure GetTableForDisplayArray returns no rows for any table, until
//    SAP2000 restarts. Saving as .sdb and reopening does not clear it; a .sdb opened in a fresh SAP2000 imports fine.
//    The MCP open_model imports .s2k in a second SAP2000 for this reason. Apply cancels the edit session after a
//    refusal and reports whether tables still read.
using System;
using System.Collections.Generic;
using System.Reflection;
using SAP2000v1;

public class SapTables
{
    readonly SapSession s;
    public SapTables(SapSession session) { s = session; }

    public sealed class Table
    {
        public string Key;
        public string[] Fields;
        public List<string[]> Rows = new List<string[]>();
    }

    object Db()
    {
        PropertyInfo p = typeof(cSapModel).GetProperty("DatabaseTables");
        object db = p == null ? null : p.GetValue(s.Model, null);
        if (db == null) throw new ArgumentException("this SAP2000 API has no DatabaseTables: tables cannot be edited");
        return db;
    }

    static MethodInfo Fn(string name)
    {
        Type it = typeof(cSapModel).Assembly.GetType("SAP2000v1.cDatabaseTables");
        MethodInfo m = it == null ? null : it.GetMethod(name);
        if (m == null) throw new ArgumentException("this SAP2000 API has no DatabaseTables." + name);
        return m;
    }

    public void Cancel() { Fn("CancelTableEditing").Invoke(Db(), new object[0]); }

    // Every row with every field, as text (group: only that group's objects). Leaves no edit session open.
    public Table Read(string key, string group)
    {
        object db = Db();
        Cancel();
        s.SelectAllForDisplay();
        object[] a = { key, group ?? "", 0, null, 0, null };
        int ret = (int)Fn("GetTableForEditingArray").Invoke(db, a);
        Cancel();
        if (ret != 0) throw new ArgumentException("table '" + key + "' cannot be read for editing (read_table lists the tables)");
        Table t = new Table();
        t.Key = key;
        t.Fields = (string[])a[3] ?? new string[0];
        string[] data = (string[])a[5] ?? new string[0];
        int nf = t.Fields.Length, n = (int)a[4];
        for (int r = 0; r < n && nf > 0; r++)
        {
            string[] row = new string[nf];
            for (int c = 0; c < nf; c++) row[c] = data[r * nf + c] ?? "";
            t.Rows.Add(row);
        }
        return t;
    }

    // Replaces each table's rows (its Fields, any subset of the table's fields) and imports them together. The model
    // must not hold results (unlock first). Returns import counts, the log, and whether tables still read.
    public Dictionary<string, object> Apply(IList<Table> tables)
    {
        if (s.Model.GetModelIsLocked()) throw new ArgumentException("the model holds results: unlock it before importing tables");
        object db = Db();
        Cancel();
        s.SelectAllForDisplay();
        Dictionary<string, object> r = new Dictionary<string, object>();
        List<object> sets = new List<object>();
        bool ok = true;
        foreach (Table t in tables)
        {
            object[] g = { t.Key, "", 0, null, 0, null };
            Fn("GetTableForEditingArray").Invoke(db, g);   // the table's current version
            int nf = t.Fields.Length;
            string[] flat = new string[t.Rows.Count * nf];
            for (int i = 0; i < t.Rows.Count; i++)
                for (int c = 0; c < nf; c++) flat[i * nf + c] = c < t.Rows[i].Length ? t.Rows[i][c] ?? "" : "";
            object[] a = { t.Key, g[2], t.Fields, t.Rows.Count, flat };
            int ret = (int)Fn("SetTableForEditingArray").Invoke(db, a);
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["table"] = t.Key; d["rows"] = t.Rows.Count; d["accepted"] = ret == 0;
            sets.Add(d);
            if (ret != 0) { ok = false; break; }
        }
        r["set"] = sets;
        if (ok)
        {
            object[] ap = { true, 0, 0, 0, 0, "" };
            int ret = (int)Fn("ApplyEditedTables").Invoke(db, ap);
            string log = (string)ap[5] ?? "";
            r["applied"] = ret == 0 && (int)ap[1] == 0;
            r["fatal"] = ap[1]; r["errors"] = ap[2]; r["warnings"] = ap[3]; r["info"] = ap[4];
            r["messages"] = Messages(log);
            r["log"] = log.Length > 6000 ? log.Substring(0, 6000) + " ..." : log;
            ok = ret == 0;
        }
        else r["applied"] = false;
        if (!ok) { try { Cancel(); } catch (Exception) { } }
        bool readable = TablesReadable();
        r["tablesReadable"] = readable;
        if (!readable) r["warning"] = "SAP2000 now reads no input tables. This happens when this SAP2000 imported a .s2k file earlier (File > Import, or opening a .s2k): save your work, restart SAP2000, open the .sdb, and import again";
        return r;
    }

    // The WARNING / ERROR lines of an import log (not the option and count lines), first 30.
    static List<string> Messages(string log)
    {
        List<string> m = new List<string>();
        foreach (string raw in log.Split('\n'))
        {
            string l = raw.Trim(), up = l.ToUpperInvariant();
            if (m.Count >= 30 || l.Length == 0 || up.StartsWith("NUMBER OF") || up.StartsWith("IMPORT CONTROL")) continue;
            if (up.Contains("WARNING") || up.Contains("ERROR")) m.Add(l);
        }
        return m;
    }

    // A model with joints must list them in "Joint Coordinates".
    public bool TablesReadable()
    {
        if (s.Model.PointObj.Count() == 0) return true;
        return s.TableRows("Joint Coordinates").Count > 0;
    }
}
