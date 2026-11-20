# XOD++ - docs

The project's memory. `xod2` is the active rework of XOD; `xod` and `xod.core` are the originals.

## Lists of record
- [MASTER-TASKS.md](MASTER-TASKS.md) - task tracker (TASK-NNN)
- [KNOWN-ISSUES.md](KNOWN-ISSUES.md) - issues (ISS-NNN)
- [plans/](plans/) - one plan per task (PLAN-NNN)
- [MIGRATION-GUIDE.md](MIGRATION-GUIDE.md) - 1.x -> 2.0 migration

## Reviews
- [REVIEW-2026-11-14](REVIEW-2026-11-14-overall.md) - the overall review of XOD at 7ac53ae (TASK-073): the verdict, 30 defects filed (ISS-129 to ISS-158, eight High), what is good, what holds it back, what would make the rules enforce themselves, and the fix order

## Direction
- [PLAN-044](plans/PLAN-044-roadmap-sql-analytics-rust-browser.md) - roadmap (proposed): what makes XOD special, who it suits, and the order for SQL and analytics, a Rust core, WASM and the browser, and the packed file
- [PLAN-045](plans/PLAN-045-query-language-spec.md) - XOD SQL, the query-language spec (accepted 2026-11-11): the SQL subset, object-model paths, type mapping, writes, and decisions D1-D8
- [PLAN-046](plans/PLAN-046-file-format-spec.md) - the XOD file format (accepted 2026-11-11): folder, encodings, encryption, pages and rows, scalar forms, the index, the journal; the schema manifest (E1-E3 decided); defects ISS-050 to ISS-055 (ISS-051 to ISS-055 fixed in PLAN-048)
- [PLAN-047](plans/PLAN-047-conformance-set.md) - the XOD conformance set (built 2026-11-11): dataset and fixtures, 139 query cases, the C# runner, the independent file reader, known-defect markers; G1-G3 decided
- [PLAN-048](plans/PLAN-048-format-defect-fixes.md) - fixes for the format defects (2026-11-11, not released): keys v2, one XML character policy, the autonumber mark, the row built before the table changes
- [PLAN-082](plans/PLAN-082-a-collection-xod-cant-read-back-is-refused.md) - a collection XOD can't read back is refused (2026-11-18, done): ISS-184 resolved; ISS-201 filed
- [PLAN-083](plans/PLAN-083-a-context-owns-its-file-keys.md) - a context owns its file keys (2026-11-18, done): ISS-152 resolved; ISS-205, ISS-206 filed
- [PLAN-084](plans/PLAN-084-every-file-written-whole-goes-through-its-tmp.md) - every file written whole goes through its .tmp (2026-11-18, done): ISS-134 resolved
- [PLAN-085](plans/PLAN-085-a-moved-child-leaves-the-parent-its-row-names.md) - a child's parent is the one its row and its list say (2026-11-18, done): ISS-202, ISS-203 resolved; ISS-207, ISS-208 filed
- [PLAN-086](plans/PLAN-086-an-update-filter-takes-a-relation-and-its-keys-together.md) - an update filter takes a relation and its keys together (2026-11-20, done): ISS-207 resolved
- [PLAN-087](plans/PLAN-087-a-page-is-on-disk-while-a-table-names-it.md) - a page is on disk while a table names it (2026-11-20, done): ISS-196 resolved; ISS-209, ISS-210 filed
- [PLAN-088](plans/PLAN-088-a-page-no-table-names-is-deleted-at-the-open.md) - a page a crash left unnamed is deleted at the next open; a relation's code that finds no row reads as gone (2026-11-20, done): ISS-209 resolved, ISS-210 closed by design; ISS-211 filed
- [PLAN-089](plans/PLAN-089-page-bytes-stay-a-readers-and-a-read-digest-stays-a-digest.md) - a page's bytes stay a reader's; a 1.x digest read from a database said to hold them stays a digest (2026-11-20, done): ISS-105, ISS-171 resolved; ISS-212 filed
- [PLAN-090](plans/PLAN-090-four-small-fixes-stray-files-interface-maps-snapshot-failures-guid-ids.md) - four small fixes: stray files, maps declared by interface, a snapshot's failure, a `Guid?` key (2026-11-20, done): ISS-182, ISS-128, ISS-198, ISS-123 resolved; ISS-213, ISS-214 filed
- [PLAN-081](plans/PLAN-081-a-child-s-entry-is-found-by-its-row.md) - a child's entry in its parent's list is found by its row (2026-11-18, done): ISS-135 resolved; ISS-202 to ISS-204 filed
- [PLAN-080](plans/PLAN-080-what-a-parent-s-update-writes-into-related-rows.md) - what a parent's update writes into the rows it names (2026-11-18, done): ISS-189 and ISS-181 resolved; ISS-200 filed
- [PLAN-079](plans/PLAN-079-a-key-part-at-its-default-is-no-value.md) - a key part at its default is no value (2026-11-18, done): ISS-188 resolved; ISS-199 filed
- [PLAN-078](plans/PLAN-078-a-page-s-rows-found-by-one-reader.md) - a page's rows found by one reader, and a row that can't be read is an error (2026-11-18, done): ISS-143 and ISS-144 resolved; ISS-195 to ISS-198 filed
- [PLAN-077](plans/PLAN-077-one-rule-for-the-rows-a-row-owns.md) - one rule for the rows a row owns (2026-11-18, done): ISS-187 resolved; ISS-193 and ISS-194 filed
- [PLAN-076](plans/PLAN-076-an-update-with-keyless-children-and-unwritten-tables.md) - an update of a parent with keyless children, and a table not written yet (2026-11-18, done): ISS-179 and ISS-192 resolved; ISS-189 to ISS-191 filed
- [PLAN-075](plans/PLAN-075-a-keyless-update-or-delete-reaches-every-page.md) - an update or delete by example of a keyless type reaches every page (2026-11-18, done): ISS-136 resolved; ISS-188 filed
- [PLAN-074](plans/PLAN-074-a-row-is-deleted-by-its-code.md) - a row is deleted by its code, with one cascade (2026-11-18, done): ISS-130, ISS-180, ISS-185, ISS-186 resolved; ISS-187 filed
- [PLAN-073](plans/PLAN-073-a-value-xod-cant-store-is-refused.md) - a value XOD can't store is refused, not lost (2026-11-18, done): ISS-142 resolved; ISS-184 filed
- [PLAN-072](plans/PLAN-072-a-char-reads-back-as-written.md) - a char reads back as written (2026-11-18, done): ISS-140 resolved; ISS-183 filed
- [RELEASE-TRIAGE-2026-11-18](RELEASE-TRIAGE-2026-11-18.md) - what blocks the next stable release, in order, and what waits; the decisions on ISS-188 and ISS-191
- [VERIFY-2026-11-18](VERIFY-2026-11-18-open-issues.md) - every open issue checked against the code with probes (2026-11-18): 2 closed, 4 filed (ISS-179 to ISS-182), texts corrected
- [PLAN-071](plans/PLAN-071-the-gate-runs-itself.md) - the gate in the repository, and warnings that fail the build (2026-11-18, done): ISS-158 resolved in part
- [PLAN-070](plans/PLAN-070-delete-where-and-drop-all-are-one-write.md) - DeleteWhere and DropAll are one write (2026-11-17, done): ISS-176 resolved in direct mode
- [PLAN-069](plans/PLAN-069-a-refused-drop-keeps-its-table.md) - a refused drop keeps its table (2026-11-17, done): ISS-178 resolved in direct mode
- [PLAN-068](plans/PLAN-068-a-failed-statement-writes-nothing-of-its-batch.md) - a failed statement writes nothing of its batch (2026-11-17, done): ISS-177 resolved
- [PLAN-067](plans/PLAN-067-a-write-statement-is-all-or-nothing.md) - a write statement outside a transaction is all or nothing (2026-11-17, done): ISS-125 resolved (a savepoint per statement in direct mode; one write at a time); ISS-176, ISS-177 and ISS-178 filed
- [PLAN-066](plans/PLAN-066-reads-wait-for-another-flow-s-transaction.md) - reads wait for another flow's transaction (2026-11-17, done): ISS-175 resolved (a read takes the same turn as a write)
- [PLAN-065](plans/PLAN-065-a-transaction-is-its-flow-s.md) - a transaction is its flow's (2026-11-16, done): ISS-132 resolved (another thread's write waits for the transaction instead of joining it); ISS-175 filed
- [PLAN-064](plans/PLAN-064-one-context-per-database.md) - a database open in one context at a time (2026-11-16, done): every open holds it alone; twelve issues of contexts side by side resolved; the code that only served them taken out before the release
- [PLAN-063](plans/PLAN-063-deletes-at-the-commit.md) - a transaction's deletes at its commit (2026-11-16, done): ISS-166 fixed (nothing set aside; a dropped table stays whole for other contexts until the commit; a rollback touches no file)
- [PLAN-062](plans/PLAN-062-commit-gate.md) - the commit gate (2026-11-16, done; superseded by PLAN-064, its code taken out before any release): ISS-164 resolved in part (writes kept apart from other contexts' commits; a commit refused over a newer write; a rollback writes nothing); ISS-166 reproduced; ISS-167 and ISS-104 extended; ISS-172 filed
- [PLAN-061](plans/PLAN-061-high-issues-from-the-overall-review.md) - the High issues from the overall review (2026-11-15, done; superseded in part by PLAN-064): ISS-147 mitigated (the journal held by its commit; other contexts wait for one under way and refuse beside one that won't finish), ISS-138 and ISS-139 resolved (a read's object cache that ends with the read; one match by example by index keys); ISS-148 to ISS-150 resolved (an open beside an unfinished password change refuses; the gate xod.gate.lock around every hold alone; one list of what a crash leaves; the AES256 key made only with the database held alone); ISS-129 resolved (one writer for a batch's pages; a document's early write leaves the batch only once made); ISS-151 resolved (encrypted 1.x databases open; an update keeps a 1.x digest; the digests a mitigation); ISS-164 to ISS-171 filed
- [PLAN-060](plans/PLAN-060-xod-sql-select-manifest-cli.md) - XOD SQL version 1 (2026-11-14, done): `SELECT` in .NET (`db.Sql`) and the CLI (`xod query`), the schema manifest written at a type's first write; ships in 2.0.2, not on NuGet until the owner says
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
- [RELEASE-NOTES-2.1.0](RELEASE-NOTES-2.1.0.md) - what 2.1.0 changed (released 2026-11-18)
- [RELEASE-NOTES-2.0.3](RELEASE-NOTES-2.0.3.md) - what 2.0.3 changed (released 2026-11-16)
- [RELEASE-NOTES-2.0.2](RELEASE-NOTES-2.0.2.md) - what 2.0.2 changed (released 2026-11-14)
- [PLAN-050](plans/PLAN-050-password-change.md) - a password change that is all or nothing (2026-11-12, not released): ISS-065 to ISS-068
- [PLAN-049](plans/PLAN-049-property-encryption.md) - property encryption with a key of the database's own (2026-11-11, not released): ISS-050, ISS-058, ISS-063, ISS-064

## Repos
- `xod2/` - XOD++ (GitHub `mhsallam/xod`)
- `xod/`, `xod.core/` - originals