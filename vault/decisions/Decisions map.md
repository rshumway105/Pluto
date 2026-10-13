# Decisions map

*↑ [[vault/Pluto Home|Home]]*

Hub for `vault/decisions/` — one dated note per design decision. Nothing lives here yet;
decisions so far are recorded inline: the v4 format decisions log in
[[vault/format/v4-schema|v4-schema]], the predicate dialect in
[[vault/format/features-sidecar|features-sidecar]], and the per-session decisions in the
the handoff log (Notes vault, `Tools/Pluto Handoffs`). Start filing new ones here as `YYYY-MM-DD-<slug>.md`.

- [[vault/decisions/2026-10-12-llm-model-interface|2026-10-12-llm-model-interface]] — plan, built (phases 0–5, server 0.6.0): one MCP server (`PlutoMcp.exe`, C# 5 on the SAP controller) so an LLM client can read, edit, run and query a SAP2000 26 model and drive the viewer; working copies, checkpoints, change log. Usage: [[vault/arms/pluto-mcp|pluto-mcp]].
- [[vault/decisions/2026-10-13-qa-records-and-bod-map|2026-10-13-qa-records-and-bod-map]] — plan, built (server 0.8.0–0.12.0): a design record per Pluto session (model basis, assumptions and conditions with their basis, changes with reasons, run checks) written by `write_design_record` into an Obsidian project vault, linked to one BOD element note per design element.
