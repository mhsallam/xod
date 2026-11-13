# The overall review of XOD (2026-11-14)

Of the committed tree at `c614e30` (TASK-071), not of the work other sessions had in the tree that day. Asked by
the owner on 2026-11-13: "fix all the open issues ... and then give XOD an overall review".

**Verdict.** XOD's records are honest and its crash handling for a single context is careful. What makes it unsafe
today is how many places decide the same thing. Values are read by six readers, rows are deleted by two paths,
pages are written in four ways, and "what a crash left" is defined three times. On top of that, caches don't learn
about writes, and state that contexts share lives in one process's memory. Most of the 30 new defects below come
from those few causes. Eight of them are High. Seven lose, hide or corrupt data: a stale reference after an
update and a query by example that answers differently from the index (both on ordinary use, in one context), a
crashed commit undoing later writes, a password change that leaves files under two passwords (two ways), and two
races (one corrupting pages, one losing a key). The eighth locks out encrypted databases from XOD 1.x. The rules hold by convention and review, not by structure,
and the gate isn't run by anything but hand.

## 1. How it was done

- **Measured** here: size, the build's settings, escape hatches, the gate (section 5).
- **Three independent reviewers**, each given one slice of the committed tree, the questions of the engineering
  standard (cause or compensation, second copies, dependency claims, what still reaches the bad state), the issue
  list, and no conclusion: A, the write path (statements, transactions, the page writers); B, reads, indexes and
  the type model; C, shared access, crash markers, security, conversion and the public API. Each worked in its own
  copy and wrote probes for what it suspected.
- **Verified** here before filing: every probe of B and C was run again and failed as reported; seven of A's nine
  probes failed as reported. A's P8 (a delete by example across pages) passed here, but B's S8 and the code show
  the same defect. A's P9 (a page corrupted by an `InsertRange`) passed in 36 runs on a quiet machine; the
  mechanism was then read in the code and holds (ISS-129). Claims made by reading are marked so in each issue.
- **Not re-filed:** a finding already listed (`ISS-118`, `[DefaultValue]` has no effect; `ISS-105`, a reader
  parsing a buffer another load gave back) is cited, not filed again. Both were filed in another session's work,
  not yet committed on 2026-11-14.

## 2. Defects (filed as ISS-129 to ISS-158)

Most severe first; each row in [KNOWN-ISSUES](KNOWN-ISSUES.md) has the place, the scenario, the evidence and a fix
direction.

| Issue | Severity | What goes wrong |
|---|---|---|
| ISS-138 | High | a referenced object is served stale from the item cache after an update or delete, in the same context |
| ISS-139 | High | a query, update or delete by example compares text culture-insensitively, unlike the index; without indexing, key checks are wrong too |
| ISS-147 | High | a journal a crashed commit left is written back by the next open alone, over writes other contexts made since |
| ISS-148 | High | a password switch left pending lets another context open with the old password and write under it |
| ISS-149 | High | pages a crashed transaction set aside survive a password change, then come back under the old one |
| ISS-150 | High | two contexts creating the data key for `[Crypto(AES256)]` at once: one context's values become unreadable |
| ISS-129 | High | an `InsertRange` writes a full page from a pooled buffer another path gives back meanwhile: pages corrupted (a race) |
| ISS-151 | High (1.x users) | encrypted XOD 1.x databases don't open; 1.x `[Crypto]` digests are hashed again by an update |
| ISS-130 | Medium | a delete by key and a delete by scan cascade differently (a reference is orphaned; no child triggers) |
| ISS-131 | Medium | a delete that fails part-way leaves its row unindexed: a duplicate unique key is accepted and kept |
| ISS-132 | Medium | another thread's write joins a transaction and is rolled back after success was reported |
| ISS-133 | Medium | an insert that can't read its page faults the context until it is reopened |
| ISS-134 | Medium | two page writes aren't atomic (truncate in place; a new page created directly) |
| ISS-135 | Medium | a keyless child inserted on its own takes a sibling's place in its parent's list |
| ISS-136 | Medium | an update or delete by example of a keyless type touches the first page only |
| ISS-140 | Medium | a whitespace `char` reads back as `'\0'`; its index key differs by source |
| ISS-141 | Medium | a reader beside a writer: an indexed query returns rows that fail its predicate |
| ISS-142 | Medium | value types outside the spec (`Int128`, `Half`, `BigInteger`, structs) are accepted and silently lost |
| ISS-143 | Medium | a `[Markup]` value holding `</Row>` breaks the raw row scanners: rows dropped, or one forged |
| ISS-144 | Medium | read failures are swallowed: a row that fails to parse, decrypt or read reads as absent |
| ISS-152 | Medium | with a password, each write derives a key afresh into a process-wide cache never emptied |
| ISS-155 | Medium | a password change lets go of the shared hold before it flushes (by reading) |
| ISS-157 | Medium | README, the package and the test plan claim more than the code does (atomic after a kill, "ACID", test counts, "B-Tree") |
| ISS-158 | Medium | the gate isn't run by anything; the build checks little |
| ISS-137 | Low | the exception for a held file depends on how many files a statement saved |
| ISS-145 | Low | an indexed query returns rows in hash order, not stored order |
| ISS-146 | Low | the readers disagree on files XOD didn't write (an empty element; a bool stored as 1) |
| ISS-153 | Low | any `*.tmp` in the folder is taken for a crash's leftover (a user's file renamed or deleted) |
| ISS-154 | Low | a context that holds no lock still creates the database file |
| ISS-156 | Low | small API defects (an undefined format accepted, a misleading lock message, lock files left, passwords as CLI arguments, public options that do nothing) |

Suspicions not filed: the item cache is keyed by type and row code, which is unique only within a page (B, by
reading); a rollback rewrites every before-image though nothing reached the disk (A, by reading; `ISS-121`(4)
discusses it).

## 3. What is good, with evidence

- **The records are honest about status.** KNOWN-ISSUES separates Fixed, Mitigated and Open (reviewer A), and
  the plans name their debt. But two debts that lose data were named only in plans, not in the list of record
  (PLAN-051 section 7, the journal; PLAN-049, the data key; reviewer C): they are ISS-147 and ISS-150 now.
- **Crash tests are deterministic.** Fault steps (`XOD_CRASH_AT_STEP`, `XOD_PAUSE_AT_STEP`, `XOD_FAIL_AT_STEP`)
  and a separate writer process land a crash or a pause at an exact point. All three reviewers used them for their
  probes.
- **The commit protocol is sound for one context's crash.** The journal is written whole, then renamed. A commit is
  refused while another journal exists. One restore routine serves a rollback, a failed commit and an open.
- **Holds have one taker.** `OpenLockFile` takes the folder's lock, then the database file's, in one order.
  `ProveExclusive` refuses an exclusive lock the file system didn't grant (NFS). Since TASK-071, a database file is
  created only with the folder held alone (a race probe: one open went on 300 times out of 300, and the other
  created nothing).
- **The index file checks itself.** Generations, a log that counts only when closed by its flush record, and
  rebuilds of any other layout. `IndexKeys` is one exact, order-preserving key encoder. `XodIndexSemanticsTests`
  checks indexed answers against the predicate over edge values.
- **The file encryption is sound in construction.** AES-256-GCM, a fresh salt and nonce for each write, data keys
  wrapped per protector with associated data. The .NET API use was checked by reading, not against the docs, so
  that part is unconfirmed.
- **There is an independent reader.** `conformance/format/read.mjs` reads every fixture, and data written fresh,
  without the library.
- **Each fix in the last tasks was proven by breaking its guard** (the plans' "each guard broken" tables), and the
  gate was green on Windows and Linux for each commit.

## 4. What holds it back

| Issue | Evidence | Why it matters |
|---|---|---|
| One decision, several copies | Six interpreters of a stored scalar (`Read`, the span reader, `StoredScalar`, `SetFromStoredScalar`, `ReadBackFromRow`, `ElementMatch`); two delete and cascade paths; four page-write forms plus the index's own writer; three definitions of what a crash left; eleven row scanners by byte pattern; six copies of item-code parsing | A fix lands on one copy (ISS-130, 139, 140, 146, 149, 153): most of the new defects are two copies disagreeing |
| Caches with no invalidation contract | The page cache, the item cache and the in-memory index don't check the disk; the item cache skips standalone types when it clears | Stale reads in one context (ISS-138) and across contexts (ISS-141) |
| State shared between contexts lives in one process's memory | A pending password switch, the data key, a commit's journal: nothing tells another context whether they are live | ISS-147, 148, 150 |
| Pooled buffers have no owner | Any path may give an array back to the pool while a background write, or another reader, still uses it | ISS-129, ISS-105 |
| A statement's undo is best effort | Undo argued case by case (ISS-062, 089, 090, 091, 120); index keys dropped before the page changes | ISS-131, 133 |
| Errors swallowed on the read path | Untyped `catch { }` in the row readers and `GetRawData` | Corruption reads as an absent row (ISS-144, 143) |
| Rules kept by convention | The mutation hook is a settable delegate, called by hand at each write site; Nullable off; no analyzers; warnings don't fail the build | ISS-154; culture comparisons (ISS-139) and empty catches (ISS-144) pass the build |
| Size and history in the code | `IOService.RawPages.cs` 1,649 lines, `XmlEngine.Mutations.cs` 1,419; comments carry long issue histories; tests 0.59 lines per line of source (389 cases) | Hard to see the second copy; the tests guard what was fixed, not the round trip of every type |

## 5. Measured

- Source 17,241 lines of C# (the library and the CLI); tests 10,191; conformance 1,497; benchmarks 3,543.
- `Nullable` is disabled in the library and the tests. There is no `TreatWarningsAsErrors`, no analyzer
  configuration, no `.editorconfig` and no `Directory.Build.props`. The build has 0 warnings, checked by hand.
- **CI hasn't run on recent work.** `.github/workflows/tests.yml` runs the gate (Windows and Linux, net8.0 and
  net9.0, conformance, `read.mjs`, fresh fixtures, bench) on a push. `main` is 20 commits ahead of `origin/main`,
  so none of them has run there. The gate is run by hand, from scripts outside the repository.
- **Escape hatches in the library:** one `catch { }` (a rollback's wait, `IOService.cs:344`); untyped catches that
  swallow on the read path (ISS-144); 27 catch blocks that neither rethrow nor return, most of them typed and argued
  at the site; 20 uses of `dynamic`; one `Thread.Sleep` (the lock retry, argued). One test sleeps
  (`XodStressTests.cs:567`).
- The project's `AGENTS.md` said `xod2` had no remote. It has one (`origin`, `mhsallam/xod` on GitHub), and the
  line was corrected the same day.

## 6. What would make the rules enforce themselves

1. **One file store.** One class writes every data file, always as a `.tmp` renamed into place, with the mutation
   hook passed to its constructor rather than set. `Microsoft.CodeAnalysis.BannedApiAnalyzers` then bans `File.*`
   writes and write-mode `FileStream` everywhere else, with RS0030 as an error in the gate. This covers ISS-134 and
   ISS-154, and gives ISS-129 one place to own its buffers.
2. **One list of what a crash leaves**, read by the recovery, the password switch and the refusals. Add liveness
   for journals and pending switches: the context that owns one holds it open, so one nobody holds is a crash, and
   writes are refused until an open alone recovers it. This covers ISS-147, 148, 149 and 153.
3. **One reader of a stored value** (`StoredValue.Read`) for every reader, and one comparison (`IndexKeys`
   equality) for every match. Add a round-trip guard test in the gate, over every supported type, every encoding
   and every reader, asserting both that each value reads back as written and that the key built from the object
   equals the key built from the row. Refuse an unsupported type when its type loads. This covers ISS-139, 140,
   142, 146, and the same reader closes ISS-143 and 144.
4. **A cache contract.** A write of a type clears that type's cached items through the one write hook, and a page
   or index in memory is checked against its file (a length, a generation) before use. This covers ISS-138 and
   ISS-141.
5. **One delete by item code and one cascade walker**, plus the statement savepoint (`ISS-090`). This covers
   ISS-130, 131, 133, 135 and 136, and ISS-089.
6. **Keys created only with the database held alone**, and one key derivation per open. This covers ISS-150 and
   ISS-152.
7. **The gate in the repository.** One script runs it; the workflow and people call that same script. Add warnings
   as errors, with analyzers for culture-sensitive comparisons (CA1307, CA1309) and empty catches. This covers
   ISS-158.

## 7. Fix order, riskiest first

1. **Before any release: say what holds (ISS-157).** README and the package description claim crash atomicity and
   "ACID" that the code doesn't give. Cheap, and it decides what users trust.
2. **Loss on ordinary use, in one context.** ISS-138 (stale references after an update) and ISS-139 (queries by
   example) need no crash or second process to happen. Fix them through items 3 and 4 of section 6.
3. **Shared access and passwords.** ISS-147, 148, 149 and 150, through items 2 and 6 of section 6.
4. **Corruption by a race.** ISS-129, through item 1 of section 6, with its probe kept as a guard test.
5. **The rest of the write path** (item 5 of section 6) and **the type model** (ISS-140, 142, 143, 144, 146).
6. **XOD 1.x users** (ISS-151): before the migration guide's "drop-in" is published, test a 1.x key and digest
   reader against a real encrypted 2017 fixture, or change the guide.
7. **The gate and analyzers** (ISS-158), alongside the above, so that each structural fix is held by the build.

The open issues from before this review stay as filed. Most of them (the statement savepoint ISS-089, 090, 124
and 125, the marker issues ISS-119 and 126, ISS-092, 095, 121, 127 and 128) wait on work another session has
uncommitted in the same files.
