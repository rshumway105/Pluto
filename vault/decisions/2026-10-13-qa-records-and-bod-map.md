---
title: Design records and basis-of-design map — plan
status: draft
created: 2026-10-13
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

# Design records and basis-of-design map — plan

**Goal:** make the design process readable. Anyone opening the project in Obsidian can see what a
model is, what it assumes, what conditions it rests on, what was changed and why, and how its
results were checked, without opening SAP2000 or asking the engineer. QA/QC here means
**transparency of the design process**, not a sign-off workflow.

**Decisions (2026-10-13, the user's):**

| Question | Decision |
|---|---|
| Unit of record | One **BOD element note** per design element + one **design record** per Pluto session, linking up to the element note(s) it touched |
| Where | Plain Markdown in an Obsidian vault (properties, links, backlinks, Bases views). A server tool writes into the folder `PLUTO_NOTES_DIR` names |
| Emphasis | Assumptions, underlying conditions, decisions and their reasons. Sign-off is minor: one optional `status` property |
| Automatic checks | Run health (case status, warnings, equilibrium) and hand-check fixtures (tool verification) |

**Where things live.** Real records are project information: a project vault, never Pluto. Pluto
holds the tools, generic templates and the fixtures. Open: a shared team vault (network drive,
synced folder, Obsidian Sync or git) vs the personal Notes vault.

> [!info]- 1. Capturing the why (new in the server)
> The record is only as clear as what the session wrote down, so the server captures intent as it goes:
> - **`reason` on every edit tool** (optional text, logged in `changes.jsonl` with the edit).
>   The server's instructions ask the LLM to pass the user's reason, or ask for one, on edits.
> - **`log_note`** — a new tool appending to `<session dir>\notes.jsonl`:
>   `kind` (assumption | condition | decision | question), `text`, `basis` (code clause,
>   drawing, report, engineering judgement), `topic` (geometry, supports, materials, sections,
>   loads, analysis, results), optional `elements` and `objects` (group / names it concerns).
>   A later note can `supersede` an earlier one; nothing is deleted.
> - `list_notes` reads them back (for the LLM and for the record).

> [!info]- 2. Design record (one per session)
> File: `<PLUTO_NOTES_DIR>\Records\<session id>.md`, written by `write_design_record`
> (arguments: `session`, `elements`, `title`, `purpose`). Re-running rewrites the generated
> sections only; text between `<!-- pluto:notes -->` markers is kept.
>
> Properties: `type: design-record`, `elements`, `model`, `session`, `program` + version,
> `date`, `author`, `status` (optional, e.g. draft / reviewed).
>
> Sections:
> 1. **Purpose** — what the session set out to do (argument + marked narrative).
> 2. **Model basis** — read from the model, not typed: units, materials used (key values),
>    sections by use, supports (restraint types with counts and groups), releases,
>    load patterns → cases → combinations, analysis types (static, modal, RS, TH, P-Δ,
>    nonlinear), mass source.
> 3. **Assumptions and conditions** — from `log_note`, grouped by topic, each with its basis;
>    superseded ones shown struck through with what replaced them.
> 4. **Decisions and changes** — edits from `changes.jsonl` summarised one line each
>    (what, how many objects, reason, checkpoint), with logged decisions alongside.
> 5. **Analysis and checks** — per run checkpoint: case status, log warnings, equilibrium; what
>    changed between runs (`compare_runs` headline) when there are several.
> 6. **Tool verification** — the latest hand-check fixture run for this program version (one line, pass / fail, link).
> 7. **Open questions** — `question` notes not resolved, cases not finished, out-of-balance checks, missing fixtures.
> 8. **Session** — link to the session folder (checkpoints, raw logs).

> [!info]- 3. BOD element note (one per design element)
> Written from a Pluto template (`vault/_meta/templates/bod-element.md`, generic); Pluto never
> edits one. Sections: description, design criteria (codes, limits), loads, conditions and
> assumptions that hold for the element, models, governing checks. Its design records show as
> backlinks. A project-level **Bases** view (template `design-records.base`) tables every record
> and note with its element, topic and status. Later: `draft_bod_element` prefills loads and
> model facts from the model.

> [!info]- 4. Hand-check fixtures (tool verification)
> Promote the 2026-10-12 scratch fixtures into `scripts/mcp/fixtures/` with a `-Fixtures`
> switch on `Test-PlutoMcp.ps1` (own SAP2000): plate (weight × 2, E × 2), area loads, response
> spectrum (one-mass cantilever), linear modal time history. Each run writes `fixtures.json`
> (program version, date, per check: hand, program, tolerance, pass).

> [!info]- 5. Build order
> 1. ~~`reason` on edits + `log_note` / `list_notes`~~ — built 2026-10-13, server 0.8.0 (tested in `-Edit`).
> 2. ~~Model basis extraction~~ — built 2026-10-13, server 0.9.0: `describe_basis` (`SapRead.Basis`), checked on the beam (`-Edit`) and on the one-mass cantilever's modal, RS and modal TH cases.
> 3. ~~`write_design_record` + templates~~ — built 2026-10-13, server 0.10.0: `DesignRecord.cs`; templates in
>    `vault/_meta/templates/` (`bod-element.md`, `design-records.base`). Tested in `-Edit` (content, rewrite keeps hand
>    text and properties) and on the one-mass cantilever into a sandbox vault. The `file.hasLink(this.file)` filter in
>    the element template's embedded base is not yet checked in Obsidian.
> 4. Fixtures into the repo + `fixtures.json`.
> 5. Usage lines in [[vault/arms/pluto-mcp|pluto-mcp]]; this note → `current`.
