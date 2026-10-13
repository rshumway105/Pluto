---
title: Pluto MCP server — an LLM reads, edits, runs and views a SAP2000 model
status: current
created: 2026-10-12
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# Pluto MCP server — `PlutoMcp.exe`

An MCP server (stdio, JSON-RPC 2.0) that lets an LLM client read a SAP2000 model, change it on
checkpoints, run it, query results at full precision, round-trip input tables through Excel and drive
the Pluto viewer live. It is a C# 5 exe built on the one SAP controller
([[vault/arms/sap-controller|sap-controller]]); it adds argument checks, paging, files, checkpoints
and the change log, never a second driver. The plan, decisions and per-phase test records are in
[[vault/decisions/2026-10-12-llm-model-interface|2026-10-12-llm-model-interface]]. Server 0.8.0,
SAP2000 26.3, development machine.

```text
 Claude Desktop / Claude Code ──MCP (stdio)──► PlutoMcp.exe ──OAPI (COM)──► SAP2000 (visible GUI)
                                                  ├── <work dir>: working copies, checkpoints, runs, change log
                                                  └── http://localhost:8765 ──WebSocket──► viewer (?live=1)
```

> [!info]- Start and client setup
> - **What the client runs:** `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <repo>\scripts\mcp\Start-PlutoMcp.ps1`.
>   The launcher rebuilds `scripts\mcp\bin\PlutoMcp.exe` when it is missing, a `scripts\mcp\*.cs` or
>   `scripts\sap\Sap*.cs` is newer, or the newest SAP2000's `SAP2000v1.dll` differs from the one it was
>   built against (a SAP upgrade). Build output goes to `<work>\pluto-mcp-build.log`, never stdout.
> - **Build by hand:** `scripts\mcp\Build-PlutoMcp.ps1` (csc.exe from .NET Framework 4.8,
>   warnings-as-errors). A running client holds the exe; the build moves it aside as
>   `PlutoMcp.<stamp>.old` instead of failing.
> - **Claude Desktop:** an `mcpServers.pluto` entry in `claude_desktop_config.json` with the command
>   above and the absolute path to `Start-PlutoMcp.ps1`. The Store install keeps the file under
>   `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\`; its log is
>   `%LOCALAPPDATA%\Claude\logs\mcp-server-pluto.log`. Desktop reads the config, and starts a new
>   server build, only after a full quit from the tray.
> - **Claude Code:** `.mcp.json` at the repo root (relative path; start Claude Code in the repo).
>   `.vscode\mcp.json` (Copilot agent mode) is kept but not a target: Microsoft 365 Copilot cannot
>   start a local stdio server, and VS Code Copilot was dropped.
> - **Run the client normally, not as administrator.** An elevated process cannot see a normal
>   SAP2000 in the running-object table; `status` names this when it happens.
> - **First call:** `status` (server version, work dir, rebuild needed, what SAP2000 holds,
>   capabilities). `self_test` checks the install in a second SAP2000 (20 ft beam: V 5 kip, M 50 kip-ft).
>
> | environment variable | effect |
> |---|---|
> | `PLUTO_WORK` | work dir (default `%LOCALAPPDATA%\Pluto\work`) |
> | `PLUTO_SAP_DIR` | SAP2000 install to build against and start (default the newest `SAP2000 NN`) |
> | `PLUTO_SAP_INSTANCE=new` | start an own SAP2000 (closed on exit) instead of attaching to the user's |
> | `PLUTO_VIEWER_PORT` | viewer host port (default 8765, next free if taken) |

> [!info]- Tools (capability table)
> Every tool states units (the model's present units) and SAP2000 local axes in its reply, caps its
> rows (default 200, `offset` / `limit` to page) and writes large output to a TSV in the work dir
> (`to_file`) instead of the reply. Read tools never change the model.
>
> | group | tools | what they do | controller code |
> |---|---|---|---|
> | start | `status`, `self_test` | server and program state, capabilities; install check in a second SAP2000 | `SapDriver`, `SapSession.WriteTestBeamS2k` |
> | open | `open_model` | `.sdb` or `.s2k` / `.$2k` → saved at once as the session's `r000.sdb`; SAP2000 then holds the copy. Replaces what SAP2000 holds | `SapDriver.OpenModel` |
> | read | `describe_model`, `list_objects`, `get_object`, `get_sections`, `get_materials`, `get_load_cases`, `get_combos` | overview, paged objects, one object in full, definitions with use counts | `SapRead` |
> | select | `select_objects`, `describe_levels` | objects by Z range, box, plane, section, group, name (combined); `save_as` a selection, `show_in_sap`; floor elevations from Z clusters, grid lines and level-named groups | `SapRead` |
> | tables | `read_table` | any `DatabaseTables` input table, filtered, paged (display-rounded: input only) | `SapSession.TableRows` |
> | run | `run_analysis` | new checkpoint, run, case status, log warnings, equilibrium, largest values; saves envelopes | `SapResults`, `SapDriver.RunAnalysis` |
> | results | `query_results`, `reactions_summary`, `compare_runs` | top N / envelope / per case / per group at full precision; base reactions against the applied load; biggest changes between two runs. Modal / buckling cases are left out unless named (mode shapes have no scale) | `SapResults` |
> | edit | `set_frame_section`, `set_area_section`, `set_link_property`, `set_material_property`, `set_restraint`, `set_frame_release`, `assign_joint_load`, `assign_frame_load`, `assign_area_load`, `define_load_pattern`, `define_load_case`, `define_combo`, `set_group`, `move_joints`, `add_frame`, `delete_objects` | targeted edits by names, group or saved selection; before → after; `dry_run`; an optional `reason`, kept in the edit log. `move_joints` takes one rule for a whole shape change (below) | `SapEdit` |
> | history | `list_checkpoints`, `revert`, `get_change_log` | checkpoints with run status and edits; reopen one; the edit log | `SapDriver` |
> | design notes | `log_note`, `list_notes` | assumptions, conditions, decisions and questions (topic, basis, what they concern) in the session's `notes.jsonl`; a later note supersedes an earlier one, nothing is deleted. Feeds the design record ([[vault/decisions/2026-10-13-qa-records-and-bod-map\|plan]]) | `ToolSet` |
> | Excel | `export_tables`, `import_tables` | input tables to a workbook the user edits, then back as one import (dry run by default) | `SapTables`, `ExcelTables` |
> | viewer | `export_viewer`, `viewer_open`, `viewer_load`, `viewer_show`, `viewer_highlight`, `viewer_isolate`, `viewer_focus`, `viewer_get_selection` | `.bin` + `.features.json` export; the live viewer: load over HTTP Range, component / case, paint / isolate / focus objects, read back what the user pinned | `SapExport` (via `Run-Sap.ps1`), `ViewerHost`, `viewer/scripts/live.js` |
>
> **`move_joints` (0.7.0): one rule, checked before it is applied.** Rules: `delta`, `to` (one joint),
> `scale` about `about` (e.g. every height × 0.8: `scale [1, 1, 0.8]`, `about [0, 0, 0]`), or `map`
> `{axis, from, to}` (piecewise-linear old levels → new levels; joints outside `from`'s range stay). Targets:
> names, group, selection and / or `z_min` / `z_max`. The server computes every coordinate; the dry run
> reports `checks`: frames / links that collapse or reverse, areas that flip, joints that land on another
> joint (any of these blocks the edit unless `force`), and `leftBehind`, the joints connected to moved ones
> that the rule would also move but that were not targeted (the forgotten ring). It also gives
> `estimatedSeconds`: SAP2000 takes about 0.1 s per coordinate change, an edit at most 15 minutes (about
> 8000 joints), so bigger moves go in Z bands. Why: a squash of the example tank done as 24 per-ring
> `move_joints` calls left the top roof ring and apex behind; as one `scale` call it moves all 901 joints
> with clean checks.
>
> Not covered by a dedicated tool: editing non-static load cases, ETABS / SAFE (planned behind the
> same tool names). `read_table` reads anything; `export_tables` / `import_tables` edit anything a
> table holds.

> [!info]- Work dir, sessions and the checkpoint rule
> - **The opened file is never written.** `open_model` makes a session folder
>   `<work>\models\<model>\<yyyyMMdd-HHmmss>\` (a new one per open, so earlier ones survive) with
>   `r000.sdb` and `session.json` (original path, size, time). A `.s2k` is imported in a helper
>   SAP2000 that saves `r000.sdb` and closes; the user's SAP2000 opens only the `.sdb` (see Known limits).
>   A model SAP2000 already held gets a session of its own on the first run or edit.
> - **One model state per `rNNN.sdb`.** An edit or run goes into the latest checkpoint while it has no
>   `runs\rNNN`; otherwise (r000, a run checkpoint, an older one after `revert`) the model is first saved
>   as the next number. Saving under a new name copies the model only, so every run checkpoint keeps its
>   results and can be queried again after `revert`.
> - An edit unlocks the model (SAP2000 refuses edits to a model with results) and says so; the results
>   stay with the run checkpoint.
> - In the session folder: `changes.jsonl` (every applied edit: time, checkpoint, tool, arguments,
>   before → after), `runs\rNNN\` (envelope TSVs + `summary.json`), `runs.jsonl`, `viewer\rNNN\`
>   (exports), `tables\` (Excel workbooks + snapshots). In the work dir: `selections\`, `tables\`,
>   `results\` (TSVs from `to_file`), `selftest\`, `pluto-mcp.log`.

> [!info]- Input tables through Excel
> 1. `export_tables(tables, group?)` writes `<session>\tables\<checkpoint>-<stamp>.xlsx` (one sheet per
>    table, values as SAP2000's text, a hidden `_row` column, a very hidden `_pluto` sheet) and a snapshot
>    `<workbook>.pluto.json`, then opens it in Excel.
> 2. The user edits and saves (change cells, add rows at the bottom, delete rows).
> 3. `import_tables()` reads it back in an invisible Excel of its own and lists changed / added / deleted
>    rows per table against the snapshot. **Dry run by default:** show the list, then call again with
>    `dry_run: false`. It warns when the model's tables changed since the export (`force` to go on).
> 4. Applied: into a checkpoint (unlocked), every table in one `ApplyEditedTables`, SAP2000's warning /
>    error lines and log returned, rows read back (`notAsSent` = rows SAP2000 did not keep), logged like
>    every edit. `revert` goes back.
>
> The server never drives an Excel the user has open; it starts its own and quits it.

> [!info]- Isolation and testing
> - By default the server **attaches to the SAP2000 the user has open**, so edits show in the GUI.
>   `open_model`, `revert` and `self_test use_running_instance` replace what it holds; the tool
>   descriptions tell the LLM to ask first.
> - **Tests never use the user's SAP2000:** `-Isolated` sets `PLUTO_SAP_INSTANCE=new`, so the server
>   starts its own SAP2000 and closes it. Full run (273 checks):
>
>   ```text
>   scripts\mcp\Test-PlutoMcp.ps1 -Isolated -SelfTest -Run -Viewer -Edit -Tables -Model <scratch copy of a .s2k>
>   ```
>
>   `-SelfTest` (beam hand values), `-Model` (read tools), `-Run` (runs, results, compare, export),
>   `-Edit` (edits, checkpoints, revert, change log), `-Viewer` (live viewer in headless Edge),
>   `-Tables` (Excel round trip), `-Direct` (the exe without the launcher). Exit 0 = every check passed.
> - `GetActiveObject` reaches only the first SAP2000 started; an own instance is reached by process id
>   (`Run-Sap.ps1 -AttachPid`, used by `export_viewer`).

> [!info]- Known limits
> - **A SAP2000 that imported a `.s2k` cannot import tables** until it restarts: every later
>   `ApplyEditedTables` fails with an empty log, and after that `GetTableForDisplayArray` returns no rows
>   for any table. Hence the helper instance in `open_model`. Do not `File.OpenFile` a `.s2k` in the
>   user's SAP2000 by other routes either.
> - A model with results refuses table import: the server unlocks first.
> - SAP2000's own displays (force diagrams, deformed shape) cannot be switched over the API; views are
>   driven in the Pluto viewer, SAP2000 shows selections and edits.
> - A modal dialog in SAP2000 blocks the API.
> - Time histories come back as envelopes (Max / Min); there is no step-by-step history ("when is the peak").
> - Joint moves cost SAP2000 about 0.1 s each (probed 2026-10-12: one `EditGeneral.Move` of a selected band
>   is only ~2.6× faster). A Joint Coordinates table import would be faster but fails, and leaves tables
>   unreadable, in a SAP2000 that has imported a `.s2k`, so it is not used for moves in the user's SAP2000.
> - `export_viewer` runs `Run-Sap.ps1 -Export` as a child process (the lib needs Excel interop, so
>   `SapToPluto` is not compiled into the exe).
> - Checked against hand values (2026-10-12, own SAP2000): `set_material_property` (weight and E on a plate:
>   DEAD 15 → 30 kip, SDL deflection halved), area loads (gravity and X), a response spectrum (one-mass
>   cantilever, flat 0.5 g: T 0.195217 s, base shear 16.087 kip) and a linear modal time history (step load:
>   peak tip displacement 2F/k, base shear 2F).
> - Checked by the user in Claude Desktop (2026-10-12): the live viewer follows Claude's viewer tools, and a shell
>   the user clicked (pinned readout) is read back through `viewer_get_selection`.
> - The live viewer opens Y-up; SAP2000 models need **Z up** ticked by hand (not yet set by `viewer_load`).
> - Not yet tried: a multi-GB `.bin` over
>   HTTP Range; a large model (index and export time, a `run_analysis` longer than the client's tool
>   timeout); SAP2000 22 (the production machine).
> - Data: model data goes to the LLM provider during a session. The work dir is outside the repo.
