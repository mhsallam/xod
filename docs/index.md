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
- [PLAN-046](plans/PLAN-046-file-format-spec.md) - the XOD file format (accepted 2026-11-11): folder, encodings, encryption, pages and rows, scalar forms, the index, the journal; the schema manifest (E1-E3 decided); defects ISS-050 to ISS-055 (ISS-051 to ISS-055 fixed in PLAN-048)
- [PLAN-047](plans/PLAN-047-conformance-set.md) - the XOD conformance set (built 2026-11-11): dataset and fixtures, 139 query cases, the C# runner, the independent file reader, known-defect markers; G1-G3 decided
- [PLAN-048](plans/PLAN-048-format-defect-fixes.md) - fixes for the format defects (2026-11-11, not released): keys v2, one XML character policy, the autonumber mark, the row built before the table changes
- [PLAN-054](plans/PLAN-054-indexes-left-behind-by-a-crash.md) - indexes a crash left behind their pages are dropped, not trusted; a write that failed stays to be made (2026-11-12, not released): ISS-043, ISS-098
- [PLAN-053](plans/PLAN-053-statement-undo-counters-stored-values-locks.md) - a failed statement's rows removed, counters kept across a rollback, values that read back as written, and the lock fixes from the reviews (2026-11-12, not released): ISS-060, ISS-062 (mitigated), ISS-083 to ISS-088, ISS-091
- [PLAN-052](plans/PLAN-052-autonumbers-password-service-migrator.md) - autonumber settings and updates, one password writer, a migrator that is all or nothing (2026-11-12, not released): ISS-047, ISS-048, ISS-059, ISS-069, ISS-072, ISS-075, ISS-080 to ISS-082
- [PLAN-051](plans/PLAN-051-commit-failure-and-shared-access.md) - a failed commit undone at once; a database a password change has to itself (2026-11-12, not released): ISS-070, ISS-071, ISS-073, ISS-074, ISS-076 to ISS-079
- [RELEASE-NOTES-NEXT](RELEASE-NOTES-NEXT.md) - behaviour changes waiting for the next release
- [PLAN-050](plans/PLAN-050-password-change.md) - a password change that is all or nothing (2026-11-12, not released): ISS-065 to ISS-068
- [PLAN-049](plans/PLAN-049-property-encryption.md) - property encryption with a key of the database's own (2026-11-11, not released): ISS-050, ISS-058, ISS-063, ISS-064

## Repos
- `xod2/` - XOD++ (local git, no remote)
- `xod/`, `xod.core/` - originals