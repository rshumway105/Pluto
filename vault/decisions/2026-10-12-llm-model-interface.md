---
title: LLM model interface (Pluto MCP server) — plan
status: draft
created: 2026-10-12
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

# LLM model interface — plan

**Goal:** talk to a model through an LLM (Claude Code or Copilot agent mode) that can read it,
change it, run it and explain the results, for fast iteration, understanding and troubleshooting
on models too large to post-process by hand. Scope: SAP2000 26 on the development machine, run
locally. The viewer stays a separate window that the LLM can drive.

**Decision (2026-10-12):** one MCP server, `PlutoMcp`, built on the existing SAP controller
(`SapSession` and the classes on it). The chat stays in Claude Code / VS Code (option A); an
in-viewer chat panel (option B) can be added later on the same server without rework.

```
 Claude Code / Copilot ──MCP (stdio)──► PlutoMcp.exe ──OAPI (COM)──► SAP2000 26 (visible GUI)
                                            │
                                            ├── working copies, checkpoints, change log  (<work dir>)
                                            └── http://localhost:<port>  ──WebSocket──► Pluto viewer
```

> [!info]- 0. Scope: CSI programs, one driver interface
> - **CSI only for now:** SAP2000 first, then ETABS (23 installed), possibly SAFE (22 installed). STAAD,
>   RISA and others are out of scope (2026-10-12). STAAD (OpenSTAAD) and RISA (SAF files, no API found) were assessed and parked.
> - Tools are named for the task, not the program (`describe_model`, `select_objects`,
>   `query_results`, `run_analysis`); one driver per program sits behind them and states its
>   capabilities so the LLM knows what a program cannot do. Phase 0 sets this structure up even though SAP is the only driver.
> - ETABS: same OAPI shape (`ETABSv1.dll`); stories are native, so level selection is exact.
>   Check whether the installs share `CSiAPIv1.dll` (one driver for all three) when ETABS starts.

> [!info]- 1. Runtime and build
> - **C# 5, .NET Framework 4.8**, compiled by `csc.exe` from the Framework
>   (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) into `PlutoMcp.exe` by
>   `scripts/mcp/Build-PlutoMcp.ps1`, against the newest SAP install's `SAP2000v1.dll` (same
>   lookup as `Import-SapApi.ps1`). No SDK, no packages, no Python, so it stays inside the repo's
>   constraints and could later run on the production machine.
> - It compiles the existing `scripts/sap/Sap*.cs` and the lib writers in, so it reuses the code
>   rather than copying it. New SAP capability goes into `SapSession` / a class on it (a method
>   the server and `Run-Sap.ps1` can both call) and into the capability table in
>   [[vault/arms/sap-controller|sap-controller]], per the one-controller rule.
> - Why an exe and not a PowerShell host: MCP over stdio needs a clean stdout and one
>   long-lived process; an exe gives both and keeps the SAP connection and compiled code warm
>   (a `Run-Sap.ps1` call recompiles and reattaches every time).
> - **Version upgrades (e.g. SAP 27): rebuild, not rewrite.** The OAPI keeps its `v1`
>   interface across releases (changed calls get a `_1` variant beside the old one). The build
>   targets the newest install; the server records the hash of the `SAP2000v1.dll` it was built against and rebuilds
>   on start when it changes, then runs the self-test beam. Rules that keep this true: stamp generated `.s2k` with
>   `Version()`; late-bind members newer than the oldest supported SAP (as `DatabaseTables` is).
> - JSON: `System.Web.Script.Serialization.JavaScriptSerializer` (already referenced by the lib).

> [!info]- 2. Server shape
> - **MCP over stdio**, JSON-RPC 2.0, hand-rolled: `initialize`, `tools/list`, `tools/call`,
>   notifications. Small surface; no SDK needed.
> - **Threads:** stdin reader → request queue → one **STA worker thread** that owns the
>   `SapSession` (every OAPI call happens there, in order); an `HttpListener` thread for the
>   viewer; log to stderr and `<work dir>/pluto-mcp.log`, never stdout.
> - **Attach, don't start:** default is to attach to the SAP you already have open, so every
>   change also shows in the SAP GUI (call `RefreshView` after edits). `NewInstance` stays
>   available for unattended runs.
> - **Registration:** `.mcp.json` at the repo root (Claude Code) and `.vscode/mcp.json`
>   (Copilot agent mode). Same exe, same tools.
> - **Units:** every tool reports values in the model's present units and says which; tools that
>   take numbers accept an optional `units` argument.

> [!info]- 3. Safety model: the original is never written
> - `open_model` opens the file, then immediately **saves as** a working copy
>   `<work dir>/<model>/r000.sdb`. Edits go to the working copy only, the same rule `RunOnCopy`
>   follows today.
> - **Checkpoints:** every `run_analysis` first saves `rNNN.sdb`. `revert(checkpoint)` reopens
>   one. `list_checkpoints` shows them with their change summary.
> - **Change log:** `<work dir>/<model>/changes.jsonl`: one line per edit (tool, arguments,
>   before, after, checkpoint). The LLM can read it back; you can audit it.
> - **Edit tools return before → after** and accept `dry_run: true`.
> - **Approval:** read tools can be allowlisted in Claude Code; edit and run tools prompt each
>   time unless you allow them.
> - **Output budget:** every tool caps its reply (default 200 rows, summary first) and pages
>   with `offset`. Large data goes to a file in the work dir and the reply gives its path and
>   summary, never the raw dump.

> [!info]- 4. Tools, by phase
> **Phase 0: spike (proves the plumbing)**
> - `Build-PlutoMcp.ps1`, the stdio loop, `sap_status` (version, model path, units, counts,
>   case status). Claude Code connects; the existing self-test beam (`WriteTestBeamS2k`) opens,
>   runs, and reports V = 5 kip, M = 50 kip-ft through the server.
>
> **Phase 1: read and understand**
> - `open_model(path)`, `describe_model` (units, counts, groups, patterns, cases, combos,
>   materials, sections, what has results).
> - `list_objects(kind, group?, offset)`; `get_object(kind, name)`: connectivity, section,
>   releases, local axes, assigned loads, restraints, groups.
> - `get_sections`, `get_materials`, `get_load_cases` (definition plus status), `get_combos`.
> - `select_objects(kind?, z_min?, z_max?, box?, plane?, section?, group?, save_as?)`: members by
>   geometry and attributes, combined; returns IDs plus a summary and can save the set as a sidecar
>   group. `describe_levels` finds floor elevations (grid Z lines, floor-named groups, clusters
>   of joint Z) so "5th floor and above" resolves to a Z range; ask when ambiguous.
> - `read_table(name, filter?)`: any `DatabaseTables` input table, paged. This is the general way to
>   read anything without a dedicated tool.
>
> **Phase 2: run and query results**
> - `run_analysis(cases?)`: checkpoint, run, report finished / not run plus warnings.
> - `query_results(kind: frame|area|joint|link|reaction, group?, cases?, quantity, top_n,
>   envelope)`: live from `Results.*` at full precision, returning top-N, the controlling case and
>   per-group summaries.
> - `reactions_summary(cases?)`: totals by direction against applied load (equilibrium check).
> - `compare_runs(a, b, quantity, group?)`: biggest changes between two checkpoints, from the
>   per-run summaries the server saves after each run.
> - `export_viewer(results?)`: the existing `SapExport` → `SapToPluto` path, to `.bin` +
>   `.features.json` in the work dir.
>
> **Phase 3: change the model**
> - Targeted: `set_frame_section`, `set_area_section`, `set_material_property`,
>   `set_restraint`, `set_frame_release`, `set_link_property`, `assign_frame_load`,
>   `assign_area_load`, `assign_joint_load`, `define_load_pattern|case|combo`,
>   `set_group(members)`, `move_joint`, `add_frame`, `delete_object`. Each takes a name list or a
>   group, returns before → after, and logs.
> - General: `edit_table(name, rows)` through `DatabaseTables` (get for editing, set,
>   `ApplyEditedTables`), with the import log returned. It covers anything the targeted tools do
>   not, at the cost of being blunter. Dry-run shows the diff first.
> - `revert(checkpoint)`, `list_checkpoints`, `get_change_log`.
>
> **Phase 4: drive the viewer**
> - The server serves `viewer/` and the work dir on `http://localhost:<port>`. The viewer gets a
>   live mode (`?live=1`): a WebSocket to the server and URL-backed loading in `files.js`
>   (`fetch` + HTTP Range, so multi-GB `.bin` still never loads whole).
> - Tools: `viewer_open` (prints the URL), `viewer_load(bin)`, `viewer_show(component, case)`,
>   `viewer_highlight(ids|group)`, `viewer_isolate(group)`, `viewer_focus(id)`,
>   `viewer_get_selection` (what you clicked, so "why is this one red?" works).
> - After `run_analysis`, an optional auto-export reloads the viewer.
>
> **Phase 5: hand-off**
> - Copilot agent-mode config and check; a usage note under `vault/arms/` (`pluto-mcp`); a
>   capability table like the SAP controller's; the Arms map line.

> [!info]- 5. Testing
> - C#: the PS 5.1 compile probe per `CLAUDE.md`, plus `Build-PlutoMcp.ps1` warnings-as-errors.
> - Protocol: a scripted stdio client (PowerShell) that sends `initialize`, `tools/list`,
>   `tools/call` and checks replies, with no LLM involved.
> - SAP: the self-test beam (hand-checked numbers); a small synthetic frame and a generated
>   tank from `pythonTools/sap` (dev side only) for edit → run → compare round trips; revert
>   restores byte-identical results.
> - Viewer: `node viewer/tests/*.js` plus a live-mode test against a stub server.

> [!info]- 6. Risks and open questions
> - **COM threading:** the OAPI from a long-lived exe on one STA thread is assumed fine;
>   Phase 0 proves it before anything else is built.
> - **Modal dialogs:** SAP blocks the API while a dialog is open. The server times out with a clear
>   message ("SAP is showing a dialog") instead of hanging the chat.
> - **Model locked after a run:** edits unlock it (`SetModelIsLocked(false)`), which discards
>   results. Tools say so, and checkpoints keep the old results.
> - **SAP GUI views (probed 2026-10-12, SAP 26):** `cView` has only `RefreshView` / `RefreshWindow`; SAP's own
>   displays (force diagrams, deformed shape) cannot be switched. `cSelect` is rich (`Group`,`CoordinateRange`,
>   `PlaneXY`, `Property*`, `SupportedPoints`), so selections can be shown in SAP. Views are driven
>   in the Pluto viewer; SAP shows selections and edits.
> - **`DatabaseTables` editing** is powerful but blunt; it stays behind dry-run and a diff.
> - **LLM mistakes:** units and sign conventions (SAP local axes). Tools state units and axes in
>   every reply; `reactions_summary` gives a cheap sanity check after each run.
> - **Production machine (SAP 22):** out of scope now. Keep newer-API members late-bound (as
>   `DatabaseTables` already is) so the exe can still compile against 22 later.
> - **Data:** model data goes to the LLM provider during a session. Accepted for local use on
>   this machine (2026-10-12). Nothing project-identifying enters the repo: the work dir is
>   outside it and gitignored.

> [!info]- 7. Order of work and size
> | phase | delivers | rough size |
> |---|---|---|
> | 0 | build, stdio loop, `sap_status`, self-test through Claude Code | 1 session |
> | 1 | read tools | 1 session |
> | 2 | run, results queries, compare | 1–2 sessions |
> | 3 | edit tools, checkpoints, change log, `edit_table` | 2 sessions |
> | 4 | viewer live mode | 2 sessions |
> | 5 | Copilot, docs | ½ session |
>
> Phases 0–2 already give a useful tool ("explain this model, run it, what governs"). Phase 3
> is the iteration loop; Phase 4 adds seeing it.

## Phase 0 status (2026-10-12)

Built and tested on the development machine (SAP2000 26.3.0). Code in `scripts/mcp/`:

| file | role |
|---|---|
| `McpServer.cs` | stdio loop, JSON-RPC dispatch (`initialize`, `ping`, `tools/list`, `tools/call`), SAP dll resolution from the install |
| `McpCore.cs` | JSON (NaN → null), log (stderr + `<work>/pluto-mcp.log`), `StaWorker`, `BuildInfo`, `ServerContext` |
| `SapDriver.cs` | `IModelDriver` + the SAP2000 driver on `SapSession`: `Status`, `SelfTest`, capabilities |
| `Tools.cs` | tool table: `status`, `self_test` |
| `Build-PlutoMcp.ps1` | csc.exe build → `bin/PlutoMcp.exe` + `bin/build.json` (gitignored) |
| `Start-PlutoMcp.ps1` | what the client runs: rebuilds on source change or a different `SAP2000v1.dll`, then the exe |
| `Test-PlutoMcp.ps1` | protocol test without an LLM (`-Direct`, `-SelfTest`) |

Registered in `.mcp.json` (Claude Code) and `.vscode/mcp.json` (Copilot). Verified: protocol checks pass;
`self_test` in a new SAP instance passes (V 5 kip, M 50 kip-ft, 29 s) through the launcher; auto-rebuild on a
source change works.

**Attach (resolved 2026-10-12): privilege level.** An elevated process sees an empty running-object table, so it
cannot find a normal SAP2000 (`MK_E_UNAVAILABLE`); from a normal process the unchanged `SapSession.Attach` works
(protocol test: `attached 26.3.0`). Nothing to set in SAP; no model needs to be open. **Run the MCP client normally,**
**not as administrator** (or both elevated). `status` names this cause when the server is elevated.

**For ETABS:** the running SAP 26 instance answers both `SAP2000v1.cOAPI` and `CSiAPIv1.cOAPI` (`CSiAPIv1.dll`, CSI's
shared API, shipped with SAP 26). The ETABS driver should be checked against `CSiAPIv1` first: possibly one driver.

## Phase 1 status (2026-10-12)

Built and tested on the development machine (SAP2000 26.3.0), server 0.2.0. The read code is controller code:
`scripts/sap/SapRead.cs` (on a `SapSession`, so `Run-Sap.ps1 -Describe <json>` uses it too); the server adds
argument checks, paging and files in `Tools.cs` and the working copy in `SapDriver.OpenModel`.

| tool | notes |
|---|---|
| `open_model(path)` | `.sdb`, or `.s2k` / `.$2k` imported; saved at once as `<work>\models\<model>\<yyyyMMdd-HHmmss>\r000.sdb` with `session.json` (original path, size, time). A session folder per open, so earlier checkpoints survive a reopen. Warns when an import yields no joints (version stamp). |
| `describe_model` | as planned, plus extent, supports by restraint pattern, constraints, coordinate systems; lists capped (`max_rows`) with `<list>Total` |
| `list_objects`, `get_object` | paged; `get_object` adds connected objects (joints), insertion point, end offsets, modifiers and loads (joint force / ground displacement, frame distributed / point, area uniform / surface pressure / gravity) |
| `get_sections`, `get_materials`, `get_load_cases`, `get_combos` | wildcard names; frame dimensions for I, box, pipe, rectangle, circle, angle, channel (others: `read_table`); case loads and initial case for linear / nonlinear static |
| `select_objects` | kind, Z range, box, plane, section, group, saved selection, name; `match` all / any / centroid; default tolerance 0.1 % of the largest extent. `save_as` → `<work>\selections\<name>.json` (not yet a viewer sidecar group: that needs the export, phase 2/4). `show_in_sap`: a group alone goes through `SelectObj.Group`; otherwise per object, capped at 2000 (~30 ms each) |
| `describe_levels` | Z clusters with horizontal frame / area counts (floor-like at ≥ max(3, 2 %) of them), grid Z lines (`Grid Lines` table), level-named groups with Z range |
| `read_table` | no name: tables holding data (`GetAllTables`, not empty); name: rows, `group`, `filter` (wildcards), `fields`, paging, `to_file` (TSV in `<work>\tables`) |

The object index (every joint's coordinates, frame / area / link connectivity and property) is cached per
(model path, counts, units). **Phase 3 edit tools must call `SapRead.Invalidate()`**: an edit that keeps the
counts would not be seen otherwise.

Verified: `Test-PlutoMcp.ps1 -Model <file>` (every phase 1 tool, consistency checks: counts agree across tools,
section use sums to the frames, below + above − on a plane = all joints, saved selection round trip, table rows
= joints, filter + file) passes on the example tank (1371 joints, 1152 areas, 72 frames, 253 links); `-SelfTest`
still passes; PS 5.1 `Add-Type` compile of `scripts/sap` and `Config.ps1` clean; `Run-Sap.ps1 -Describe` works.

Clients: Claude Desktop connects and lists the tools (an `mcpServers.pluto` entry with the absolute path to
`Start-PlutoMcp.ps1`; the Store install keeps `claude_desktop_config.json` under
`%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude`, and the app reads it only on a full quit and restart).
Microsoft 365 Copilot cannot start a local stdio server, so it is out; VS Code Copilot agent mode is configured but
dropped as a target.

Not yet tried: a large model (index time; seconds per 10k objects expected from 0.4 s for 2.8k), a model with
combos and frame sections of every type, and the tools from Claude Code itself (this session's client was not
started in the repo, so `.mcp.json` was not loaded; the protocol test drove the server).

## Phase 2 status (2026-10-12)

Built and tested on the development machine (SAP2000 26.3.0), server 0.3.0. Results code is controller code,
`scripts/sap/SapResults.cs` (on a `SapSession` and a `SapRead`); checkpoints and run summaries are in
`SapDriver.RunAnalysis`, the tools in `Tools.cs`.

| tool | notes |
|---|---|
| `run_analysis(cases?)` | saves `<session dir>\rNNN.sdb` (next number; r000 stays the opened model) and runs there; `cases` also flags the initial cases they continue from. A model SAP held without `open_model` gets a session of its own first (r000 = its state then). Reply: case status, WARNING / ERROR / instability lines of SAP's `.LOG` (all-clear lines dropped), equilibrium per case, largest value of every quantity. Saves `runs\rNNN\<kind>.tsv` envelopes + `summary.json`, and a line in `runs.jsonl` |
| `query_results` | kinds joint (U1–R3, U), reaction, frame, area (forces), area_stress, link; `Results.*` with the case / combo selected for output; nonlinear and multi-step static: last step (`SetOption…Static(3)`). Top N by magnitude / max / min, envelope per object (controlling case and station / joint), max / min, worst per case, `by_group` (wildcards, ≤ 100), filters group / selection / name, `to_file` (TSV in `<work>\results`) |
| `reactions_summary` | `BaseReact` per case / combo against the applied load summed from the assignments: self weight (frame A × L × weight per volume × modifiers, shell membrane thickness × area, link weight), joint forces, frame distributed / point / gravity loads (local, global, projected), area uniform / uniform-to-frame / gravity loads, face pressures (joint pattern: corner mean × area; top face pushes along −3, bottom along +3). Linear and nonlinear static cases (with their initial case), linear additive combos. Default tolerance 0.5 % |
| `compare_runs(a?, b?)` | from the saved envelopes: per object and case, b − a of one quantity, top N; changes below 1e-9 of the largest magnitude are noise (a nonlinear rerun differs by ~1e-14). Without kind: lists the runs |
| `export_viewer` | runs `Run-Sap.ps1 -KeepOpen -Export <session dir>\viewer\rNNN` as a child process (the lib needs Excel interop, so `SapToPluto` is not compiled into the exe); `.bin`, `.features.json`, model / results `.s2k`, log |

Verified (`Test-PlutoMcp.ps1 -SelfTest -Run -Model <tank>`, through `Start-PlutoMcp.ps1`, 132 checks): on the example
tank every case balances: DEAD residual 1e-10 of 214 kip, HYDRO / NL_HYDRO / NL_SETTLE 6e-4 of 7021 / 7236 kip (the
corner-mean pressure); two runs of the same model compare as unchanged; the export writes the `.bin`. On the
self-test beam through the tools: M3 50 kip-ft, applied FZ −10 against reaction +10, two supports at 5 kip.
`Build-PlutoMcp.ps1` now moves a locked exe aside (a running client holds it) instead of failing.

Not yet tried: other case types (response spectrum, time history: reported unchecked), combos (the example tank has
none), models with edge pressures or auto lateral loads, run time on a large model against the client's tool
timeout. Saved selections are still not viewer groups.