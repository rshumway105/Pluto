---
title: Viewer overview
status: current
created: 2026-08-26
---

# Viewer overview

*↑ [[vault/Pluto Home|Home]] › [[vault/viewer/Viewer map|Viewer map]]*

## What it is

Browser-based Three.js viewer for plate/shell FEA results with **true per-corner field
visualization** — bilinear interpolation across quads, no nodal averaging, no flat-shaded
triangle seams. Per-corner field planes hold stress and displacement components (and any
generic kind); stress and displacement are selected through the same dropdown, read by
the same strided math, and colored by the same shader. Beams are a second domain (below).

- **Multiple models at once**: the file picker accepts several `.bin`
  files with identical geometry (e.g. the same structure re-run with
  different soil springs). Load cases from all files form one globally
  indexed list — the LC dropdown groups them under file-name dividers
  (LC names/numbers commonly collide across variants), and captions /
  controlling-LC readouts cite "file · LC". The first file supplies
  geometry, components and strengths; other files must match its
  geometry exactly (verified at load) and may differ in component
  order/coverage (remapped by name, missing fields read as no-data).
- **Envelopes** (in-memory, never persisted): min / max / abs(max)
  across all primary LCs **of every loaded model**, each tracking
  **which model + LC controlled** every corner value, plus a **Global
  DSR** envelope (worst check across all LCs *and* all `kind:"dsr"`
  components) tracking the controlling check and controlling LC per
  corner. "By check" recolors the Global DSR view as a categorical
  controlling-check map.
- **Calc review**: pinning a point in a Global DSR view slices the
  controlling LC's element record straight from the file (a few hundred
  bytes) and shows every DSR check, the constituent stress components,
  and the corner's worst check per primary LC.
- **Deformed shape**: GPU-side (`position + dispScale * dispVec`), with
  a ±scale cycling animation. Requires the metadata to tag the
  translation components (see below). Picking is disabled while the
  deformed shape is shown.
- **Design strengths** (phi factors applied): LC-independent
  per-corner fields stored once in the
  file (see the metadata notes below), selectable from the component
  dropdown and summarized in the readout on every pin.
- **Display transforms**: |value| toggle (abs applied after
  interpolation, so interior zero crossings are exact), overstress
  alarm color with an x-ray **Flash** pulse to reveal obscured flagged
  regions, smoothing: **element means** (default since 2026-09-10: each element's mean of its
  own corners, averaged at the node, so a force profile at a shell edge reads without the
  corner extrapolation overshoot), coincident-node averaging, or none.
- **Navigation**: find element/node by real ID (moves the orbit focus
  and flashes the target on top of everything -- no zoom),
  orthographic projection + axis triad, load-case stepping (arrow
  keys), persistent active-view caption.

## Run

Just open `viewer/index.html` in a browser — no server needed. A synthetic
demo (v4: curved plate of shells + a steel beam frame with I / pipe / rect
sections) loads automatically; use the file picker to open a real
`.bin`. Binary files are read on demand through the file picker, so
multi-GB files never get materialized whole.

## Inspector

Open `viewer/inspector.html` to inspect a `.bin` field by field — header
fields with a raw hex dump, the range-checked block layout, metadata
tables, paged node/element/ID tables, and per-load-case field data
(per-component min/max/mean stats plus a per-element corner × component
matrix). It reuses the frozen `format/v3Reader.js`, so it can never disagree with the
viewer about the file layout. Use **Download Sample .bin** to emit a
test file from `sampleModel.js`, then open it back through the picker.

## Layout (`viewer/scripts/`)

| File | Role |
|------|------|
| `format/pluto.js` | format entry point: version dispatch, unified model, v3-shaped domain views, `FEABinary` compat |
| `format/v4Reader.js` | v4 block-directory reader |
| `format/v4Writer.js` | demo/test-only in-browser v4 writer (sample generator); production files come from `RawViewerWriter.cs` |
| `format/v3Reader.js` | frozen legacy v3 reader (adapted by `pluto.js`) |
| `modelSet.js` | multi-model set: geometry validation, global LC index, per-file read routing |
| `geometryBuilder.js` | duplicate-vertex shell mesh build, tri/quad triangulation |
| `beamGeometry.js` | extruded cross-section beam mesh (parametric sections), axis param for picking |
| `viewerBeams.js` | beam-domain display layered on viewer.js: own component/range, neutral in envelope views, pick readout |
| `attributeUpdaters.js` | rewrite `cornerVals` on component/LC swap (only per-update path) |
| `shaders.js` | bilinear vertex/fragment GLSL, colormap LUTs |
| `pointQuery.js` | raycast → inverse-bilinear → exact field eval |
| `sampleModel.js` | synthetic FEA binary generator + `.bin` download (demo / format reference) |
| `viewer.js` | scene wiring, render loop, UI, hover/pin readout |
| `features.js` | sidecar loader, Color by groups, legend, predicate hook |
| `predicates.js` | predicate flyout + engine (`FEAPredicates`) |
| `sectionCut.js` | section cuts: named list (groups, visibility, adjust, delete, numeric centre), probe + plot + isolate on the selected cut (this wall / slab, or a **crop box** of the cut length by a width along a secondary axis, 2026-09-10), sidecar round trip, legacy import (2026-09-04) |
| `live.js` | live mode (`index.html?live=1`, served by the Pluto MCP server): a WebSocket to the server; the LLM's viewer tools load a model (HTTP Range slices), pick component / load case, highlight and isolate objects, focus, and read the pinned readout. Inert without `?live` |
| `viewCube.js` | orientation cube |
| `inspector.html` / `inspector.js` | standalone field-by-field binary inspector |

Three.js r128 and OrbitControls are vendored under `lib/`.

## Recent additions (newest first)

Live mode (2026-10-12, `live.js`): the Pluto MCP server hosts the viewer on `http://localhost:<port>/viewer/index.html?live=1` and drives it over a WebSocket ([[vault/decisions/2026-10-12-llm-model-interface|LLM model interface]], phase 4). Commands go through the existing controls (component / LC dropdowns, Find, the groups switch, the section-cut `elemVis` attribute), so there is no second render path. A highlight is a sidecar group tagged `pluto-live`. Verified in a headless Edge (software WebGL) on the example tank.

Files (2026-09-04, `files.js`): one "Choose files…" for .bin + .json (File System Access
API on Chrome / Edge, hidden `<input type=file>` otherwise), one "Save features" (Ctrl+S)
that writes the sidecar back in place through the kept handle, or downloads; legacy
`section_cuts.json` / `predicates.json` route through the same picker. Edits mark the
features name `*`. Verified over localhost with the extension (first time it connected).

Groups tab (2026-09-03): the sidecar-groups UI moved out of the main panel into a flyout
tab under the predicates tab (`#grPanel`, `styles/groups.css`). Per-group enable
(`hidden` flag in the sidecar), colour edit, drag reorder, painted/members counts that
expose shadowing (e.g. WALL 0/720 under twenty COURSE groups), node groups on their own
**Nodes** sub-tab, drawn as coloured square points (`nodeMarkers`, one
THREE.Points, depth-tested, 2026-09-04; undeformed positions; coincident markers are nudged
aside in list order so all show, rows count the nudged ones; orbs on top were tried and
rejected as clutter), every group off until ticked (`hidden: false`), All/None/Invert per
sub-tab, Export. Spec: [[vault/format/features-sidecar|features-sidecar]];
test `viewer/tests/test_groups.js`. URL load `index.html?bin=…&features=…` (same day).

Predicates (2026-09-01, ported from the old viewer — plan:
[[vault/viewer/old-viewer-merge-plan|old-viewer-merge-plan]]):
- `scripts/predicates.js` (`FEAPredicates`), flyout tab under the section-cut tab.
- Trees of plane / finite-plane slab tests (AND/OR, per-node negate), evaluated over
  shell centroids+normals, beam midpoints (no normal test; a beam pick takes its axis
  as the plane normal), and nodes. Drag-drop tree editing, inspector with scroll-nudge,
  match highlighting, finite-extents ghost.
- Stored in the features sidecar `predicates` section in the **production dialect**
  (`kind`/`tol`/`normal_tol_deg`/`negated` — what C# `Groups.cs` parses); points are
  world/plant coordinates (both recenter offsets subtracted at eval).
- `resolveMembers(predicateId)` feeds `features.js` groups → **Group from pred.** button
  colors a predicate's members via Color by groups; Export downloads the whole sidecar;
  Import accepts a sidecar or a legacy `predicates.json` (v1/v3).
- Known old-viewer bugs fixed at port (idx typo, cosNormTol write, overlay tri count);
  undo/redo was never implemented in the old code — still open.

Beam support (2026-08-25, first cut):
- Beam domain renders as solid extruded sections (`beamGeometry.js`), linear end-A→end-B color interpolation (`FEAShaders.beamFragment`).
- Own component dropdown + auto range in the **Beams** panel; shares colormap / abs / alarm with shells.
- Envelope, strength and Global-DSR views draw beams neutral grey — no beam envelopes yet.
- Hover/pick reports beam ID, section, `t` along the axis, end node IDs.
- Deformed shape: beam ends follow the beam domain's displacementVector; shares the shell scale.
- Hollow sections (BOX, PIPE) render outer outline only.

Not yet: beam envelopes / min-max folds, a beam calc card, line-only mode for large models, `nStations > 2` rendering (format supports it; updater reads slots 0 and 1).

Geometry-only / beam-only files (2026-08-25): a file with no shell domain loads through a
synthetic empty shell view; zero load cases short-circuits `selectLC` (beams draw neutral,
groups/labels still work). Pipe facet count is adaptive (24 → 12 → 8 → 6 by beam count) so
~150k pipes stay renderable; a line-only mode is still the next step for very large models.
