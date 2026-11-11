# XOD++ - docs

The project's memory. `xod2` is the active rework of XOD; `xod` and `xod.core` are the originals.

## Lists of record
- [MASTER-TASKS.md](MASTER-TASKS.md) - task tracker (TASK-NNN)
- [KNOWN-ISSUES.md](KNOWN-ISSUES.md) - issues (ISS-NNN)
- [plans/](plans/) - one plan per task (PLAN-NNN)
- [MIGRATION-GUIDE.md](MIGRATION-GUIDE.md) - 1.x -> 2.0 migration

## Direction
- [PLAN-044](plans/PLAN-044-roadmap-sql-analytics-rust-browser.md) - roadmap (proposed): what makes XOD special, who it suits, and the order for SQL and analytics, a Rust core, WASM and the browser, and the packed file
- [PLAN-045](plans/PLAN-045-query-language-spec.md) - XOD SQL, the query-language spec (accepted 2026-11-11): the SQL subset, object-model paths, type mapping, writes, and decisions D1-D8
- [PLAN-046](plans/PLAN-046-file-format-spec.md) - the XOD file format (accepted 2026-11-11): folder, encodings, encryption, pages and rows, scalar forms, the index, the journal; the schema manifest (E1-E3 decided); defects ISS-050 to ISS-055

## Repos
- `xod2/` - XOD++ (local git, no remote)
- `xod/`, `xod.core/` - originals