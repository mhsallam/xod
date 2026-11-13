# XOD++ - docs

The project's memory. `xod2` is the active rework of XOD; `xod` and `xod.core` are the originals.

## Lists of record
- [MASTER-TASKS.md](MASTER-TASKS.md) - task tracker (TASK-NNN)
- [KNOWN-ISSUES.md](KNOWN-ISSUES.md) - issues (ISS-NNN)
- [plans/](plans/) - one plan per task (PLAN-NNN)
- [MIGRATION-GUIDE.md](MIGRATION-GUIDE.md) - 1.x -> 2.0 migration

## Reviews
- [REVIEW-2026-11-14](REVIEW-2026-11-14-overall.md) - the overall review of XOD at c614e30 (TASK-073): the verdict, 30 defects filed (ISS-129 to ISS-158, eight High), what is good, what holds it back, what would make the rules enforce themselves, and the fix order

## Direction
- [PLAN-044](plans/PLAN-044-roadmap-sql-analytics-rust-browser.md) - roadmap (proposed): what makes XOD special, who it suits, and the order for SQL and analytics, a Rust core, WASM and the browser, and the packed file
- [PLAN-045](plans/PLAN-045-query-language-spec.md) - XOD SQL, the query-language spec (accepted 2026-11-11): the SQL subset, object-model paths, type mapping, writes, and decisions D1-D8
- [PLAN-046](plans/PLAN-046-file-format-spec.md) - the XOD file format (accepted 2026-11-11): folder, encodings, encryption, pages and rows, scalar forms, the index, the journal; the schema manifest (E1-E3 decided); defects ISS-050 to ISS-055 (ISS-051 to ISS-055 fixed in PLAN-048)
- [PLAN-047](plans/PLAN-047-conformance-set.md) - the XOD conformance set (built 2026-11-11): dataset and fixtures, 139 query cases, the C# runner, the independent file reader, known-defect markers; G1-G3 decided
- [PLAN-048](plans/PLAN-048-format-defect-fixes.md) - fixes for the format defects (2026-11-11, not released): keys v2, one XML character policy, the autonumber mark, the row built before the table changes
- [PLAN-059](plans/PLAN-059-one-database-per-folder-recorded-format-value-collections.md) - one database per folder against two opens at once, the database's recorded format, maps and lists that read back as written (2026-11-13, not released): ISS-096, ISS-093, ISS-097; filed ISS-127, ISS-128
- [PLAN-058](plans/PLAN-058-standalone-undo-held-index-files-insert-range-index-save.md) - a standalone table's failed insert taken back, a commit's undo past held index files, an InsertRange's index save left to the flush (2026-11-13, not released; revised after its review): ISS-120, ISS-110, ISS-115; filed ISS-124 to ISS-126
- [PLAN-057](plans/PLAN-057-review-of-task-066-direct-writes-triggers-transactions.md) - the review of TASK-066 (2026-11-13, not released): a direct write that failed stays to be made, written when its statement ends; no index file written inside a transaction; flushes and closes from triggers; a held crash marker; a close racing a write; a transaction's deletes set aside until its commit; the regressions its own review found: ISS-099 to ISS-103, ISS-107, ISS-109; filed ISS-119 to ISS-121
- [PLAN-056](plans/PLAN-056-reads-that-scale-and-writes-that-dont-stall-them.md) - why reads barely scale with threads and what a committing writer costs them: the read path's exclusive locks, index snapshots from re-keying unchanged keys, a page's row map dropped when a row grows (2026-11-12, investigated; the writer-side fixes in the tree 2026-11-13, read scaling proposed): ISS-044, ISS-105, ISS-106, ISS-108, ISS-112 to ISS-114
- [PLAN-055](plans/PLAN-055-index-files-replaced-or-held.md) - index files another context replaced, or another program holds: a generation checked at each block read, unread blocks built from the pages in place, a rebuilt index saved best-effort, a file that can't be deleted left untrusted (2026-11-12, not released; revised after its review): ISS-042; filed ISS-109 to ISS-111
- [PLAN-054](plans/PLAN-054-indexes-left-behind-by-a-crash.md) - indexes a crash left behind their pages are dropped, not trusted; a write that failed stays to be made (2026-11-12, not released): ISS-043, ISS-098
- [PLAN-053](plans/PLAN-053-statement-undo-counters-stored-values-locks.md) - a failed statement's rows removed, counters kept across a rollback, values that read back as written, and the lock fixes from the reviews (2026-11-12, not released): ISS-060, ISS-062 (mitigated), ISS-083 to ISS-088, ISS-091
- [PLAN-052](plans/PLAN-052-autonumbers-password-service-migrator.md) - autonumber settings and updates, one password writer, a migrator that is all or nothing (2026-11-12, not released; the Guid autonumber regression ISS-117 fixed 2026-11-13, section 8): ISS-047, ISS-048, ISS-059, ISS-069, ISS-072, ISS-075, ISS-080 to ISS-082, ISS-117
- [PLAN-051](plans/PLAN-051-commit-failure-and-shared-access.md) - a failed commit undone at once; a database a password change has to itself (2026-11-12, not released): ISS-070, ISS-071, ISS-073, ISS-074, ISS-076 to ISS-079
- [RELEASE-NOTES-NEXT](RELEASE-NOTES-NEXT.md) - behaviour changes waiting for the next release
- [PLAN-050](plans/PLAN-050-password-change.md) - a password change that is all or nothing (2026-11-12, not released): ISS-065 to ISS-068
- [PLAN-049](plans/PLAN-049-property-encryption.md) - property encryption with a key of the database's own (2026-11-11, not released): ISS-050, ISS-058, ISS-063, ISS-064

## Repos
- `xod2/` - XOD++ (local git, no remote)
- `xod/`, `xod.core/` - originals