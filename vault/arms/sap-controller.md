---
title: SAP controller — the one place Pluto drives SAP2000
status: current
created: 2026-10-02
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

**Rule:** every SAP2000 OAPI call in Pluto is C# in `scripts/sap/`, built on `SapSession.cs`.
Python never talks to SAP: it calls `scripts/sap/Run-Sap.ps1` through
`pythonTools/sap/tankbuilder/sap_cli.py`. **Before writing SAP code, find the capability in the
table below. If it is missing, add it to the C# (a method plus a runner switch), never to Python.**
This is the only route that runs on the production machine (SAP 22, no Python). Unified
2026-10-02. The Python `comtypes` driver `sap_api.py` and the C# proof of concept `SapPoc.cs` are in `archive/`.

## Files

| file | role |
|---|---|
| `SapSession.cs` | the controller: attach / start / new instance, open (`.sdb` or `.s2k` import), save as, run, run on a copy, case status, load cases, groups, counts, area local axes, input tables read back (`TableRows`, `WriteTablesS2k`), results → `.s2k` (`WriteResultsS2k`), self-test beam |
| `SapRead.cs` | read-only views as JSON-ready dictionaries, for the [[vault/decisions/2026-10-12-llm-model-interface\|Pluto MCP server]]: object index (cached per model state), describe, list / one object in full, sections, materials, cases, combos, selection by geometry and attributes (+ GUI selection), levels, input tables. Uses a `SapSession` |
| `SapExport.cs` | whatever SAP holds → analysis mesh (`Mesh`, renumbered 1..N) → `model.s2k` + `labels.csv` for `SapToPluto` |
| `SapSurvey.cs` | read-only survey of a model (materials, sections, frames, links, joints, patterns, cases, combos, groups → TSVs), expansion-joint candidates, force export by group, the axis probe. Derives from `SapSession` |
| `SapRelease.cs` | HVAC link / direct release models, the synthetic duct test model. Uses a `SapSurvey` |
| `Import-SapApi.ps1` | dot-sourced by every runner: finds the install (`-SapDir`, `PLUTO_SAP_DIR`, else the newest `SAP2000 NN`), loads `SAP2000v1.dll`, compiles every `Sap*.cs` in one batch |
| `Run-Sap.ps1` | the general runner (below); Python's only entry point |
| `Run-SapSurvey.ps1`, `Run-SapRelease.ps1` | survey / candidates / forces / probe; release models (duct workflow) |

## Capabilities

| capability | C# | runner | used by |
|---|---|---|---|
| attach to the running SAP, else start one | `SapSession.AttachOrStart` | every runner | all |
| a second SAP whatever is running | `SapSession.NewInstance` | `Run-Sap.ps1 -NewInstance [-KeepOpen]` | `export_model.py --new-instance` |
| open `.sdb` / import `.s2k` (+ save `.sdb`) | `Open` | `-Open X [-NoSaveSdb]` | `run_sap.py`, `offset_study.py`, `export_model.py` |
| run; run on a saved copy; only if results are missing | `Run`, `RunOnCopy`, `AllCasesFinished` | `-Run`, `-RunOnCopy <sdb>\|auto`, `-RunIfNeeded` | tank, export |
| shell local 1 axis in global | `AreaLocal1`, `WriteAreaLocal1` | `-AreaAxes <tsv>` | `run_sap.py` axes check |
| input tables read back (display precision); by group; which tables hold data | `TableRows`, `WriteTablesS2k`, `AvailableTables` | `-Tables "a;b" -TablesOut <s2k>` | `offset_study.py` (insertion points, gap props), MCP `read_table` |
| model overview: units, counts, extent, supports, properties with use counts, groups, patterns, cases, combos | `SapRead.Describe` | `-Describe <json>` | MCP `describe_model` |
| list objects, one object in full, sections, materials, cases, combos, select by geometry / attributes, levels | `SapRead` | (MCP server only) | MCP phase 1 tools |
| results at full precision: joints, shells (forces + face stresses), frames, links | `WriteResultsS2k` | `-Results <s2k>` | tank `results.s2k` |
| model → viewer: mesh, model + results `.s2k`, `SapToPluto` | `SapExport` | `-Export <base\|folder> [-NoResults] [-FinalOnly] [-Cylindrical] [-NoViewerFile]` | `export_model.py` |
| survey, candidates, forces by group, axis probe | `SapSurvey` | `Run-SapSurvey.ps1` | duct workflow |
| link / direct release models, synthetic duct | `SapRelease` | `Run-SapRelease.ps1` | duct workflow |
| self test: 20 ft beam, 10 kip midspan → 5 kip, 50 kip-ft | `WriteTestBeamS2k` | `-SelfTest <dir>` | install check |

`Run-Sap.ps1` prints `[step]` progress lines and ends with `RESULT {json}` (version, counts,
groups, units, cases, output paths). Steps run in a fixed order: attach → open → axes → run → tables → describe → results → export.

## Facts the code relies on

- Stamp generated `.s2k` with the attached SAP's `Version()`: SAP 26 imports nothing from a file stamped 22.0.0, and `OpenFile` still returns 0.
- `RunAnalysis` needs a saved model and writes it. Use `RunOnCopy` when the opened file must stay untouched.
- GUI selection per object (`*Obj.SetSelected`) costs about 30 ms an object (SAP 26.3, 2026-10-12); `SelectObj.Group` selects a whole group in one call. Reading is far cheaper: indexing 2.8k objects takes 0.4 s.
- `Results.*` return full doubles. `DatabaseTables.GetTableForDisplayArray` is display-rounded, so use it for input tables only.
- Numbers are written as `G17`. .NET Framework's `"R"` drops the last digit of some values, and its `double.Parse` is not correctly rounded either. With G17, a value read back in Python equals SAP's double exactly; checked against the retired Python writer on the example tank, where all 54k rows were identical.
- Nonlinear static output is set to step-by-step (`SetOptionNLStatic(2)`). The default envelope rows zero the principals and von Mises.
- PowerShell cannot call methods on the COM model or pass it to a constructor. Go through a C# method (`SapExport.BuildMesh(session)`).
- From Python: the runner's output goes to a log file and stdin is closed. A SAP started by the child inherits its handles, so reading a pipe to EOF would hang until SAP exits.

## Verified 2026-10-02 (SAP 26.3.0)

- Self test OK.
- `run_sap.py` on the example tank: the C# results writer equals the Python writer on the same analysed model. Every value in joints, shell forces, shell stresses and frames matched; only Python's junk `StepType=None` is gone.
- `export_model.py`: `model.s2k` / `results.s2k` equal value for value and the `.bin` is byte-identical to the Python exporter.
- `offset_study.py --run` (beam): the CSV is identical apart from 1e-15 run-to-run noise in one row.
- Axis probe as recorded 2026-09-14.
- Survey OK.
- Duct `Run-ReleaseCheck.ps1 -TestModel`: see the handoff.

Not yet run on SAP 22: the first production run of any runner is that check.
