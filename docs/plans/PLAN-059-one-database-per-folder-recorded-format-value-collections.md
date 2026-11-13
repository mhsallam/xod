# PLAN-059: One database per folder against two opens at once, the database's recorded format, maps and lists that read back as written (TASK-071)

Status: **in the tree** (2026-11-13), not released: no version change, nothing pushed. `ISS-096`, `ISS-093` and
`ISS-097` fixed.
Issues: `ISS-096`, `ISS-093`, `ISS-097` (filed by the review of `TASK-063`,
[`PLAN-053`](PLAN-053-statement-undo-counters-stored-values-locks.md)). Found and fixed while doing it: a list of a
nullable type couldn't be stored, and an empty map read back as null (section 1.3). Found by its independent review
and fixed: the first version of the `ISS-096` fix didn't fix it (section 1.1), an update that changed only a map's
value to null or a list's order was dropped (1.3), a map of another type than `Dictionary<,>` couldn't be read
(1.3), the format came from the path rather than the database file and a record naming no format was read as one
(1.2). Found by a second independent review, of the folder hold, and fixed: the second version of the `ISS-096` fix
checked the folder after the fact, leaving the refused open's file and the folder refused for good, and recovered
before checking; and a context could lose its hold silently (predates; all in 1.1). Filed: `ISS-127`, `ISS-128`.

## 1. What was wrong, and why

1. **`ISS-096`, two opens creating different databases in one folder both went on.** *Cause:* a folder holds one
   database (PLAN-046 section 2), and every database file in it shares the folder's tables, pages, journals and
   markers. The one-database check (`DatabaseFiles.Resolve`) ran before the open's hold, and nothing kept another
   context from creating a database file between the check and the open's own creation; and the hold was the lock
   file of the open's own database file, which was also its proof that it had the database to itself before it
   recovered what a crash left. So:
   - two opens naming different new files in an empty folder both passed the check, each created its own file,
     and both wrote the tables;
   - an open naming a new file in a folder whose database another context was writing (the check having passed
     before that database existed) held its own lock alone, ran the folder's recovery as if it had the database to
     itself, undid that context's commit under way and dropped its indexes (its marker being held and behind), and
     only then found the other file. The first review reproduced both: the commit then reported success with its
     rows gone, and the index file was deleted.

   *The change:* the check is made under a hold that keeps every other creator out, and the hold covers the
   folder.
   - Every hold (a context's, shared or exclusive, a password change's, the migrator's) takes the folder's lock
     file `xod.lock` before the database file's, of the same kind (`IOService.OpenLockFile`, a `DatabaseHold`), so
     an open recovers only when no other context has any database of the folder open.
   - A new database's file is created only with the database held alone (`IOService.OpenAndRecover`), until the
     engine has written it (`ShareHoldOnceCreated`, at the end of the engine's constructor). An open that finds
     the folder held by another context takes the shared hold instead: it then opens the database another context
     created meanwhile, or is refused, creating nothing.
   - Every open checks the folder again once its hold is taken, before it recovers or creates anything
     (`DatabaseFiles.Recheck`); so does the migrator. Under any hold the answer stands, since no other context of
     this release can create a database file while it is held. A path naming a folder whose database another
     context created meanwhile is refused with that database's name ("open it again").

   So of two opens creating different files at once, one goes on and the other is refused, creating nothing, and an
   open the check refuses recovers nothing. The database file's lock stays: XOD 2.0.0 and 2.0.1 take only that one
   (with `LockDatabase`), and contexts of one database file still have to see them. Both are taken in one order, so
   two holders never wait on each other.

   *What changes for users:* the open that creates a new database holds it alone until its file is written, as an
   open does while it recovers what a crash left; another open meanwhile waits up to `LockRetryTimeoutMs`, then
   gets `DatabaseLockedException` (before, two first opens of one new database could both go on, each writing the
   database file). `xod.lock` stays in the folder, as the database file's lock file does (PLAN-046 section 9).
   The password change's tests, which check that nothing but the database's files is left in the folder, count it
   among them (the gate found every one of them failing on it), and the conformance fixture writers delete both
   lock files once their context is closed (`Repo.DropLockFiles`, PLAN-047 section 6.1), so a fixture written
   again isn't committed with them.

   *Found by the second review and fixed with it* (section 4): a context taking the database alone (a password
   change) let go of its shared hold first, and took it back only when the exclusive hold failed for another's
   lock or for access; any other failure (an `IOException` such as too many open files, or a network error) left
   it holding nothing, with no reason recorded, so it went on writing and its next password change ran as if alone
   (predates). Now it takes the shared hold back whatever failed (`WithDatabaseToItself`).

   *Not covered:* XOD 2.0.0 and 2.0.1 take no folder hold, so a process of theirs can still create a database
   file, or recover, beside one of this release (the release notes already say not to run them on a database a
   newer version has open). The check reads the folder's listing, which a network share's directory cache may
   give late to another host (not checked).
2. **`ISS-093`, a database wasn't written in the encoding it records.** *Cause:* the database file records the
   encoding the database was created in (`Format`, PLAN-046 section 5), and `XodMigrator` rewrites it, but nothing
   read it: a context took its encoding from `DatabaseOptions.StorageFormat` or, without options, from the
   extension. `StorageFormat` defaulted to XML, so options that didn't set it couldn't be told from options that
   chose XML. A `.xod` database converted to Binary in place (it keeps its name) was then written in XML, and a
   `.xodb` database given options without a format was XML from the start. Files stayed readable: each file's
   encoding is read from the file. *The change:* `DatabaseOptions` knows whether `StorageFormat` was set
   (`StorageFormatSet`). Set, it is the encoding written, and the record is left as it is (a folder may hold all
   three, PLAN-046 section 3). Unset, `XodContext` lets the engine choose: the encoding the database file records
   once it is open, before any table is read, else the one the database file's extension picks, which a new
   database's file then records (the engine writes it there at creation, as before).

   The review found two gaps in the first version, both fixed: the fallback read the extension of the path given,
   not of the database file it names (a folder named `shop.bin` got a Binary `data.xod`; a database opened through
   its folder fell back to XML whatever its file), and the record was read by `Enum.TryParse`, which takes `"1"` and
   `"Xml, Json"` for Json. Now the record is one of the three names in any case, or none.
3. **`ISS-097`, maps and lists didn't read back as written.** The stored-value rule of `ISS-060` (a value is left
   out only when a read gives it back; a null a read wouldn't give back is marked `nil="true"`) covered scalar
   properties, not what a collection holds. Four defects, one writer and two readers:
   - a map's null value was written as empty text, which reads back as `""` (for a string);
   - a list's or array's null item was left out;
   - a list or array of a nullable type (`List<int?>`) couldn't be stored: its items were named by the item type's
     short name, `Nullable`1`, which isn't an XML name (`XmlException` on insert; found doing this);
   - an empty map was left out, so it read back as null where an empty list read back empty (found doing this).

   *Cause:* the writer and the two row readers (the standalone one and the general one, each with its own copy of
   the map and list reader) wrote and read an entry's or item's text only. *The change:* a map entry's or list
   item's content is its text or, for a null, the mark a null property has (`StoredValue.ItemContent`); a reader
   takes a marked one as null (`StoredValue.ItemText`; a value type without a null reads its default, as a list's
   item always did); an item is named by `StoredValue.ItemName`, the item type's short name or a nullable type's
   underlying type's (`Int32` for `int?`), in the writer and the readers alike; and an empty map is stored. The two
   readers' copies are one now (`ReadStoredMap`, `ReadStoredScalarItems`). The independent reader
   (`conformance/format/read.mjs`) reads the mark too, and reads an item with no text as `""` (it read it as
   null); the stored-values fixture has a table of maps and lists (PLAN-047 section 6.1).

   The review found two more readers of these values, both fixed:
   - *An update's comparison of rows* (`XmlExtensions.ElementEquals`, which decides whether an update writes the
     row) looked only for the stored row's attributes in the new one, so an entry marked null equalled the stored
     entry with no text; and it matched each child with any equal one, so a list reordered, or with an item
     repeated in place of another, equalled the stored list. The update was dropped and reported made. Now both
     sides' attributes must match, and the children match in order. A row whose properties another version stored
     in another order only costs a write.
   - *A map of another type than `Dictionary<,>`*: every map was read as a `Dictionary<,>` of the property's own
     type arguments, which a `SortedDictionary`, a `Hashtable` or a class deriving from `Dictionary<,>` can't hold
     (it threw once the map held anything, and, with empty maps stored, at all). Now a map is read as the
     property's own type where it has a public parameterless constructor, with the key and value types of the
     `IDictionary<K, V>` it implements (strings for a `Hashtable`).

   *Not covered, filed:* a null collection or reference is still left out of the row, so it reads back as a new
   instance holds it (`ISS-127`; marking it touches the relation code that adds to a parent's list); a property
   declared as `IDictionary<K, V>` or `IReadOnlyDictionary<K, V>` isn't taken for a map at all, and is stored as a
   list of `"[k, v]"` strings that can't be read back (`ISS-128`, predates; the fix is in `PropertyService`).

   *Compatibility:* XOD 2.0.0 and 2.0.1 read a marked entry or item as its empty text (`""` for a string, null for
   a nullable type), and can't read a row that holds a list of a nullable type (`XName` refuses `Nullable`1`), or
   an empty map of another type than `Dictionary<,>`.

## 2. Tests

New:
- `XodSharedAccessTests.Two_opens_creating_different_databases_in_one_folder_don_t_both_go_on` (crash writer
  `open-second`, paused once it has checked the folder, fault step `open-checked`): the test creates another
  database in the folder and writes to it, its indexes left behind its pages (its marker held and marked), then
  lets the child go on. The child is refused, the refusal names the test's file, it created no database file,
  every file the test's database had is as it was, byte for byte, and the test's database opens again once closed.
- `XodSharedAccessTests.An_open_naming_a_second_database_doesn_t_undo_a_commit_under_way` (the review's first run):
  the crash writer's `open-second` checks an empty folder and pauses; another process creates a database there and
  pauses its commit once its files are written (`commit-written`); the opener goes on and is refused, creating no
  file; the commit reports success, and its rows are there.
- `XodSharedAccessTests.An_open_creating_a_database_holds_its_folder_until_its_file_is_written` (`open-second`
  paused at the fault step `creating`, its check passed, its file not yet written): an open naming another new file,
  and one naming the same, are refused (`DatabaseLockedException`) and create nothing; the child then goes on, and
  its database is the folder's one.
- `XodSharedAccessTests.An_open_the_folder_refuses_recovers_nothing` (the second review's run): a commit's journal
  in the folder (its process crashed) while the test holds the database file's lock as XOD 2.0.1 does with
  `LockDatabase`; the paused `open-second` goes on, is refused, and the journal is still there; the database's own
  next open recovers it.
- `XodSharedAccessTests.A_new_database_file_isn_t_created_while_another_context_holds_the_folder`: a context holds
  the folder while its database file is gone (deleted while open); an open naming a new file is refused, creating
  nothing.
- `XodSharedAccessTests.A_context_whose_hold_alone_fails_holds_the_database_again` (crash writer `pwchange-hold`,
  `XOD_FAIL_AT_STEP=hold-alone`: an `IOException` once an exclusive hold has the folder's lock): after the failed
  password change another context opens the database, one with `LockDatabase` is refused, and the child writes.
- `XodMigratorSafetyTests.A_conversion_checks_the_folder_again_under_its_hold` (crash writer `convert`, paused at
  `open-checked`): a copy of the database file put in the folder meanwhile; the conversion is refused under its
  hold, converting nothing.
- `XodMigrationTests.A_converted_database_is_written_in_the_format_it_was_converted_to` (to Binary into another
  folder; with no options, options without a format, and a format given, which leaves the record),
  `A_database_converted_in_place_stays_in_its_new_format`, `A_new_database_takes_its_extension_s_format_when_none_is_given`
  (and the next context takes it from the record), `The_database_file_s_extension_picks_the_format_not_the_path_s`,
  `A_record_that_names_no_format_leaves_it_to_the_extension` (`"1"`, `"Xml, Json"`),
  `A_database_is_written_as_its_record_says_whatever_its_extension` (a `.xodb` that records Xml, through XML's own
  paths). Each reads the encoding from the first bytes of the page the context wrote.
- `XodStoredValueTests.Value_collections_read_back_as_written` (XML, JSON, Binary, XML with a password; a table
  read by the standalone row reader and one read by the general one, each read by `Find` and `Select` after a
  reopen), `A_null_entry_or_item_is_marked_and_a_nullable_item_is_named_by_its_type` (the stored form),
  `An_update_of_what_a_map_or_list_holds_is_written` (each update changes one thing: a map's value to null, a list's
  order, an item repeated in place of another; both readers, every format),
  `Maps_of_other_types_read_back_as_their_own_type` (`SortedDictionary`, `Hashtable`, a class deriving from
  `Dictionary<,>`; with entries and empty; every format).
- The conformance fixture `stored-values` gains `BlankCollections`; `read.mjs --check` and `--check-fresh` read it.

Each new test was red against the code before its change (the breaks below put that code back).

## 3. Each guard broken

Each guard broken alone, the affected classes run on net9.0 (the crash writer rebuilt against the broken library
where it runs one), every file restored and checked by hash afterwards.

| Break | Guard broken | Result |
|---|---|---|
| C1 | a new database created under the shared hold (as the second version did) | red: `An_open_creating_a_database_holds_its_folder_until_its_file_is_written`, `A_new_database_file_isn_t_created_while_another_context_holds_the_folder` |
| C2 | no check under the hold | red: `Two_opens_creating_different_databases_in_one_folder_don_t_both_go_on`, `An_open_naming_a_second_database_doesn_t_undo_a_commit_under_way`, `An_open_the_folder_refuses_recovers_nothing` |
| C3 | the check after the recovery | red: `An_open_the_folder_refuses_recovers_nothing` |
| C4 | the shared hold taken back only after another's lock or a refused access | red: `A_context_whose_hold_alone_fails_holds_the_database_again` |
| C5 | the folder's lock kept when the database file's fails | red: `A_context_whose_hold_alone_fails_holds_the_database_again`, `An_open_that_cannot_take_a_hold_recovers_nothing_and_is_refused_while_something_is_pending` |
| C6 | the conversion not checked under its hold | red: `A_conversion_checks_the_folder_again_under_its_hold` |
| C7 | a new file created under a shared hold when the check finds no database file | red: `A_new_database_file_isn_t_created_while_another_context_holds_the_folder` |
| C8 | the hold kept alone after the creation | red: `Two_opens_creating_different_databases_in_one_folder_don_t_both_go_on` |
| C9 | no folder lock (each database file's lock alone) | red: `An_open_creating_a_database_holds_its_folder_until_its_file_is_written`, `A_new_database_file_isn_t_created_while_another_context_holds_the_folder` |
| B2 | the recorded format not taken | red: `A_converted_database_is_written_in_the_format_it_was_converted_to`, `A_database_converted_in_place_stays_in_its_new_format` |
| B3 | options without a format count as a format given | red: `A_converted_database_is_written_in_the_format_it_was_converted_to`, `A_new_database_takes_its_extension_s_format_when_none_is_given` |
| B3a | a new database file records XML whatever its encoding (the line that records it removed) | red: `A_new_database_takes_its_extension_s_format_when_none_is_given` (the next context wrote XML) |
| B3b | the format from the path given, not the database file | red: `The_database_file_s_extension_picks_the_format_not_the_path_s` |
| B3c | the record read by `Enum.TryParse` | red: `A_record_that_names_no_format_leaves_it_to_the_extension` (both cases) |
| B4 | an item named by its own type (`Nullable`1`) | red: both collection tests, every format |
| B5 | a null item left out | red: both collection tests, every format |
| B6 | a map's null value written as empty text | red: both collection tests, every format |
| B7 | an empty map left out | red: `Value_collections_read_back_as_written`, every format |
| B8 | the map reader ignores the mark | red: `Value_collections_read_back_as_written`, every format |
| B9 | the list reader ignores the mark | red: `Value_collections_read_back_as_written`, every format |
| B10 | the standalone row reader with its own map reader again (the mark ignored) | red: `Value_collections_read_back_as_written`, every format |
| B11 | the general row reader with its own map reader again | red: `Value_collections_read_back_as_written`, every format |
| B12 | `read.mjs` ignores the mark | `--check` fails on `stored-values` |
| B13 | `read.mjs` reads an item with no text as missing | `--check` fails on `stored-values` |
| B14 | an update's comparison looks for one side's attributes only | red: `An_update_of_what_a_map_or_list_holds_is_written`, every format |
| B15 | an update's comparison matches each child with any equal one | red: `An_update_of_what_a_map_or_list_holds_is_written`, every format |
| B16 | every map read as `Dictionary<,>` | red: `Maps_of_other_types_read_back_as_their_own_type`, every format |

B14 and B15 were green against the first version of the update test, which changed three properties in one
update: any change makes the whole row differ. Each update now changes one thing.

The `ISS-096` guards of the second version were each red against its tests then: no second check once the file
was on disk (the child went on), and no folder lock (the index file gone; 25 of 50 customers, the commit undone).
The first is gone with this design; the folder lock is C9. With the check made under the hold, before any
recovery, the two open-and-commit tests no longer rely on the folder lock alone (C2 is red on both).

The second review's race probe (two opens creating `a.xod` and `b.xod` in a fresh folder, released together, 300
times; run in the copy, not kept): against this code one went on and the other got `DatabaseLockedException` 300
times, no refused open left a file, and every folder opened again. Against the second version it left all 300
folders refusing every open (both opens refused 222 times); without any second check both went on 130 times.

## 4. The independent reviews

### 4.1 The first

A fresh agent got the diff and the three issues as filed, the four questions, and no conclusion; it exported HEAD,
applied the diff, and ran programs against it outside the repository. Its findings, each checked here before
acting:

| Finding | What it is | Done |
|---|---|---|
| F1 (high) | `ISS-096` not fixed: the refused open ran the folder's recovery first, as if alone (it held its own file's lock); reproduced undoing a commit under way, and deleting an index file | fixed (section 1.1): every hold covers the folder (C9); and, after the second review (4.2), the check made under it before any recovery (C2, C3); two tests |
| F2 (medium) | an update that only marks a map's value null is dropped and reported made (`ElementAttributesEquals` one way) | fixed (section 1.3); B14 |
| F3 (low-medium) | an empty map of another type than `Dictionary<,>` stored now can't be read (any such map with entries never could) | fixed (section 1.3); B16 |
| F4 (low, predates) | an update that only reorders a list is dropped (`ElementEquals` matched children with any equal one); with null items stored, also moving a null | fixed (section 1.3); B15 |
| F5 (low) | the format fallback reads the path's extension, not the database file's; the docs claimed the file's | fixed (section 1.2); B3b |
| F6 (low) | the record is read leniently (`"1"`, `"Xml, Json"` taken for Json) | fixed (section 1.2); B3c |
| F7 (low) | without a format, `XodContext` builds an `XmlEngine` for a `.xodj`/`.xodb` path (the `Engine` it reports was `JsonEngine`/`BinaryEngine`; the format reported is right); `JsonEngine` and `BinaryEngine` set the format on the caller's options object, so a reused instance forces it | noted: the engine classes differ only in that setting; the options side effect predates |
| tests | the open test couldn't see the child's recovery writes, nor the commit-under-way order; the format tests lacked a `.xodb` recording Xml, a record missing or invalid; nothing updated a map or list, or read another map type | new tests (section 2) |
| docs | the comments and this plan claimed `ISS-096` fixed; PLAN-046 section 2 said the extension mattered only for a new database | corrected |

The review also confirmed, by running on .NET 8 and 9: `XElement` and `XName` refuse ``Nullable`1``; an empty entry
serializes as `<Entry key="e"></Entry>`; a nullable type's `FullName` names the runtime's version (PLAN-046's
note); `read.mjs --check-fresh` reads the fixture. It found no other reader or writer of the record, the entries
or the items, and one other check-then-hold-then-recover path, `XodMigrator`, which now holds the folder too.

### 4.2 The second, of the folder hold

The folder hold is concurrency code that the first review hadn't seen, so a second fresh agent got the code diff,
the two defects as reported, the constraints (2.0.x processes, Windows and Unix), the four questions and no
conclusion; it ran probes in its own copy. Its findings, each checked here before acting:

| Finding | What it is | Done |
|---|---|---|
| S1 (medium) | `ISS-096` detected after the fact: the check ran outside any hold that excluded the other creator, so a race left the refused open's file, and the folder refused every open after, the winner's included (300 of 300 folders in its probe) | fixed (section 1.1): a database file created only with the folder held alone, the check under the hold; C1, C7, C9, two tests, the probe |
| S2 (low-medium) | an open the check would refuse ran the folder's recovery first, beside a 2.0.1 context holding the other database file's lock (reproduced: a crashed commit's journal recovered under it); the comment said it couldn't | fixed: the check under every hold, before any recovery; C2, C3, a test |
| S3 (low-medium, predates) | `WithDatabaseToItself` took the shared hold back only after another's lock or a refused access; any other `IOException` left the context holding nothing, writing on | fixed (section 1.1); C4, C5, a test |
| S4 (low) | the migrator checked the folder before its hold only, and converted every file in it | fixed: checked again under its hold; C6, a test |
| S5 (low) | `KNOWN-ISSUES` and the release notes not updated | not so: the review saw the code's diff only; the shared docs are staged on their own (now updated for this design) |
| tests | nothing tested a 2.0.x-style holder against the two-file hold, the outcome where both opens are refused, nor the folder's lock let go after a failed hold off Windows (only a Windows access-control test caught it) | `An_open_the_folder_refuses_recovers_nothing`, `A_context_whose_hold_alone_fails_holds_the_database_again` (any system); both refused can't happen now (the probe) |

It also confirmed, from the runtime's source for .NET 8 and 9 (`SafeFileHandle.Unix.cs`; the official `FileShare`
page describes Windows only): on Unix `FileShare.None` takes `LOCK_EX` and any other sharing `LOCK_SH`, without
waiting; a lock refused for another's is the only one reported, any other failure leaving the file open unlocked
(hence `ProveExclusive`). It found no deadlock (one taker, one order, waits bounded), no lock left held on a
failure path, and every hold taken through `OpenLockFile`.

## 5. Verdicts

| Change | Verdict |
|---|---|
| `ISS-096` | clean for contexts of this release; named limits: XOD 2.0.x processes take no folder hold, and a network share's listing across hosts isn't checked |
| a hold taken back whatever failed (`WithDatabaseToItself`) | clean |
| the migrator's check under its hold | clean |
| `ISS-093` | clean |
| `ISS-097` | clean for what a map or list holds, through an insert, an update and a read; a null collection or reference filed (`ISS-127`), an interface-typed map filed (`ISS-128`) |
| the two row readers' map and list readers made one | clean |
| an update's comparison of rows | clean; a row stored by another version in another property order costs a write |
| `read.mjs` and the fixture | clean |

## 6. Gate

Run 2026-11-13 on a copy of HEAD with this task's lines only (other work in the tree left out), built clean:
- the library, Release: no warnings;
- `Xod.Tests`: 389 of 389 on net8.0, and on net9.0; and 389 of 389 on Linux (the .NET 9 SDK container, no
  network, a user other than root);
- `Xod.Conformance`: 14 of 14 on both;
- `read.mjs --check`: every fixture matches (`stored-values`: 8 rows, 12 index keys); `--check-fresh` on the
  fixtures the tree writes: every one matches, with no lock file left in them;
- the bench's tests: 24 of 26 in the copy; the two that need a git checkout (the harness finds its checkout,
  git ignores the machine records) pass in the checkout, 26 of 26.

Earlier runs: the second version passed it (384 of 384, and on Linux) before its second review. The run before
that failed every password-change test (18, on both runtimes) on `xod.lock`, left in the folder as it should be,
which those tests didn't count among the database's files (section 1.1).
