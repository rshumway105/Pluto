// Pluto MCP: SAP2000 input tables <-> an Excel workbook, for export_tables / import_tables. One sheet per table: row 1
// the field names, then the rows as text (number format "@", so SAP2000's text comes back exactly); column A ("_row",
// hidden) is each row's index in the exported table, empty for a row the user adds, so an import can tell changed,
// added and deleted rows apart. A very hidden sheet "_pluto" maps sheets to table keys and names the snapshot
// (<workbook>.pluto.json, the tables as exported) the import compares against. Excel is driven through COM on its own
// STA thread, invisible, and quit after each call; the user's Excel opens the saved file. C# 5 (see McpServer.cs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

public sealed class ExcelTables : IDisposable
{
    readonly StaWorker sta = new StaWorker("Excel");
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    public const string RowColumn = "_row", MetaSheet = "_pluto";

    public sealed class Sheet
    {
        public string Table, Name;
        public string[] Fields;
        public List<string[]> Rows = new List<string[]>();
        public List<int> RowIds = new List<int>();   // index in the exported table; -1 = added in Excel
    }

    static dynamic NewExcel()
    {
        Type t = Type.GetTypeFromProgID("Excel.Application");
        if (t == null) throw new ToolError("Excel is not installed (no Excel.Application)");
        dynamic xl = Activator.CreateInstance(t);
        xl.Visible = false; xl.DisplayAlerts = false; xl.ScreenUpdating = false;
        return xl;
    }

    static void Quit(dynamic xl)
    {
        try { xl.Quit(); } catch (Exception) { }
        try { Marshal.FinalReleaseComObject(xl); } catch (Exception) { }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    // Excel sheet names: 31 characters, none of []:*?/\ , unique.
    public static string SheetName(string table, HashSet<string> used)
    {
        System.Text.StringBuilder b = new System.Text.StringBuilder();
        foreach (char c in table) b.Append("[]:*?/\\".IndexOf(c) >= 0 ? ' ' : c);
        string n = b.ToString().Trim();
        if (n.Length > 28) n = n.Substring(0, 28).Trim();
        if (n.Length == 0 || n.Equals(MetaSheet, StringComparison.OrdinalIgnoreCase)) n = "Table";
        string u = n;
        for (int k = 2; used.Contains(u.ToLowerInvariant()); k++) u = (n.Length > 25 ? n.Substring(0, 25) : n) + " " + k;
        used.Add(u.ToLowerInvariant());
        return u;
    }

    public void Write(string path, IList<Sheet> sheets, string snapshotFile)
    {
        sta.Invoke("excel write", Timeout, delegate
        {
            dynamic xl = NewExcel();
            try
            {
                dynamic wb = xl.Workbooks.Add();
                for (int i = 0; i < sheets.Count; i++)
                {
                    Sheet s = sheets[i];
                    dynamic ws = i == 0 ? wb.Worksheets[1] : wb.Worksheets.Add(After: wb.Worksheets[wb.Worksheets.Count]);
                    ws.Name = s.Name;
                    int nc = s.Fields.Length + 1, nr = s.Rows.Count + 1;
                    object[,] v = new object[nr, nc];
                    v[0, 0] = RowColumn;
                    for (int c = 0; c < s.Fields.Length; c++) v[0, c + 1] = s.Fields[c];
                    for (int r = 0; r < s.Rows.Count; r++)
                    {
                        v[r + 1, 0] = s.RowIds[r].ToString(CultureInfo.InvariantCulture);
                        for (int c = 0; c < s.Fields.Length; c++) v[r + 1, c + 1] = c < s.Rows[r].Length ? s.Rows[r][c] : "";
                    }
                    dynamic rng = ws.Range(ws.Cells[1, 1], ws.Cells[nr, nc]);
                    rng.NumberFormat = "@";
                    rng.Value2 = v;
                    ws.Rows[1].Font.Bold = true;
                    ws.Columns[1].Hidden = true;
                    try { rng.Columns.AutoFit(); } catch (Exception) { }
                }
                dynamic meta = wb.Worksheets.Add(After: wb.Worksheets[wb.Worksheets.Count]);
                meta.Name = MetaSheet;
                object[,] m = new object[sheets.Count + 2, 3];
                m[0, 0] = "pluto-tables"; m[0, 1] = "1"; m[0, 2] = "do not edit this sheet";
                m[1, 0] = "snapshot"; m[1, 1] = snapshotFile; m[1, 2] = "";
                for (int i = 0; i < sheets.Count; i++) { m[i + 2, 0] = "table"; m[i + 2, 1] = sheets[i].Table; m[i + 2, 2] = sheets[i].Name; }
                dynamic mr = meta.Range(meta.Cells[1, 1], meta.Cells[sheets.Count + 2, 3]);
                mr.NumberFormat = "@";
                mr.Value2 = m;
                meta.Visible = 2;   // xlSheetVeryHidden
                wb.Worksheets[1].Activate();
                wb.SaveAs(path, 51);   // xlOpenXMLWorkbook
                wb.Close(false);
            }
            finally { Quit(xl); }
            return 0;
        });
    }

    // The sheets of a workbook written by Write (read-only; the user's Excel may still have it open).
    public List<Sheet> Read(string path, out string snapshotFile)
    {
        string snap = null;
        List<Sheet> result = sta.Invoke("excel read", Timeout, delegate
        {
            List<Sheet> list = new List<Sheet>();
            dynamic xl = NewExcel();
            try
            {
                dynamic wb = xl.Workbooks.Open(path, 0, true);
                try
                {
                    dynamic meta = null;
                    foreach (dynamic w in wb.Worksheets) if (string.Equals((string)w.Name, MetaSheet, StringComparison.OrdinalIgnoreCase)) meta = w;
                    if (meta == null) throw new ToolError(path + " was not written by export_tables (no " + MetaSheet + " sheet)");
                    object[,] m = Cells(meta);
                    for (int r = m.GetLowerBound(0); r <= m.GetUpperBound(0); r++)
                    {
                        string k = Text(m[r, m.GetLowerBound(1)]);
                        if (k == "snapshot") snap = Text(m[r, m.GetLowerBound(1) + 1]);
                        if (k != "table") continue;
                        Sheet s = new Sheet();
                        s.Table = Text(m[r, m.GetLowerBound(1) + 1]);
                        s.Name = Text(m[r, m.GetLowerBound(1) + 2]);
                        dynamic ws;
                        try { ws = wb.Worksheets[s.Name]; }
                        catch (Exception) { throw new ToolError("sheet '" + s.Name + "' (table " + s.Table + ") is missing from " + path); }
                        object[,] v = Cells(ws);
                        int r0 = v.GetLowerBound(0), c0 = v.GetLowerBound(1), nc = v.GetUpperBound(1) - c0 + 1;
                        if (Text(v[r0, c0]) != RowColumn) throw new ToolError("sheet '" + s.Name + "': column A must stay '" + RowColumn + "' (hidden); the sheet's first column was moved or deleted");
                        s.Fields = new string[nc - 1];
                        for (int c = 1; c < nc; c++) s.Fields[c - 1] = Text(v[r0, c0 + c]);
                        for (int rr = r0 + 1; rr <= v.GetUpperBound(0); rr++)
                        {
                            string[] row = new string[nc - 1];
                            bool any = false;
                            for (int c = 1; c < nc; c++) { row[c - 1] = Text(v[rr, c0 + c]); if (row[c - 1].Length > 0) any = true; }
                            if (!any) continue;   // blank rows are ignored
                            int id;
                            s.RowIds.Add(int.TryParse(Text(v[rr, c0]), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ? id : -1);
                            s.Rows.Add(row);
                        }
                        list.Add(s);
                    }
                }
                finally { wb.Close(false); }
            }
            finally { Quit(xl); }
            return list;
        });
        snapshotFile = snap;
        return result;
    }

    // From A1 to the last used cell, as a 2-D array (one cell comes back as a value, made a 1 x 1 array here).
    static object[,] Cells(dynamic ws)
    {
        dynamic used = ws.UsedRange;
        int lastRow = (int)used.Row + (int)used.Rows.Count - 1, lastCol = (int)used.Column + (int)used.Columns.Count - 1;
        object v = ws.Range(ws.Cells[1, 1], ws.Cells[lastRow, lastCol]).Value2;
        object[,] a = v as object[,];
        if (a != null) return a;
        a = (object[,])Array.CreateInstance(typeof(object), new[] { 1, 1 }, new[] { 1, 1 });
        a[1, 1] = v;
        return a;
    }

    // A cell as SAP2000 text: strings as they are; numbers Excel converted (typed into a reformatted cell) as G15.
    static string Text(object o)
    {
        if (o == null) return "";
        if (o is double) return ((double)o).ToString("G15", CultureInfo.InvariantCulture);
        if (o is bool) return (bool)o ? "Yes" : "No";
        return Convert.ToString(o, CultureInfo.InvariantCulture).Trim();
    }

    public void Dispose() { sta.Dispose(); }
}
