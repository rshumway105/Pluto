# Pluto

A FEMAP-style hub for finite-element data: solver **import/export arms** converge on one
common model (a v4 binary for results + geometry, a JSON **features sidecar** for
everything a user defines), and one generic **viewer** consumes it.

```text
  Solver A  ──import──┐                       ┌──export──▶  Solver A
  Solver B  ──import──┤                       ├──export──▶  Solver B
  Solver C  ──import──┼──▶  common model  ──▶ ┤
  ...                 │      (Pluto core)     └──export──▶  ...
                      └──▶  Viewer arm  (generic point of convergence)
```

**Documentation lives in the Obsidian vault — start at `vault/Pluto Home.md`, whose up-link opens the full tool note in the Notes vault.**
The current state and next task are in the Notes vault handoff (`Tools/Pluto Handoffs/`).

## Repo layout

```text
vault/             Obsidian vault (vault root = repo root) — ALL documentation
scripts/           production C# 5 / PowerShell 5.1 toolchain: STAAD post-processing lib,
  lib/               v4 writer, sidecar writer — one Add-Type batch via lib/Config.ps1
  arms/              pipe / steel / combined CSV / SAP2000 -> .bin + sidecar
  sap/               the SAP2000 controller (C# over the CSI OAPI): read, edit, run, results, export
  mcp/               Pluto MCP server (PlutoMcp.exe) for Claude Desktop / Claude Code, and its tests
viewer/            the viewer (open viewer/index.html; no build, no server)
pythonTools/       development-side Python (sap/: SAP2000 tank generator -> .s2k)
archive/           superseded code (old viewer, v3 writer) -- reference only
```

## Work on a model through an LLM (Pluto MCP server)

`scripts/mcp/` builds `PlutoMcp.exe`, an MCP server that lets Claude (Desktop or Code) drive a running SAP2000:

- **Read** the model: overview, objects, sections, materials, load cases, combinations, and its basis
  (supports, releases, mass source, each case's definition).
- **Edit** it with targeted tools (sections, restraints, releases, loads, cases, combinations, joint moves, ...)
  into numbered checkpoints, never the opened file; every edit is logged, with an optional reason.
- **Run** it and query results, base-reaction equilibrium and run-to-run changes; drive the live viewer.
- **Record the design process:** log assumptions, conditions, decisions and questions with their basis, and write
  the session as a **design record** in an Obsidian vault (model basis, assumptions, changes and reasons, analysis
  checks, open questions), linked to basis-of-design element notes. Templates in `vault/_meta/templates/`.

Setup, tools and tests: `vault/arms/pluto-mcp.md`. Build with `scripts/mcp/Build-PlutoMcp.ps1`; test with
`scripts/mcp/Test-PlutoMcp.ps1 -Isolated ...` (its own SAP2000, never the one you have open).

## Run the viewer

Open `viewer/index.html`. A synthetic demo loads; use the file picker to open a real
`.bin` (drop its `.features.json` alongside for groups). `viewer/inspector.html` dumps a
file field by field.

**Project data is not in this repo.** Real tank configs and SAP models live in a local project folder outside the repo and are passed to the scripts by path; `[output] dir` in a config, or the default `../models` beside it, decides where a model lands. Project findings and drawings are notes in the Notes vault, not here.
